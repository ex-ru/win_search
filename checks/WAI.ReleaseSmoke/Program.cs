using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WAI.Core;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Check(string Name, string Status, string Detail, double Seconds);
    private static readonly List<Check> Checks = new();

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--crash-child") return CrashChild(args[1]);
        string? Value(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string output = Path.GetFullPath(Value("--report") ?? "release-smoke.json");
        string? package = Value("--package");
        string? models = Value("--models");
        string model = Value("--model") ?? "bge-m3";
        string? manifest = Value("--eval");
        bool native = args.Contains("--native");
        string root = Path.Combine(Path.GetTempPath(), "WAI-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Run("arguments", () =>
        {
            foreach (string key in new[] { "--report", "--package", "--models", "--model", "--eval" })
                if (args.Contains(key)) Require(Value(key) is { Length: > 0 } value && !value.StartsWith("--"), $"Missing value for {key}.");
            Require(manifest is null || models is not null, "--eval requires --models and --package; evaluation must use actual local model vectors.");
            return "Explicit dependency options validated.";
        });
        Run("file-lifecycle-lexical", () => FileLifecycle(Path.Combine(root, "lexical"), null, package, false));
        Run("crash-during-transaction", () => CrashRecovery(Path.Combine(root, "crash")));
        Run("sqlite-disk-full", () => DiskFull(Path.Combine(root, "full")));
        WorkerConfig? evaluationConfig = null;
        if (models is not null)
        {
            Run("packaged-worker-cpu", () =>
            {
                Require(package is not null, "--models requires --package with the actual shipped worker.");
                string exe = Path.Combine(Path.GetFullPath(package!), "wai-embed.exe");
                Require(File.Exists(exe), $"Missing packaged worker: {exe}");
                Require(Directory.Exists(models), $"Missing local model directory: {models}");
                var config = new WorkerConfig(exe, ["--model", model, "--cpu"],
                    Environment: new() { ["WAI_MODELS"] = Path.GetFullPath(models) }, TextOnly: true,
                    RequestTimeout: TimeSpan.FromMinutes(2), StartupTimeout: TimeSpan.FromMinutes(3));
                evaluationConfig = config;
                using (var worker = new WorkerClient(config))
                {
                    float[] vector = worker.EmbedTexts([Prompts.Query("Проверка поиска документов")])[0];
                    Require(vector.Length == worker.Info.Dim && vector.All(float.IsFinite), "Invalid CPU embedding.");
                    Require(vector.Sum(x => (double)x * x) is > 0.99 and < 1.01, "CPU vector is not normalized.");
                }
                FileLifecycle(Path.Combine(root, "semantic"), config, package, native);
                return $"Actual packaged worker launched with --cpu, model={model}; embeddings, hybrid search, update, rename, deletion and reopen passed. GPU hardware matrix is not tested.";
            });
        }
        else Skip("packaged-worker-cpu", "Not requested: supply --package and --models. Release script requires both.");
        if (manifest is not null) Run("production-evaluation", () =>
        {
            Require(evaluationConfig is not null, "Evaluation requires an actual packaged worker and local models.");
            string evalRoot = Path.Combine(root, "eval");
            try { Evaluate(evalRoot, evaluationConfig!, manifest, package); }
            finally
            {
                string detailedReport = Path.Combine(evalRoot, "eval-results.json");
                if (File.Exists(detailedReport))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(detailedReport, Path.Combine(Path.GetDirectoryName(output)!, "eval-results.json"), overwrite: true);
                }
            }
            return "Frozen corpus evaluated with actual model vectors; all required per-query assertions passed. This does not establish real-world accuracy.";
        });
        if (native) Run("native-pdfium-ffmpeg", () => NativeRuntimes(Path.Combine(root, "native"), package));
        else Skip("native-pdfium-ffmpeg", "Not requested: supply --native to require PDFium rendering and actual FFmpeg audio/video decoding.");
        string status = Checks.Any(c => c.Status == "failed") ? "failed" : "passed";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string corePath = typeof(IndexStore).Assembly.Location;
        File.WriteAllText(output, JsonSerializer.Serialize(new { status, startedAtUtc = started, finishedAtUtc = DateTimeOffset.UtcNow,
            loadedCoreAssembly = corePath, loadedCoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath))),
            loadedRuntimeAssembly = typeof(object).Assembly.Location,
            isolatedWorkspace = root, checks = Checks }, Json) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"{status}: {output}");
        // Preserve this uniquely created workspace as reproducible evidence; no user index or user folders are opened.
        return status == "passed" ? 0 : 1;
    }

    private static void Run(string name, Func<string> action)
    {
        var timer = Stopwatch.StartNew();
        try { Checks.Add(new(name, "passed", action(), timer.Elapsed.TotalSeconds)); }
        catch (Exception error) { Checks.Add(new(name, "failed", error.ToString(), timer.Elapsed.TotalSeconds)); }
        Console.WriteLine($"{Checks[^1].Status}: {name}");
    }
    private static void Skip(string name, string why) => Checks.Add(new(name, "skipped", why, 0));
    private static void Require(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }

    private static string FileLifecycle(string root, WorkerConfig? worker, string? package, bool withPdf)
    {
        string documents = Path.Combine(root, "documents"), index = Path.Combine(root, "index");
        Directory.CreateDirectory(documents);
        string invoice = Path.Combine(documents, "invoice.txt"), backup = Path.Combine(documents, "backup.md");
        File.WriteAllText(invoice, "Договор поставки. Счет 88392. Поставка медицинского оборудования. Уникальная отметка Amberwood.");
        File.WriteAllText(backup, "Восстановление рабочих документов после поломки диска. Резервная копия хранится на отдельном носителе. Уникальная отметка Silverbrook.");
        if (withPdf) WritePdf(Path.Combine(documents, "native.pdf"));
        using (var store = new IndexStore(index))
        using (var indexer = MakeIndexer(store, worker, package))
        {
            Require(indexer.Sync([documents]).Added == (withPdf ? 3 : 2), "Crawler did not discover all fixture files.");
            Index(indexer);
            Require(store.FilesUnder([documents]).All(f => f.State == FileState.Done), "Extraction/indexing did not finish successfully.");
            AssertHit(store, worker, "88392", "invoice.txt");
            AssertHit(store, worker, "Silverbrook", "backup.md");
            if (worker is not null) AssertHit(store, worker, "Как восстановить документы при выходе диска из строя?", "backup.md", hybrid: true);
            File.WriteAllText(invoice, "Договор поставки обновлен. Счет 99173. Медицинское оборудование доставлено. Новая отметка Copperfield.");
            File.SetLastWriteTimeUtc(invoice, DateTime.UtcNow.AddSeconds(5));
            Require(indexer.Sync([documents]).Changed == 1, "Modified file was not requeued.");
            Index(indexer);
            AssertHit(store, worker, "99173", "invoice.txt");
            using (var searcher = new Searcher(store, loadVectors: false)) Require(searcher.Search("Amberwood", null).Count == 0, "Old text survived reindexing.");
            string renamed = Path.Combine(documents, "renamed-invoice.txt");
            File.Move(invoice, renamed);
            var renamedSync = indexer.Sync([documents]);
            Require(renamedSync.Moved == 1 || (renamedSync.Added == 1 && renamedSync.Removed == 1), "Rename was not reconciled.");
            Index(indexer);
            AssertHit(store, worker, "99173", "renamed-invoice.txt");
            Require(store.FilesUnder([documents]).All(f => !f.Path.EndsWith("/invoice.txt", StringComparison.OrdinalIgnoreCase)), "Old path survived rename.");
            File.Delete(backup);
            Require(indexer.Sync([documents]).Removed == 1, "Deletion was not detected.");
            using var afterDelete = new Searcher(store, loadVectors: false);
            Require(afterDelete.Search("Silverbrook", null).Count == 0, "Deleted file remained searchable.");
        }
        using (var reopened = new IndexStore(index))
        {
            AssertHit(reopened, worker, "99173", "renamed-invoice.txt", hybrid: worker is not null);
            Require(reopened.PendingCount() == 0, "Unexpected pending files after reopen.");
            Integrity(reopened);
        }
        return "Real crawler → DefaultExtractor → Indexer → SQLite → Searcher; change, rename, delete, dispose/reopen and integrity verified.";
    }

    private static Indexer MakeIndexer(IndexStore store, WorkerConfig? worker, string? package)
    {
        var options = new IndexOptions();
        var extractor = new DefaultExtractor(options, new WicImageDecoder(), new CompositeOcrEngine(),
            new PdfiumEngine(Find(package, "pdfium.dll")), new(Find(package, "ffmpeg.exe"), Find(package, "ffprobe.exe")));
        return new Indexer(store, options, new Crawler(), () => worker is null ? new WordsOnly() : new WorkerClient(worker), extractor) { WorkerCount = 1, PrepareDegree = 1, ResetOnModelChange = (_, _) => { } };
    }
    private static void Index(Indexer indexer)
    {
        var states = indexer.Run();
        Require(!states.ContainsKey(FileState.Failed), "Indexer reported failed extraction: " + JsonSerializer.Serialize(states));
        indexer.FinishLongFiles();
    }
    private static void AssertHit(IndexStore store, WorkerConfig? config, string query, string filename, bool hybrid = false)
    {
        using var searcher = new Searcher(store, loadVectors: hybrid);
        float[]? vector = null;
        if (hybrid)
        {
            Require(config is not null, "Hybrid assertion requires real worker.");
            using var worker = new WorkerClient(config!);
            vector = worker.EmbedTexts([Prompts.Query(query)])[0];
            Require(searcher.VectorCount > 0 && searcher.VectorSource == worker.Info.VectorSource, "Index has no matching real vectors.");
        }
        Require(searcher.Search(query, vector, limit: 5).Any(hit => Path.GetFileName(hit.Path) == filename), $"Expected {filename} in top-5 for '{query}'.");
    }

    private static int CrashChild(string root)
    {
        using var store = new IndexStore(root);
        string fixture = Path.Combine(root, "crashfixture.txt");
        File.WriteAllText(fixture, "CRASHUNCOMMITTED. This indexed document must roll back with its FTS rows.");
        var candidate = new Crawler().Crawl([root], new IndexOptions()).Candidates.Single(c => c.Path.EndsWith("crashfixture.txt", StringComparison.Ordinal));
        store.Db.Transaction(() =>
        {
            store.SetMeta("uncommitted", new string('x', 2_000_000));
            long id = store.Insert(candidate);
            store.AddSegment(id, SegmentKind.Chunk, Modality.Text, text: DocExtractor.Text(fixture));
            store.Finish(id, FileState.Done);
            Console.WriteLine("transaction-open"); Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        });
        return 99;
    }
    private static string CrashRecovery(string root)
    {
        using (var store = new IndexStore(root)) store.SetMeta("committed", "retained");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--crash-child"); start.ArgumentList.Add(root);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start crash child.");
        try
        {
            string? ready = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Require(ready == "transaction-open", "Child did not reach an active transaction.");
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(10000); }
        using var recovered = new IndexStore(root);
        Require(recovered.Meta("committed") == "retained", "Committed data lost after process kill.");
        Require(recovered.Meta("uncommitted") is null, "Uncommitted data survived process kill.");
        Require(recovered.FilesUnder([root]).Count == 0, "Uncommitted indexed file survived process kill.");
        using (var searcher = new Searcher(recovered, loadVectors: false))
            Require(searcher.Search("CRASHUNCOMMITTED", null).Count == 0, "Uncommitted FTS result survived process kill.");
        Integrity(recovered); recovered.SetMeta("post-crash", "writable");
        return "Killed a live child inside Database.Transaction after an actual 2 MB write and indexed document/FTS insertion; reopen rolled back all uncommitted rows and retained committed data.";
    }
    private static string DiskFull(string root)
    {
        using var store = new IndexStore(root);
        store.SetMeta("committed", "retained");
        long pages = 0;
        store.Db.Query("PRAGMA page_count", r => pages = r.GetInt64(0));
        store.Db.Execute($"PRAGMA max_page_count = {pages}");
        bool full = false;
        try { store.Db.Transaction(() => store.SetMeta("too-large", new string('z', 4_000_000))); }
        catch (SqliteException error) when (error.SqliteErrorCode == 13) { full = true; }
        Require(full, "SQLite SQLITE_FULL was not raised by the constrained real write.");
        store.Db.Execute("PRAGMA max_page_count = 2147483646");
        Require(store.Meta("too-large") is null && store.Meta("committed") == "retained", "Failed disk-full transaction was not atomic.");
        store.SetMeta("post-full", "writable"); Integrity(store);
        return "Real SQLite write reached SQLITE_FULL via max_page_count (no host disk filled); rollback, integrity and subsequent write verified.";
    }
    private static void Integrity(IndexStore store)
    {
        var values = new List<string>(); store.Db.Query("PRAGMA integrity_check", r => values.Add(r.GetString(0)));
        Require(values.Count == 1 && values[0] == "ok", "SQLite integrity_check failed: " + string.Join("; ", values));
        int violations = 0; store.Db.Query("PRAGMA foreign_key_check", _ => violations++);
        Require(violations == 0, "Foreign key violations after recovery.");
    }

    private static string Find(string? package, string filename)
    {
        string root = package is null ? AppContext.BaseDirectory : Path.GetFullPath(package);
        string plain = Path.Combine(root, filename), native = Path.Combine(root, "native", filename);
        return File.Exists(plain) ? plain : native;
    }
    private static string NativeRuntimes(string root, string? package)
    {
        Require(package is not null, "--native requires --package."); Directory.CreateDirectory(root);
        string dependencyAudit = NativeDependencyAudit.Run(package!);
        string pdfPath = Path.Combine(root, "fixture.pdf"); WritePdf(pdfPath);
        using (var pdf = new PdfiumEngine(Find(package, "pdfium.dll")))
        using (var opened = pdf.Open(pdfPath))
        {
            Require(opened.PageCount == 1 && opened.GetPageText(0).Contains("PDFIUMMARKER"), "PDFium did not extract the fixture text.");
            string png = Path.Combine(root, "render.png"); opened.RenderPageToPng(0, 512, png);
            Require(new FileInfo(png).Length > 100, "PDFium did not render a page.");
        }
        var paths = new FFmpegPaths(Find(package, "ffmpeg.exe"), Find(package, "ffprobe.exe")); paths.Validate();
        short[] samples = Enumerable.Range(0, 48000).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 16000)).ToArray();
        string wav = Path.Combine(root, "tone.wav"); AudioVideoExtractor.WriteWav(wav, samples, 0, samples.Length);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var audio = AudioVideoExtractor.DecodeAudioWindowsAsync(wav, 3, 3, 1, Path.Combine(root, "decoded"), paths, timeout.Token).GetAwaiter().GetResult();
        Require(audio.Pieces.Count > 0 && audio.Duration > 2.5, "Actual FFmpeg audio decoding produced no windows.");
        string video = Path.Combine(root, "video.mp4");
        string rgb = Path.Combine(root, "frames.rgb");
        File.WriteAllBytes(rgb, Enumerable.Range(0, 320 * 240 * 3 * 5).Select(i => (byte)(i % 251)).ToArray());
        RunProcess(paths.FFmpegExe, ["-y", "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", "320x240", "-framerate", "5", "-i", rgb, "-c:v", "mpeg4", video]);
        Require(AudioVideoExtractor.HasStreamAsync(video, 'v', paths, timeout.Token).GetAwaiter().GetResult(), "FFprobe did not find generated video.");
        RunProcess(paths.FFmpegExe, ["-y", "-i", video, "-frames:v", "1", Path.Combine(root, "frame.png")]);
        Require(new FileInfo(Path.Combine(root, "frame.png")).Length > 100, "FFmpeg did not decode the video frame.");
        string av1 = Path.Combine(root, "av1.webm");
        using (var fixture = typeof(Program).Assembly.GetManifestResourceStream("WAI.ReleaseSmoke.Fixtures.av1.webm")
            ?? throw new InvalidOperationException("Missing embedded AV1 fixture."))
        using (var output = File.Create(av1)) fixture.CopyTo(output);
        RunProcess(paths.FFmpegExe, ["-y", "-hwaccel", "none", "-i", av1, "-frames:v", "1", Path.Combine(root, "av1.png")]);
        Require(new FileInfo(Path.Combine(root, "av1.png")).Length > 100, "FFmpeg did not decode AV1 on the CPU.");
        return $"{dependencyAudit} PDFium text extraction and PNG rendering; FFprobe, PCM decoding, MPEG-4 and CPU AV1 frame decoding passed.";
    }
    private static void RunProcess(string exe, string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot launch {exe}");
        var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException(exe); }
        Require(process.ExitCode == 0, $"{exe} failed: {errors.GetAwaiter().GetResult()}"); output.GetAwaiter().GetResult();
    }
    private static void WritePdf(string path)
    {
        string text = "PDFIUMMARKER Production smoke fixture verifies text extraction and page rendering with the shipped native library. " +
            "A medical equipment invoice contains account number 483716 and delivery instructions for the laboratory. This document is generated in an isolated release workspace.";
        string stream = $"BT /F1 10 Tf 30 780 Td ({text}) Tj ET";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2000 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream"];
        var data = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(data.Length); data.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = data.Length; data.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) data.Append($"{offset:0000000000} 00000 n \n");
        data.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, data.ToString(), Encoding.ASCII);
    }

    private static void Evaluate(string root, WorkerConfig config, string manifest, string? package)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        Require(json.RootElement.GetProperty("documents").GetArrayLength() > 0 &&
            json.RootElement.GetProperty("queries").GetArrayLength() > 0, "Evaluation requires nonempty document and query arrays.");
        string documents = Path.Combine(root, "documents"); Directory.CreateDirectory(documents);
        foreach (var document in json.RootElement.GetProperty("documents").EnumerateArray())
        {
            string filename = document.GetProperty("filename").GetString()!;
            Require(Path.GetFileName(filename) == filename, "Manifest filenames must be single file names.");
            File.WriteAllText(Path.Combine(documents, filename), document.GetProperty("text").GetString());
        }
        using var store = new IndexStore(Path.Combine(root, "index"));
        using var indexer = MakeIndexer(store, config, package);
        indexer.Sync([documents]); Index(indexer);
        using var searcher = new Searcher(store);
        using var worker = new WorkerClient(config);
        var results = new List<object>(); int failures = 0;
        foreach (var query in json.RootElement.GetProperty("queries").EnumerateArray())
        {
            string text = query.GetProperty("query").GetString()!, kind = query.GetProperty("kind").GetString()!;
            string[] expected = query.GetProperty("expected_filenames").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Require(kind is "semantic" or "morphology" or "exact" or "absent", $"Unknown evaluation kind: {kind}");
            Require(!string.IsNullOrWhiteSpace(text) && (kind == "absent" ? expected.Length == 0 : expected.Length > 0), "Evaluation query/expected answer contract is invalid.");
            Require(expected.All(name => Path.GetFileName(name) == name && File.Exists(Path.Combine(documents, name))), "Expected answers must name actual fixture documents.");
            var timer = Stopwatch.StartNew();
            var lexicalHits = searcher.Search(text, null, limit: 5);
            var hybridHits = searcher.Search(text, worker.EmbedTexts([Prompts.Query(text)])[0], limit: 5);
            var lexical = lexicalHits.Select(h => Path.GetFileName(h.Path)).ToArray();
            var hybrid = hybridHits.Select(h => Path.GetFileName(h.Path)).ToArray();
            bool passed = kind == "absent" ? lexical.Length == 0 : (kind == "semantic" ? hybrid : lexical).Intersect(expected, StringComparer.OrdinalIgnoreCase).Any();
            if (!passed) failures++;
            object[] Scores(IEnumerable<SearchHit> hits) => hits.Select(h => (object)new { filename = Path.GetFileName(h.Path),
                score = h.Score, cosine = h.Cosine, z = h.Z, keywordRank = h.KeywordRank }).ToArray();
            results.Add(new { query = text, kind, expected, lexical, hybrid, lexicalScores = Scores(lexicalHits),
                hybridScores = Scores(hybridHits), passed, milliseconds = timer.Elapsed.TotalMilliseconds });
        }
        File.WriteAllText(Path.Combine(root, "eval-results.json"), JsonSerializer.Serialize(new { queryCount = results.Count, failures,
            documentCount = json.RootElement.GetProperty("documents").GetArrayLength(), model = worker.Info.Model,
            dimension = worker.Info.Dim, vectorSource = worker.Info.VectorSource,
            limitation = "Synthetic independent-topic corpus. Absence asserts lexical results only; hybrid candidates are diagnostic. No real-world accuracy claim.", results }, Json));
        Require(failures == 0, $"Production fixture evaluation failed {failures}/{results.Count}; see {root}.");
    }
    private sealed class WordsOnly : IEmbedder
    {
        public WorkerInfo Info { get; } = new("none", "words-only", "words-only", "none", 768, true, null);
        public float[][] EmbedTexts(IReadOnlyList<string> texts) => texts.Select(_ => new float[768]).ToArray();
        public Embedding[] EmbedImages(IReadOnlyList<string> paths, int budget) => throw new InvalidOperationException("Lexical fixtures must not request image embeddings.");
        public Embedding[] EmbedAudio(IReadOnlyList<string> paths) => throw new InvalidOperationException("Lexical fixtures must not request audio embeddings.");
        public void Close() { }
        public void Dispose() { }
    }
}
