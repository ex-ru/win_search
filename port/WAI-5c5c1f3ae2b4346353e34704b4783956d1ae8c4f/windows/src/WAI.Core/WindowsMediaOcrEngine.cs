using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace WAI.Core;

/// <summary>
/// Native Windows OCR engine powered by Windows.Media.Ocr.
/// Available on Windows 10 (19041+) and Windows 11 with zero external dependencies.
/// Uses installed language recognizers (including Russian and English) to extract text
/// from full-resolution screenshots for hybrid keyword search.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;

    public bool ReadsArabic { get; }

    public WindowsMediaOcrEngine()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine is null)
            {
                var available = OcrEngine.AvailableRecognizerLanguages;
                if (available.Count > 0)
                    _engine = OcrEngine.TryCreateFromLanguage(available[0]);
            }

            if (_engine is not null)
            {
                ReadsArabic = _engine.RecognizerLanguage.LanguageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            _engine = null;
        }
    }

    public string Recognize(string imagePath)
    {
        if (_engine is null || !File.Exists(imagePath))
            return string.Empty;

        try
        {
            return RecognizeAsync(imagePath).GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> RecognizeAsync(string imagePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await _engine!.RecognizeAsync(bitmap);
        return result?.Text ?? string.Empty;
    }
}
