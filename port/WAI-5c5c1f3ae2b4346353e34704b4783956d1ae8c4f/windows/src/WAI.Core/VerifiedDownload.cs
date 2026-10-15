using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace WAI.Core;

/// <summary>One resumable transfer per destination; a final name always means validated bytes.</summary>
public static class VerifiedDownload
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Written, string Hash)> Verified = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string path, long expectedBytes, string sha256)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedBytes || expectedBytes <= 0 || sha256.Length != 64) return false;
            if (Verified.TryGetValue(path, out var previous) && previous == (info.Length, info.LastWriteTimeUtc, sha256)) return true;
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) return false;
            Verified[path] = (info.Length, info.LastWriteTimeUtc, sha256);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static async Task DownloadAsync(string url, string destination, long expectedBytes = 0,
        string? sha256 = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        HttpClient? client = null)
    {
        destination = Path.GetFullPath(destination);
        var gate = Gates.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                if (sha256 is not null ? IsValid(destination, expectedBytes, sha256)
                    : expectedBytes > 0 && new FileInfo(destination).Length == expectedBytes)
                { progress?.Report(1); return; }
                File.Move(destination, destination + ".invalid", overwrite: true);
            }
            string part = destination + ".part";
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > 0 && expectedBytes > 0 && have >= expectedBytes)
            {
                if (have == expectedBytes && sha256 is not null && IsValid(part, expectedBytes, sha256))
                { File.Move(part, destination, true); progress?.Report(1); return; }
                File.Delete(part); have = 0;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("WAI/1.2");
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                headerTimeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (resumed && response.Content.Headers.ContentRange?.From != have)
                throw new InvalidDataException("The download server returned an incorrect resume offset.");
            if (!resumed) have = 0;
            long total = expectedBytes > 0 ? expectedBytes : response.Content.Headers.ContentRange?.Length
                ?? ((response.Content.Headers.ContentLength ?? 0) + have);
            using (var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 1 << 17, useAsync: true))
            {
                var buffer = new byte[1 << 17];
                while (true)
                {
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    int read = await network.ReadAsync(buffer, idleTimeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    have += read;
                    if (total > 0 && have > total) throw new InvalidDataException("The download exceeds the expected size.");
                    if (total > 0) progress?.Report(Math.Min(0.99, (double)have / total));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (total > 0 && have != total) throw new InvalidDataException("The download is incomplete; retry to resume.");
            if (sha256 is not null && !IsValid(part, expectedBytes, sha256))
            {
                File.Delete(part);
                throw new InvalidDataException("The download checksum does not match; retry to download again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(part, destination, overwrite: true);
            progress?.Report(1);
        }
        finally { gate.Release(); }
    }
}
