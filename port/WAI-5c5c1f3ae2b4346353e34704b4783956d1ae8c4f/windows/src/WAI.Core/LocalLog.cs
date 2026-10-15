using System.Collections.Concurrent;

namespace WAI.Core;

/// <summary>Bounded local diagnostics; failure to log must never interrupt file search.</summary>
public static class LocalLog
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static void Write(string path, string message, long maxBytes = 2 * 1024 * 1024)
    {
        try
        {
            path = Path.GetFullPath(path);
            lock (Gates.GetOrAdd(path, _ => new object()))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= maxBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                // Native runtimes sometimes emit a whole tensor dump as one line.
                if (message.Length > 16 * 1024) message = message[..(16 * 1024)] + "…";
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
