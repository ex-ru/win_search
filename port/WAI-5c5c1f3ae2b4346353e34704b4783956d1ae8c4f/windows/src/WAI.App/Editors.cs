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

    /// <summary>Editor command for file + line + optional column (1-based). "auto" tries VS Code, then friends.</summary>
    public static Target ForCode(string editor, string path, int line, int column = 1)
    {
        string q = $"\"{path}\"";
        if (editor == "auto") editor = FindEditor() ?? "notepad";
        string lower = editor.ToLowerInvariant();
        int col = Math.Max(1, column);
        int ln = Math.Max(1, line);

        if (lower is "code" or "vscode" or "cursor" or "vscodium" || lower.EndsWith("code.exe") || lower.EndsWith("code.cmd") || lower.EndsWith("cursor.exe"))
            return new Target(editor, $"--goto {q}:{ln}:{col}");

        if (lower is "notepad++" or "notepadpp" || lower.EndsWith("notepad++.exe"))
            return new Target(editor, $"-n{ln} -c{col} {q}");

        if (lower is "sublime" or "subl" || lower.EndsWith("subl.exe"))
            return new Target(editor, $"{q}:{ln}:{col}");

        if (lower is "devenv" or "visualstudio" || lower.EndsWith("devenv.exe"))
            return new Target(editor, $"{q} /command \"edit.goto {ln}\"");

        if (IsJetBrains(lower))
            return new Target(editor, $"--line {ln} --column {col} {q}");

        if (lower is "kate" or "geany" || lower.EndsWith("kate.exe") || lower.EndsWith("geany.exe"))
            return new Target(editor, $"-l {ln} -c {col} {q}");

        return new Target(editor, q);
    }

    public static bool IsJetBrains(string editor)
    {
        string lower = editor.ToLowerInvariant();
        return lower is "jetbrains" or "idea" or "pycharm" or "webstorm" or "rider" or "clion" or "goland" or "rustrover" or "phpstorm"
            || lower.EndsWith("idea64.exe") || lower.EndsWith("pycharm64.exe") || lower.EndsWith("rider64.exe")
            || lower.EndsWith("webstorm64.exe") || lower.EndsWith("clion64.exe") || lower.EndsWith("goland64.exe")
            || lower.EndsWith("rustrover64.exe") || lower.EndsWith("phpstorm64.exe") || lower.EndsWith("studio64.exe");
    }

    private static string? FindEditor()
    {
        string? envEditor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(envEditor)) return envEditor;
        foreach (string name in new[] { "code.cmd", "code.exe", "cursor.exe", "idea64.exe", "pycharm64.exe", "rider64.exe", "webstorm64.exe", "devenv.exe", "notepad++.exe", "subl.exe" })
            if (ExistsOnPath(name)) return name;
        return null;
    }

    public static bool ExistsOnPath(string name)
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

    /// <summary>
    /// Resolves an exact application and command-line arguments to open a PDF at a specific page (1-based).
    /// Supports Adobe Acrobat (32/64 bit), Foxit Reader, SumatraPDF, PDF-XChange, Edge, and Chrome.
    /// Returns null if only standard shell association is available.
    /// </summary>
    public static Target? ResolvePdfTarget(string path, double? page)
    {
        if (page is null || page < 1) return null;
        string native = Native(path);
        int p = (int)Math.Round(page.Value);

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1. Adobe Acrobat / Acrobat Reader (64-bit and 32-bit)
        string[] acrobatPaths =
        [
            Path.Combine(pf, "Adobe", "Acrobat DC", "Acrobat", "Acrobat.exe"),
            Path.Combine(pf, "Adobe", "Acrobat Reader DC", "Reader", "AcroRd64.exe"),
            Path.Combine(pfx86, "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe"),
            Path.Combine(pfx86, "Adobe", "Acrobat DC", "Acrobat", "Acrobat.exe")
        ];
        foreach (var exe in acrobatPaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"/A \"page={p}\" \"{native}\"");
        }

        // 2. Foxit PDF Reader
        string[] foxitPaths =
        [
            Path.Combine(pf, "Foxit Software", "Foxit PDF Reader", "FoxitPDFReader.exe"),
            Path.Combine(pfx86, "Foxit Software", "Foxit PDF Reader", "FoxitPDFReader.exe"),
            Path.Combine(pf, "Foxit Software", "Foxit Reader", "FoxitReader.exe"),
            Path.Combine(pfx86, "Foxit Software", "Foxit Reader", "FoxitReader.exe")
        ];
        foreach (var exe in foxitPaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"/A \"page={p}\" \"{native}\"");
        }
        if (ExistsOnPath("FoxitPDFReader.exe"))
            return new Target("FoxitPDFReader.exe", $"/A \"page={p}\" \"{native}\"");

        // 3. SumatraPDF
        string[] sumatraPaths =
        [
            Path.Combine(pf, "SumatraPDF", "SumatraPDF.exe"),
            Path.Combine(localApp, "SumatraPDF", "SumatraPDF.exe")
        ];
        foreach (var exe in sumatraPaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"-page {p} \"{native}\"");
        }
        if (ExistsOnPath("SumatraPDF.exe"))
            return new Target("SumatraPDF.exe", $"-page {p} \"{native}\"");

        // 4. PDF-XChange Editor
        string[] pdfxPaths =
        [
            Path.Combine(pf, "Tracker Software", "PDF Editor", "PDFXEdit.exe"),
            Path.Combine(pfx86, "Tracker Software", "PDF Editor", "PDFXEdit.exe")
        ];
        foreach (var exe in pdfxPaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"/A \"page={p}\" \"{native}\"");
        }
        if (ExistsOnPath("PDFXEdit.exe"))
            return new Target("PDFXEdit.exe", $"/A \"page={p}\" \"{native}\"");

        // 5. Microsoft Edge (standard Windows PDF handler with #page=N support)
        string[] edgePaths =
        [
            Path.Combine(pfx86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe")
        ];
        string uriPath = native.Replace('\\', '/');
        foreach (var exe in edgePaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"\"file:///{uriPath}#page={p}\"");
        }

        return null;
    }

    /// <summary>PDFs: opens at the exact page using Acrobat, Foxit, Sumatra, PDF-XChange, Edge or default viewer.</summary>
    public static void OpenPdf(string path, double? page)
    {
        string native = Native(path);
        try
        {
            var target = ResolvePdfTarget(native, page);
            if (target is not null)
            {
                Process.Start(new ProcessStartInfo(target.Exe, target.Args) { UseShellExecute = false });
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OpenPdf target failed: " + ex.Message);
        }
        Open(native);
    }

    /// <summary>
    /// Resolves external media player (VLC, MPC-HC, MPC-BE, MPV, PotPlayer) with exact timestamp seek arguments.
    /// Returns null if no known player is installed.
    /// </summary>
    public static Target? ResolveMediaTarget(string path, double? seconds)
    {
        if (seconds is null || seconds <= 0) return null;
        string native = Native(path);
        double s = seconds.Value;
        int wholeSec = Math.Max(0, (int)s);

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // 1. VLC media player
        string[] vlcPaths =
        [
            Path.Combine(pf, "VideoLAN", "VLC", "vlc.exe"),
            Path.Combine(pfx86, "VideoLAN", "VLC", "vlc.exe")
        ];
        foreach (var exe in vlcPaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"--start-time={wholeSec} \"{native}\"");
        }
        if (ExistsOnPath("vlc.exe"))
            return new Target("vlc.exe", $"--start-time={wholeSec} \"{native}\"");

        // 2. MPC-HC / MPC-BE (takes milliseconds)
        int ms = (int)(s * 1000);
        string[] mpcPaths =
        [
            Path.Combine(pf, "MPC-HC", "mpc-hc64.exe"),
            Path.Combine(pfx86, "MPC-HC", "mpc-hc.exe"),
            Path.Combine(pf, "MPC-BE x64", "mpc-be64.exe"),
            Path.Combine(pfx86, "MPC-BE", "mpc-be.exe")
        ];
        foreach (var exe in mpcPaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"/start {ms} \"{native}\"");
        }
        if (ExistsOnPath("mpc-hc64.exe")) return new Target("mpc-hc64.exe", $"/start {ms} \"{native}\"");
        if (ExistsOnPath("mpc-be64.exe")) return new Target("mpc-be64.exe", $"/start {ms} \"{native}\"");

        // 3. MPV
        if (ExistsOnPath("mpv.exe"))
            return new Target("mpv.exe", $"--start={s:0.##} \"{native}\"");

        // 4. PotPlayer
        string[] potPaths =
        [
            Path.Combine(pf, "DAUM", "PotPlayer", "PotPlayer64.exe"),
            Path.Combine(pfx86, "DAUM", "PotPlayer", "PotPlayer.exe")
        ];
        foreach (var exe in potPaths)
        {
            if (File.Exists(exe))
                return new Target(exe, $"\"{native}\" /seek={s:0.##}");
        }

        return null;
    }

    /// <summary>Media: opens audio/video at exact timestamp in VLC/MPC-HC/MPV or default player.</summary>
    public static void OpenMedia(string path, double? seconds)
    {
        string native = Native(path);
        try
        {
            var target = ResolveMediaTarget(native, seconds);
            if (target is not null)
            {
                Process.Start(new ProcessStartInfo(target.Exe, target.Args) { UseShellExecute = false });
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OpenMedia target failed: " + ex.Message);
        }
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
