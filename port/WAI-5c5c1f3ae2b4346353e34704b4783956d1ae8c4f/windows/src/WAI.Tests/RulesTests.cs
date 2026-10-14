using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class RulesTests
{
    [Theory]
    [InlineData("Screenshot 2026-10-12.png", true)]
    [InlineData("screenshot_app.jpg", true)]
    [InlineData("Screen Shot 123.png", true)]
    [InlineData("CleanShot 2026.png", true)]
    [InlineData("SCR-9901.png", true)]
    [InlineData("Снимок экрана 2026-10-12.png", true)]
    [InlineData("Скриншот-счет.png", true)]
    [InlineData("снимок_оплаты.png", true)]
    [InlineData("photo_vacation.jpg", false)]
    [InlineData("document.pdf", false)]
    [InlineData("contract.docx", false)]
    public void IsScreenshot_CorrectlyClassifies_EnglishAndRussianFilenames(string filename, bool expected)
    {
        bool actual = Rules.IsScreenshot(filename);
        Assert.Equal(expected, actual);
    }
}
