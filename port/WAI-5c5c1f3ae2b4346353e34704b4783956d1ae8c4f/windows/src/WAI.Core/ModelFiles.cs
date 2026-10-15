using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace WAI.Core;

/// <summary>One model file: its Hugging Face address at the pinned revision,
/// its size, and its SHA-256.</summary>
public sealed record ModelFile(string Name, long Bytes, string Sha256, string? SourceUrl = null)
{
    /// <summary>Hugging Face's address for this file at the pinned revision
    /// (it redirects to a CDN that takes HTTP ranges).</summary>
    public string Url => SourceUrl ??
        $"https://huggingface.co/{ModelFiles.Repository}/resolve/{ModelFiles.Revision}/{Name}";
}

/// <summary>The EmbeddingGemma 2 files the app runs: ggml-org's Q8_0 GGUF conversion at
/// one pinned revision. Ports ModelFiles.swift.</summary>
public static class ModelFiles
{
    public const string Repository = "ggml-org/embeddinggemma-2-GGUF";
    public const string Revision = "bfcd298762cc34d0357ece5ebdd31791a3a374d8";

    /// <summary>The text model (310 MB): all that queries need.</summary>
    public static readonly ModelFile Text = new(
        "embeddinggemma-2-Q8_0.gguf", 309_855_456,
        "2188ac1deca4b77dffefd603c2776a9d76d9d74ec01841392982ebb840b09135");

    /// <summary>The image and audio encoders (555 MB).</summary>
    public static readonly ModelFile Media = new(
        "mmproj-embeddinggemma-2-Q8_0.gguf", 554_821_024,
        "c4a8a52691ecef40618438928bdf9e68379b854e24166f292592353db0aab64f");

    /// <summary>In download order.</summary>
    public static readonly ModelFile[] All = { Text, Media };

    public static long TotalBytes => Text.Bytes + Media.Bytes;

    /// <summary>Where the app keeps them: %LocalAppData%\WAI\Models.</summary>
    public static string InstalledDirectory =>
        Environment.GetEnvironmentVariable("WAI_MODELS") is { Length: > 0 } models ? models :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WAI", "Models");

    /// <summary>The pinned revision's folder in the Hugging Face cache ($HF_HUB_CACHE,
    /// $HF_HOME/hub, or ~/.cache/huggingface/hub).</summary>
    public static string SnapshotDirectory()
    {
        string? hub = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        if (hub is null)
        {
            string? home = Environment.GetEnvironmentVariable("HF_HOME");
            hub = home is null
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache", "huggingface", "hub")
                : Path.Combine(home, "hub");
        }
        return Path.Combine(hub,
            "models--" + Repository.Replace('/', '-'), "snapshots", Revision);
    }

    /// <summary><paramref name="file"/> in the Hugging Face cache, when it's there at
    /// the right size (its checksum is for the caller to check).</summary>
    public static string? CachedCopy(ModelFile file)
    {
        string path = Path.Combine(SnapshotDirectory(), file.Name);
        try
        {
            return new FileInfo(path).Length == file.Bytes ? path : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The files that aren't in <paramref name="folder"/> yet. Only verified
    /// files get their final name, so being there is enough.</summary>
    public static List<ModelFile> Missing(string folder)
    {
        var missing = new List<ModelFile>();
        foreach (var file in All)
        {
            if (!VerifiedDownload.IsValid(Path.Combine(folder, file.Name), file.Bytes, file.Sha256))
                missing.Add(file);
        }
        return missing;
    }

    /// <summary>The SHA-256 of a file as lowercase hex, read in 8 MB pieces
    /// (a 555 MB file takes about half a second).</summary>
    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: false);
        var buffer = new byte[8 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    /// <summary>Downloads <paramref name="file"/> into <paramref name="folder"/>,
    /// resuming a previous partial download (HTTP range over a ".part" file), then
    /// verifies size and SHA-256 before moving it into place. Throws on mismatch.</summary>
    public static Task DownloadAsync(ModelFile file, string folder,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        VerifiedDownload.DownloadAsync(file.Url, Path.Combine(folder, file.Name), file.Bytes,
            file.Sha256, progress, cancellationToken);

    public static Task DownloadFileFromUrlAsync(string url, string folder, string fileName,
        long expectedBytes, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        DownloadAsync(ModelCatalog.All.SelectMany(m => m.Files).FirstOrDefault(f => f.Name == fileName)
            ?? throw new InvalidOperationException("This model is not in the verified download catalog."),
            folder, progress, cancellationToken);

    public static async Task EnsureModelAsync(EmbeddingModelDef model, string directory,
        IProgress<(string File, double Frac)>? progress = null, CancellationToken cancellationToken = default)
    {
        long completedBytes = 0;
        long totalBytes = model.Files.Sum(file => file.Bytes);
        foreach (var file in model.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(directory, file.Name);
            if (VerifiedDownload.IsValid(target, file.Bytes, file.Sha256))
            { completedBytes += file.Bytes; progress?.Report((file.Name, (double)completedBytes / totalBytes)); continue; }
            string? existing = EmbeddingModelDef.ResolvePath(file.Name, directory);
            string? cached = file.Name == Text.Name || file.Name == Media.Name ? CachedCopy(file) : null;
            foreach (var source in new[] { existing, cached }.Where(p => p is not null).Distinct())
            {
                if (!VerifiedDownload.IsValid(source!, file.Bytes, file.Sha256)) continue;
                Directory.CreateDirectory(directory);
                if (!Path.GetFullPath(source!).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    string copying = target + ".copying";
                    await using (var input = File.OpenRead(source!))
                    await using (var output = new FileStream(copying, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, true))
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    File.Move(copying, target, true);
                }
                break;
            }
            if (!VerifiedDownload.IsValid(target, file.Bytes, file.Sha256))
                await DownloadAsync(file, directory, new InlineProgress<double>(f => progress?.Report((file.Name, (completedBytes + f * file.Bytes) / totalBytes))), cancellationToken).ConfigureAwait(false);
            completedBytes += file.Bytes;
            progress?.Report((file.Name, (double)completedBytes / totalBytes));
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
