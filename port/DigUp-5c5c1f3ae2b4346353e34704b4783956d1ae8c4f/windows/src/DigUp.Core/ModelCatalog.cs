namespace DigUp.Core;

/// <summary>Definition of a supported embedding model.</summary>
public sealed record EmbeddingModelDef(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    int Dimension,
    string TextFile,
    string? MediaFile,
    bool HasMediaProjection,
    string Repo,
    string DownloadUrl,
    long EstimatedBytes)
{
    public bool IsDownloaded(string? customDir = null)
    {
        string? textPath = ResolvePath(TextFile, customDir);
        if (textPath is null) return false;
        if (HasMediaProjection && MediaFile is not null)
        {
            string? mediaPath = ResolvePath(MediaFile, customDir);
            return mediaPath is not null;
        }
        return true;
    }

    public static string? ResolvePath(string fileName, string? customDir = null)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(customDir)) dirs.Add(customDir);

        string? envModels = Environment.GetEnvironmentVariable("DIGUP_MODELS");
        if (!string.IsNullOrEmpty(envModels)) dirs.Add(envModels);

        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigUp", "Models"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "models"));
        dirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "models"));

        foreach (string d in dirs)
        {
            try
            {
                string p = Path.Combine(d, fileName);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}

/// <summary>Catalog of supported embedding models in DigUp.</summary>
public static class ModelCatalog
{
    public const string DefaultModelId = "gemma2";

    public static readonly EmbeddingModelDef Gemma2 = new(
        Id: "gemma2",
        Name: "EmbeddingGemma 2",
        DisplayName: "EmbeddingGemma 2 (2.6B, Мультимодальная: Текст + Фото + Аудио, 768 dim)",
        Description: "Оригинальная мультимодальная модель от Google. Анализирует изображения и звук через проектор mmproj. 768 измерений.",
        Dimension: 768,
        TextFile: "embeddinggemma-2-Q8_0.gguf",
        MediaFile: "mmproj-embeddinggemma-2-Q8_0.gguf",
        HasMediaProjection: true,
        Repo: "ggml-org/embeddinggemma-2-GGUF",
        DownloadUrl: "https://huggingface.co/ggml-org/embeddinggemma-2-GGUF/resolve/bfcd298762cc34d0357ece5ebdd31791a3a374d8/embeddinggemma-2-Q8_0.gguf",
        EstimatedBytes: 309_855_456 + 554_821_024);

    public static readonly EmbeddingModelDef BgeM3 = new(
        Id: "bge-m3",
        Name: "BGE-M3",
        DisplayName: "BGE-M3 (568M, Топ RU/EN поиск, 1024 dim)",
        Description: "Мировой лидер в многоязычном и русском поиске. Контекст до 8192 токенов, 1024 измерения. OCR для изображений.",
        Dimension: 1024,
        TextFile: "bge-m3-Q4_K_M.gguf",
        MediaFile: null,
        HasMediaProjection: false,
        Repo: "gpustack/bge-m3-GGUF",
        DownloadUrl: "https://huggingface.co/gpustack/bge-m3-GGUF/resolve/main/bge-m3-Q4_K_M.gguf",
        EstimatedBytes: 437_774_432);

    public static readonly EmbeddingModelDef PerplexityV2 = new(
        Id: "pplx-v2",
        Name: "Perplexity Embed v2",
        DisplayName: "Perplexity Embed v2 (0.6B Fast CPU INT8, 1024 dim)",
        Description: "Сверхбыстрый инференс на процессоре в INT8 (в 2-3 раза быстрее Gemma 2 на CPU). Точность выше e5-large. 1024 измерения.",
        Dimension: 1024,
        TextFile: "pplx-late-Q8_0.gguf",
        MediaFile: null,
        HasMediaProjection: false,
        Repo: "bigdawg003/pplx-embed-v2-late-0.6b-GGUF",
        DownloadUrl: "https://huggingface.co/bigdawg003/pplx-embed-v2-late-0.6b-GGUF/resolve/main/pplx-late-Q8_0.gguf",
        EstimatedBytes: 536_191_680);

    public static readonly EmbeddingModelDef Nomic = new(
        Id: "nomic",
        Name: "Nomic Embed v1.5",
        DisplayName: "Nomic Embed v1.5 (137M, Ультралегкая и быстрая, 768 dim)",
        Description: "Легковесная и скоростная модель для CPU (25мс на запрос). 768 измерений, контекст 8192.",
        Dimension: 768,
        TextFile: "nomic-embed-text-v1.5.Q8_0.gguf",
        MediaFile: null,
        HasMediaProjection: false,
        Repo: "nomic-ai/nomic-embed-text-v1.5-GGUF",
        DownloadUrl: "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/main/nomic-embed-text-v1.5.Q8_0.gguf",
        EstimatedBytes: 146_146_432);

    public static readonly IReadOnlyList<EmbeddingModelDef> All = [Gemma2, BgeM3, PerplexityV2, Nomic];

    public static EmbeddingModelDef Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Gemma2;
        foreach (var m in All)
        {
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return Gemma2;
    }
}
