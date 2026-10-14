// Open a result where it matches: PDFs at the page, sound/video at the
// moment, code at the line. Players vary, so moments open the file plus show
// the time; editors get exact line args.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WAI.App;

public static class Editors
{
    public record Target(string Exe, string Args);

    /// <summary>Editor command for file + line (1-based). "auto" tries VS Code, then friends.</summary>
    public static Target ForCode(string editor, string path, int line)
    {
        string q = $"\"{path}\"";
        if (editor == "auto") editor = FindEditor() ?? "notepad";
        return editor.ToLowerInvariant() switch
        {
            "code" or "vscode" or "cursor" => new Target(editor, $"--goto {q}:{line}"),
            "notepad++" or "notepadpp" => new Target(editor, $"{q} -n{line}"),
            "sublime" or "subl" => new Target(editor, $"{q}:{line}"),
            "devenv" or "visualstudio" => new Target(editor, $"{q} /command \"edit.goto {line}\""),
            _ when editor.EndsWith("idea64.exe") || editor == "jetbrains" => new Target(editor, $"--line {line} {q}"),
            _ => new Target(editor, q),
        };
    }

    private static string? FindEditor()
    {
        string? envEditor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(envEditor)) return envEditor;
        foreach (string name in new[] { "code.cmd", "code.exe", "cursor.exe", "devenv.exe", "notepad++.exe", "subl.exe" })
            if (ExistsOnPath(name)) return name;
        return null;
    }

    private static bool ExistsOnPath(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { if (File.Exists(Path.Combine(dir.Trim(), name))) return true; } catch { }
        }
        return false;
    }

    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(Native(path)) { UseShellExecute = true });

    // The index keeps forward slashes; Windows apps prefer backslashes.
    public static string Native(string path) => path.Replace('/', '\\');

    // PDFs: most readers take no page arg; Acrobat takes /A page=N.
    public static void OpenPdf(string path, double? page)
    {
        string native = Native(path);
        try
        {
            string acro = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe");
            if (page is not null && File.Exists(acro))
            {
                Process.Start(new ProcessStartInfo(acro, $"/A \"page={page:0}\" \"{native}\"") { UseShellExecute = false });
                return;
            }
        }
        catch { }
        Open(native);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr apidl,
        uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr pidl);

    public static void OpenInExplorer(string path)
    {
        string native = Native(path);
        try
        {
            if (File.Exists(native) || Directory.Exists(native))
            {
                // Try official Shell API to reveal & highlight the file in an active or new Explorer window
                try
                {
                    IntPtr pidl = ILCreateFromPathW(native);
                    if (pidl != IntPtr.Zero)
                    {
                        try
                        {
                            int hr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                            if (hr == 0) return;
                        }
                        finally
                        {
                            ILFree(pidl);
                        }
                    }
                }
                catch { }

                // Fallback to explorer.exe /select,"<file>" with direct process creation
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{native}\"",
                    UseShellExecute = false
                });
                return;
            }
            string? dir = Path.GetDirectoryName(native);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OpenInExplorer failed: " + ex.Message);
        }
    }

    /// <summary>Invokes Windows native 'Open With...' dialog for the file.</summary>
    public static void OpenWith(string path)
    {
        string native = Native(path);
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{native}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OpenWith failed: " + ex.Message);
            Open(native);
        }
    }
}
