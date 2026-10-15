// wai-embed.exe: Multimodel embedding engine on llama.cpp as a process.
// Supports EmbeddingGemma 2, BGE-M3, Perplexity Embed v2 and custom GGUF models.
// JSON-lines protocol of worker/wai_worker.py:
// one request object per stdin line, one reply object per stdout line. Logs go to stderr.

using System.Text.Json;
using WAI.Embed;

internal static class Program
{
    private const int VectorVersion = 1;
    private const string BuildTag = "b11461";
    private static string _activeRevision = "bfcd298762cc34d0357ece5ebdd31791a3a374d8";
    private static string _activeDtype = "text-q8_0+mmproj-q8_0";
    private static readonly int[] Budgets = [70, 140, 280, 560, 1120];

    private static Embedder? _embedder;
    private static bool _textOnly;
    private static string _activeModelName = "google/embeddinggemma-2";
    private static string _activeTextFile = "embeddinggemma-2-Q8_0.gguf";
    private static string? _activeMediaFile = "mmproj-embeddinggemma-2-Q8_0.gguf";

    private static int Main(string[] args)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _textOnly = args.Contains("--text-only");

        string backend = "auto";
        int bIdx = Array.IndexOf(args, "--backend");
        if (bIdx >= 0 && bIdx + 1 < args.Length)
        {
            backend = args[bIdx + 1].ToLowerInvariant().Trim();
        }
        bool forceCpu = backend == "cpu" || args.Contains("--cpu");

        string modelKey = "gemma2";
        int mIdx = Array.IndexOf(args, "--model");
        if (mIdx >= 0 && mIdx + 1 < args.Length)
        {
            modelKey = args[mIdx + 1].Trim();
        }

        bool background = args.Contains("--background");
        int threads = 0;
        int tIdx = Array.IndexOf(args, "--threads");
        if (tIdx >= 0 && tIdx + 1 < args.Length && int.TryParse(args[tIdx + 1], out int t))
        {
            threads = Math.Max(1, t);
        }

        if (background && Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
            }
            catch { }
        }

        ConfigureModel(modelKey);

        if (!FindModelFiles(_activeTextFile, _activeMediaFile, out string? textPath, out string? mediaPath) || textPath is null)
        {
            Emit(new Dictionary<string, object?> { ["ready"] = false, ["error"] = MissingModelsMessage(_activeTextFile) });
            return 1;
        }

        if (!_textOnly && mediaPath is null)
        {
            _textOnly = true; // Gracefully fallback to text-only if media projector is absent
        }

        try
        {
            _embedder = new Embedder(textPath, mediaPath, _textOnly, forceCpu, threads, background);
        }
        catch (DllNotFoundException ex)
        {
            Emit(new Dictionary<string, object?> { ["ready"] = false, ["error"] = NativeError(ex) });
            return 1;
        }
        catch (BadImageFormatException)
        {
            Emit(new Dictionary<string, object?> { ["ready"] = false,
                ["error"] = "llama.dll or mtmd.dll has the wrong CPU type: use windows\\native\\x64 on Intel/AMD and windows\\native\\arm64 on Snapdragon." });
            return 1;
        }
        catch (Exception ex)
        {
            Emit(new Dictionary<string, object?> { ["ready"] = false, ["error"] = Chop(ex.Message, 500) });
            return 1;
        }

        var e = _embedder;
        Log($"ready in {e.LoadSeconds} s ({(_textOnly ? "text-only" : "full")}, model={_activeModelName}, dim={e.Dim}, backend={e.ActiveBackend}, llama.cpp {BuildTag}, vectors {VectorVersion})");
        Emit(new Dictionary<string, object?> { ["ready"] = true, ["info"] = Info(e) });

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            line = line.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (Exception ex)
            {
                Emit(new Dictionary<string, object?> { ["id"] = null, ["ok"] = false, ["error"] = $"JSON parse error: {ex.Message}" });
                continue;
            }
            using (doc)
            {
                var req = doc.RootElement;
                string id = req.TryGetProperty("id", out var idProp) ? idProp.GetRawText() : "null";
                string? op = req.TryGetProperty("op", out var opProp) ? opProp.GetString() : null;
                string[] inputs = req.TryGetProperty("inputs", out var inProp) && inProp.ValueKind == JsonValueKind.Array
                    ? inProp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!).ToArray()
                    : [];
                try
                {
                    switch (op)
                    {
                        case "quit":
                            Emit(new Dictionary<string, object?> { ["id"] = IdOf(id), ["ok"] = true });
                            _embedder.Dispose();
                            return 0;
                        case "info":
                            Emit(new Dictionary<string, object?> { ["id"] = IdOf(id), ["ok"] = true, ["info"] = Info(e) });
                            break;
                        case "text":
                            ReplyVectors(id, e.EmbedTexts(inputs).Select(v => ((float[]?)v, (string?)null)).ToArray(), e.Dim);
                            break;
                        case "image":
                            int budget = req.TryGetProperty("budget", out var bProp) && bProp.TryGetInt32(out var b)
                                ? b : 280;
                            if (!Budgets.Contains(budget))
                                throw new InvalidOperationException($"budget must be one of {string.Join(",", Budgets)}");
                            ReplyVectors(id, inputs.Select(p => e.EmbedImageFile(p, budget)).ToArray(), e.Dim);
                            break;
                        case "audio":
                            ReplyVectors(id, inputs.Select(p => e.EmbedAudioFile(p, 16000)).ToArray(), e.Dim);
                            break;
                        default:
                            throw new InvalidOperationException($"unknown op {op ?? "(none)"}");
                    }
                }
                catch (Exception ex)
                {
                    Emit(new Dictionary<string, object?>
                        { ["id"] = IdOf(id), ["ok"] = false, ["error"] = Chop(ex.Message, 500) });
                }
            }
        }
        _embedder.Dispose();
        return 0;
    }

    private static void ConfigureModel(string modelKey)
    {
        switch (modelKey.ToLowerInvariant())
        {
            case "bge-m3":
            case "bge":
                _activeModelName = "BAAI/bge-m3";
                _activeRevision = "2d48f1737679ad900d5c26c5aad5410e9c70fdca";
                _activeDtype = "text-q4_k_m";
                _activeTextFile = "bge-m3-Q4_K_M.gguf";
                _activeMediaFile = null;
                _textOnly = true;
                break;
            case "pplx-v2":
            case "perplexity":
            case "pplx":
                _activeModelName = "perplexity-ai/pplx-embed-v2";
                _activeRevision = "07ebf344332880f14f72673df5d41549826872f7";
                _activeDtype = "text-q8_0";
                _activeTextFile = "pplx-late-Q8_0.gguf";
                _activeMediaFile = null;
                _textOnly = true;
                break;
            case "nomic":
            case "nomic-embed":
                _activeModelName = "nomic-ai/nomic-embed-text-v1.5";
                _activeRevision = "0188c9bf409793f810680a5a431e7b899c46104c";
                _activeDtype = "text-q8_0";
                _activeTextFile = "nomic-embed-text-v1.5.Q8_0.gguf";
                _activeMediaFile = null;
                _textOnly = true;
                break;
            case "gemma2":
            default:
                if (File.Exists(modelKey))
                {
                    _activeModelName = Path.GetFileNameWithoutExtension(modelKey);
                    using var stream = File.OpenRead(modelKey);
                    _activeRevision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                    _activeDtype = "custom-gguf";
                    _activeTextFile = modelKey;
                    _activeMediaFile = null;
                }
                else
                {
                    _activeModelName = "google/embeddinggemma-2";
                    _activeTextFile = "embeddinggemma-2-Q8_0.gguf";
                    _activeMediaFile = "mmproj-embeddinggemma-2-Q8_0.gguf";
                }
                break;
        }
    }

    private static void ReplyVectors(string id, (float[]? Vector, string? Error)[] items, int dim)
    {
        var flat = new float[items.Length * dim];
        var errors = new List<object?>(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Vector is not null)
            {
                if (items[i].Vector!.Length != dim) { errors.Add($"bad vector ({items[i].Vector!.Length}, want {dim})"); continue; }
                Array.Copy(items[i].Vector!, 0, flat, i * dim, dim);
            }
            errors.Add(items[i].Error is null ? null : (object)items[i].Error!);
        }
        var bytes = new byte[flat.Length * sizeof(float)];
        Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
        Emit(new Dictionary<string, object?>
        {
            ["id"] = IdOf(id),
            ["ok"] = true,
            ["dim"] = dim,
            ["count"] = items.Length,
            ["vectors"] = Convert.ToBase64String(bytes),
            ["errors"] = errors,
        });
    }

    private static Dictionary<string, object?> Info(Embedder e) => new()
    {
        ["model"] = _activeModelName,
        ["revision"] = _activeRevision,
        ["runtime"] = $"llama.cpp-vectors{VectorVersion}",
        ["dtype"] = _activeDtype,
        ["dim"] = e.Dim,
        ["text_only"] = _textOnly,
        ["load_seconds"] = e.LoadSeconds,
        ["backend"] = e.ActiveBackend,
        ["device"] = e.ActiveDevice,
    };

    private static string NativeError(DllNotFoundException ex) =>
        "llama.dll or mtmd.dll was not found (tried next to wai-embed.exe and on PATH)."
        + " Build them with: powershell -File windows\\scripts\\build-llama-windows.ps1"
        + $" ({ex.Message})";

    private static bool FindModelFiles(string textFile, string? mediaFile, out string? text, out string? media)
    {
        text = media = null;
        if (File.Exists(textFile))
        {
            text = textFile;
            if (mediaFile is not null && File.Exists(mediaFile)) media = mediaFile;
            return true;
        }

        var dirs = new List<string>();
        string? chosen = Environment.GetEnvironmentVariable("WAI_MODELS");
        if (!string.IsNullOrEmpty(chosen)) dirs.Add(chosen);

        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI", "Models"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "models"));
        dirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "models"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface"));

        // Fallbacks for BGE-M3 filenames
        string[] textCandidates = textFile.Contains("bge-m3")
            ? [textFile, "bge-m3-Q4_K_M.gguf", "bge-m3-Q8_0.gguf", "bge-m3-FP16.gguf"]
            : [textFile];

        foreach (string d in dirs)
        {
            try
            {
                foreach (string candidate in textCandidates)
                {
                    string t = Path.Combine(d, candidate);
                    if (File.Exists(t))
                    {
                        text = t;
                        if (mediaFile is not null)
                        {
                            string m = Path.Combine(d, mediaFile);
                            if (File.Exists(m)) media = m;
                        }
                        return true;
                    }
                }
            }
            catch { }
        }
        return false;
    }

    private static string MissingModelsMessage(string textFile)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI", "Models");
        return $"no model file ({textFile}) found in {local} or models directory."
            + " Place the GGUF file there or set WAI_MODELS.";
    }

    private static void Emit(Dictionary<string, object?> reply)
    {
        Console.WriteLine(JsonSerializer.Serialize(reply));
        Console.Out.Flush();
    }

    private static object IdOf(string raw)
    {
        try { return JsonDocument.Parse(raw).RootElement.Clone(); }
        catch { return raw; }
    }
    private static void Log(string message) => Console.Error.WriteLine($"[wai-embed] {message}");
    private static string Chop(string text, int max) => text.Length <= max ? text : text[..max];
}
