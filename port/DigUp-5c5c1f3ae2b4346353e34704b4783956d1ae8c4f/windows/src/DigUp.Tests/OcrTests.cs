using System.IO;
using DigUp.Core;
using Xunit;

namespace DigUp.Tests;

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
}
