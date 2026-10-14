using System;
using System.Diagnostics;
using System.IO;

namespace WAI.Core;

/// <summary>
/// Autonomous Tesseract OCR fallback engine.
/// Activated when Windows Media OCR language packs are missing or for specialized OCR language models.
/// Looks for local tesseract binaries or tessdata in %LocalAppData%\WAI\tessdata or the app directory.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    private readonly string? _tesseractExe;
    private readonly string? _tessdataDir;

    public bool ReadsArabic => false;

    public TesseractOcrEngine(string? customTesseractExe = null, string? customTessdataDir = null)
    {
        _tessdataDir = customTessdataDir ?? FindTessdataDir();
        _tesseractExe = customTesseractExe ?? FindTesseractExe();
    }

    public string Recognize(string imagePath)
    {
        if (string.IsNullOrEmpty(_tesseractExe) || !File.Exists(_tesseractExe) || !File.Exists(imagePath))
            return string.Empty;

        try
        {
            string tempOutBase = Path.Combine(Path.GetTempPath(), "tess_" + Guid.NewGuid().ToString("N"));
            var psi = new ProcessStartInfo
            {
                FileName = _tesseractExe,
                Arguments = $"\"{imagePath}\" \"{tempOutBase}\" -l rus+eng --oem 1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!string.IsNullOrEmpty(_tessdataDir) && Directory.Exists(_tessdataDir))
            {
                psi.EnvironmentVariables["TESSDATA_PREFIX"] = _tessdataDir;
            }

            using var process = Process.Start(psi);
            if (process is null) return string.Empty;
            JobObjectManager.AssignProcess(process);

            process.WaitForExit(10000); // 10s timeout
            string txtPath = tempOutBase + ".txt";
            if (File.Exists(txtPath))
            {
                string text = File.ReadAllText(txtPath);
                try { File.Delete(txtPath); } catch { }
                return text.Trim();
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? FindTesseractExe()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string candidate = Path.Combine(localAppData, "WAI", "tesseract", "tesseract.exe");
        if (File.Exists(candidate)) return candidate;

        string appDir = AppContext.BaseDirectory;
        string appCandidate = Path.Combine(appDir, "tesseract.exe");
        if (File.Exists(appCandidate)) return appCandidate;

        return null;
    }

    private static string? FindTessdataDir()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string candidate = Path.Combine(localAppData, "WAI", "tessdata");
        if (Directory.Exists(candidate)) return candidate;

        string appDir = AppContext.BaseDirectory;
        string appCandidate = Path.Combine(appDir, "tessdata");
        if (Directory.Exists(appCandidate)) return appCandidate;

        return null;
    }
}

/// <summary>
/// Composite OCR engine providing primary WinRT recognition with automatic Tesseract fallback.
/// </summary>
public sealed class CompositeOcrEngine : IOcrEngine
{
    private readonly IOcrEngine _primary;
    private readonly IOcrEngine _fallback;

    public bool ReadsArabic => _primary.ReadsArabic || _fallback.ReadsArabic;

    public CompositeOcrEngine(IOcrEngine? primary = null, IOcrEngine? fallback = null)
    {
        _primary = primary ?? new WindowsMediaOcrEngine();
        _fallback = fallback ?? new TesseractOcrEngine();
    }

    public string Recognize(string imagePath)
    {
        try
        {
            string primaryText = _primary.Recognize(imagePath);
            if (!string.IsNullOrWhiteSpace(primaryText))
                return primaryText;
        }
        catch { }

        try
        {
            return _fallback.Recognize(imagePath);
        }
        catch
        {
            return string.Empty;
        }
    }
}
