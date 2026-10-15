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
    public static Task DownloadFileWithResumeAsync(string url, string destinationPath,
        long? expectedBytes = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        VerifiedDownload.DownloadAsync(url, destinationPath, expectedBytes ?? 0, progress: progress,
            cancellationToken: cancellationToken);

    /// <summary>Ensures model files are present, downloading if necessary.</summary>
    public static async Task EnsureModelAsync(
        EmbeddingModelDef model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ModelFiles.EnsureModelAsync(model, ModelsDir,
            progress is null ? null : new Progress<(string File, double Frac)>(p => progress.Report(p.Frac)),
            cancellationToken).ConfigureAwait(false);
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

