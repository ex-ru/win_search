namespace WAI.Core;

/// <summary>Definition of a supported embedding model.</summary>
public sealed record EmbeddingModelDef(
    string Id,
    string Name,
    string DisplayNameRu,
    string DisplayNameEn,
    string DescriptionRu,
    string DescriptionEn,
    int Dimension,
    string TextFile,
    string? MediaFile,
    bool HasMediaProjection,
    string Repo,
    string DownloadUrl,
    long EstimatedBytes)
{
    public string DisplayName => GetDisplayName(true);
    public string GetDisplayName(bool isRu = true) => isRu ? DisplayNameRu : DisplayNameEn;

    public string Description => GetDescription(true);
    public string GetDescription(bool isRu = true) => isRu ? DescriptionRu : DescriptionEn;

    public IReadOnlyList<ModelFile> Files => ModelCatalog.FilesFor(Id);

    public bool IsDownloaded(string? customDir = null)
    {
        return Files.All(file => ResolvePath(file.Name, customDir) is { } path
            && VerifiedDownload.IsValid(path, file.Bytes, file.Sha256));
    }

    public static string? ResolvePath(string fileName, string? customDir = null)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(customDir)) dirs.Add(customDir);

        string? envModels = Environment.GetEnvironmentVariable("WAI_MODELS");
        if (!string.IsNullOrEmpty(envModels)) dirs.Add(envModels);

        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI", "Models"));
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

/// <summary>Catalog of supported embedding models in WAI.</summary>
public static class ModelCatalog
{
    public const string DefaultModelId = "gemma2";

    public static readonly EmbeddingModelDef Gemma2 = new(
        Id: "gemma2",
        Name: "EmbeddingGemma 2",
        DisplayNameRu: "EmbeddingGemma 2 (2.6B, Текст + изображения + аудио, 768 dim)",
        DisplayNameEn: "EmbeddingGemma 2 (2.6B, Text + images + audio, 768 dim)",
        DescriptionRu: "Модель Google для поиска по тексту, изображениям и аудио через проектор mmproj. 768 измерений.",
        DescriptionEn: "Google model for searching text, images, and audio through the mmproj projector. 768 dimensions.",
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
        DisplayNameRu: "BGE-M3 (568M, многоязычный текстовый поиск, 1024 dim)",
        DisplayNameEn: "BGE-M3 (568M, multilingual text search, 1024 dim)",
        DescriptionRu: "Многоязычная модель текстовых эмбеддингов. Текст на изображениях можно искать через OCR. 1024 измерения.",
        DescriptionEn: "A multilingual text embedding model. Text in images can be searched through OCR. 1024 dimensions.",
        Dimension: 1024,
        TextFile: "bge-m3-Q4_K_M.gguf",
        MediaFile: null,
        HasMediaProjection: false,
        Repo: "gpustack/bge-m3-GGUF",
        DownloadUrl: "https://huggingface.co/gpustack/bge-m3-GGUF/resolve/2d48f1737679ad900d5c26c5aad5410e9c70fdca/bge-m3-Q4_K_M.gguf",
        EstimatedBytes: 437_778_496);

    public static readonly EmbeddingModelDef Nomic = new(
        Id: "nomic",
        Name: "Nomic Embed v1.5",
        DisplayNameRu: "Nomic Embed v1.5 (137M, компактная текстовая модель, 768 dim)",
        DisplayNameEn: "Nomic Embed v1.5 (137M, compact text model, 768 dim)",
        DescriptionRu: "Компактная модель текстовых эмбеддингов. Текст на изображениях можно искать через OCR. 768 измерений.",
        DescriptionEn: "A compact text embedding model. Text in images can be searched through OCR. 768 dimensions.",
        Dimension: 768,
        TextFile: "nomic-embed-text-v1.5.Q8_0.gguf",
        MediaFile: null,
        HasMediaProjection: false,
        Repo: "nomic-ai/nomic-embed-text-v1.5-GGUF",
        DownloadUrl: "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/0188c9bf409793f810680a5a431e7b899c46104c/nomic-embed-text-v1.5.Q8_0.gguf",
        EstimatedBytes: 146_146_432);

    public static readonly IReadOnlyList<EmbeddingModelDef> All = [Gemma2, BgeM3, Nomic];

    public static IReadOnlyList<ModelFile> FilesFor(string id) => id switch
    {
        "gemma2" => ModelFiles.All,
        "bge-m3" => [new(BgeM3.TextFile, BgeM3.EstimatedBytes, "6d39681b26c61279ac1f82db35a04a05009e94c415b51c858ff571489a82fc06", BgeM3.DownloadUrl)],
        "nomic" => [new(Nomic.TextFile, Nomic.EstimatedBytes, "3e24342164b3d94991ba9692fdc0dd08e3fd7362e0aacc396a9a5c54a544c3b7", Nomic.DownloadUrl)],
        _ => throw new ArgumentException("Unknown embedding model.", nameof(id))
    };


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
