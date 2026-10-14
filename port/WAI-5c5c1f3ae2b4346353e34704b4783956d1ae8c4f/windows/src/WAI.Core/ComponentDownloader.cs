using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace WAI.Core;

/// <summary>
/// On-demand downloader for heavy components (FFmpeg media tools, alternative GGUF models).
/// Enables lightweight installer (~75 MB) without bundling heavy external assets.
/// </summary>
public static class ComponentDownloader
{
    public static string LocalToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI", "tools");

    public static string FFmpegDir => Path.Combine(LocalToolsDir, "ffmpeg");

    public static string ModelsDir =>
        Environment.GetEnvironmentVariable("WAI_MODELS") is { Length: > 0 } env ? env :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI", "Models");

    public static bool HasFFmpeg(out string? ffmpegPath, out string? ffprobePath)
    {
        ffmpegPath = null;
        ffprobePath = null;

        string[] searchDirs = [
            AppContext.BaseDirectory,
            FFmpegDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ffmpeg", "bin")
        ];

        foreach (string dir in searchDirs)
        {
            string ff = Path.Combine(dir, "ffmpeg.exe");
            string fp = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ff) && File.Exists(fp))
            {
                ffmpegPath = ff;
                ffprobePath = fp;
                return true;
            }
        }

        // Check PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (string p in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string ff = Path.Combine(p.Trim(), "ffmpeg.exe");
                string fp = Path.Combine(p.Trim(), "ffprobe.exe");
                if (File.Exists(ff) && File.Exists(fp))
                {
                    ffmpegPath = ff;
                    ffprobePath = fp;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Downloads a file with range resuming and progress reporting.</summary>
    public static async Task DownloadFileWithResumeAsync(
        string url,
        string destinationPath,
        long? expectedBytes = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(destinationPath))
        {
            progress?.Report(1.0);
            return;
        }

        string part = destinationPath + ".part";
        long have = 0;
        if (File.Exists(part))
        {
            have = new FileInfo(part).Length;
            if (expectedBytes.HasValue && have > expectedBytes.Value)
            {
                File.Delete(part);
                have = 0;
            }
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WAI-ComponentDownloader/1.1");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) have = 0;

        long total = (response.Content.Headers.ContentLength ?? 0) + have;
        if (expectedBytes.HasValue && expectedBytes.Value > 0) total = expectedBytes.Value;

        using var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true);

        var buffer = new byte[1 << 17];
        int read;
        while ((read = await network.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            have += read;
            if (total > 0) progress?.Report(Math.Min(1.0, (double)have / total));
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Close();

        File.Move(part, destinationPath, overwrite: true);
        progress?.Report(1.0);
    }

    /// <summary>Ensures model files are present, downloading if necessary.</summary>
    public static async Task EnsureModelAsync(
        EmbeddingModelDef model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsDir);
        string textDest = Path.Combine(ModelsDir, model.TextFile);
        if (!File.Exists(textDest))
        {
            await DownloadFileWithResumeAsync(model.DownloadUrl, textDest, model.EstimatedBytes, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ensures FFmpeg tools are present in local tools directory, downloading if needed.</summary>
    public static async Task<bool> EnsureFFmpegAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (HasFFmpeg(out _, out _)) return true;

        Directory.CreateDirectory(FFmpegDir);
        string zipPath = Path.Combine(FFmpegDir, "ffmpeg-tools.zip");
        string url = "https://github.com/ex-ru/win_search/releases/download/v1.1.0/ffmpeg-tools-win-x64.zip";

        try
        {
            await DownloadFileWithResumeAsync(url, zipPath, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (File.Exists(zipPath))
            {
                ZipFile.ExtractToDirectory(zipPath, FFmpegDir, overwriteFiles: true);
                try { File.Delete(zipPath); } catch { }
                return HasFFmpeg(out _, out _);
            }
        }
        catch
        {
            // Silent fallback, video/audio indexing will use metadata stubs if FFmpeg is unavailable
        }
        return false;
    }
}

