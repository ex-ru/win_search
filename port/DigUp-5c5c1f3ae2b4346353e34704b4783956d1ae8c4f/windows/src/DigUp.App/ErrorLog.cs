// The flight recorder: every top-level failure and diagnostic event lands here.
// Writes never throw (logging must not crash the app it watches).

using System.Diagnostics;
using System.IO;

namespace DigUp.App;

internal static class ErrorLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB per log file before rotation

    public static string LogDir => Settings.LogsDir;
    public static string Path => System.IO.Path.Combine(LogDir, "wai.log");
    public static string PreviousPath => System.IO.Path.Combine(LogDir, "wai.previous.log");
    public static string LegacyPath => System.IO.Path.Combine(Settings.Dir, "error.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            lock (Gate)
            {
                try
                {
                    if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    {
                        try
                        {
                            if (File.Exists(PreviousPath)) File.Delete(PreviousPath);
                            File.Move(Path, PreviousPath);
                        }
                        catch { }
                    }
                }
                catch { }

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(Path, line);
                try { File.AppendAllText(LegacyPath, line); } catch { }
            }
        }
        catch { }
    }

    public static void WriteException(string context, Exception ex)
    {
        string details = $"{context}: {ex.GetType().Name} - {ex.Message}{Environment.NewLine}{ex.StackTrace}";
        if (ex.InnerException is not null)
        {
            details += $"{Environment.NewLine}Inner: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}{Environment.NewLine}{ex.InnerException.StackTrace}";
        }
        Write(details);
    }

    public static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{LogDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Write("OpenLogsFolder failed: " + ex.Message);
        }
    }
}
