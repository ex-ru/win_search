using System.IO;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class OcrTests
{
    [Fact]
    public void WindowsMediaOcrEngine_Instantiates_WithoutError()
    {
        var ocr = new WindowsMediaOcrEngine();
        Assert.NotNull(ocr);
    }

    [Fact]
    public void WindowsMediaOcrEngine_RecognizesRussianAndEnglishText()
    {
        string samplePath = Path.GetFullPath(@"..\..\..\..\..\..\..\..\testbed\Скриншот-счет.png");
        if (!File.Exists(samplePath))
        {
            samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\..\testbed\Скриншот-счет.png"));
        }

        if (!File.Exists(samplePath))
        {
            samplePath = @"D:\_projects\win_search\testbed\Скриншот-счет.png";
        }

        Assert.True(File.Exists(samplePath), $"Sample file not found at {samplePath}");

        var ocr = new WindowsMediaOcrEngine();
        string text = ocr.Recognize(samplePath);

        Assert.NotNull(text);
        Assert.NotEmpty(text);
        // Verify key tokens are recognized
        Assert.True(text.Contains("88392") || text.Contains("15400") || text.Contains("PAYMENT") || text.Contains("оплат"),
            $"Expected text not found in OCR result: '{text}'");
    }

    [Fact]
    public void CompositeOcrEngine_DelegatesToFallback_WhenPrimaryEmpty()
    {
        var stubPrimary = new MockOcrEngine("");
        var stubFallback = new MockOcrEngine("Fallback text from Tesseract");

        var composite = new CompositeOcrEngine(stubPrimary, stubFallback);
        string result = composite.Recognize("dummy_path.png");

        Assert.Equal("Fallback text from Tesseract", result);
    }

    [Fact]
    public void CompositeOcrEngine_PrefersPrimary_WhenNonEmpty()
    {
        var stubPrimary = new MockOcrEngine("Primary text from WinRT");
        var stubFallback = new MockOcrEngine("Fallback text");

        var composite = new CompositeOcrEngine(stubPrimary, stubFallback);
        string result = composite.Recognize("dummy_path.png");

        Assert.Equal("Primary text from WinRT", result);
    }

    [Fact]
    public void TesseractOcrEngine_GracefullyHandlesMissingExecutable()
    {
        var tess = new TesseractOcrEngine(customTesseractExe: @"C:\non_existent_path\tesseract.exe");
        string result = tess.Recognize(@"D:\_projects\win_search\testbed\Screenshot-probe.png");
        Assert.Equal(string.Empty, result);
    }

    private sealed class MockOcrEngine : IOcrEngine
    {
        private readonly string _text;
        public bool ReadsArabic => false;
        public MockOcrEngine(string text) => _text = text;
        public string Recognize(string imagePath) => _text;
    }
}
