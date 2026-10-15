// The flight recorder: every top-level failure and diagnostic event lands here.
// Writes never throw (logging must not crash the app it watches).

using System.Diagnostics;
using System.IO;

namespace WAI.App;

internal static class ErrorLog
{

    public static string LogDir => Settings.LogsDir;
    public static string Path => System.IO.Path.Combine(LogDir, "wai.log");
    public static string PreviousPath => Path + ".previous";

    public static void Write(string message)
    {
        WAI.Core.LocalLog.Write(Path, message);
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
