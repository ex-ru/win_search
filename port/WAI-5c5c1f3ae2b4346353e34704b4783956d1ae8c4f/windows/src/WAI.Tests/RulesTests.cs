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

    [Theory]
    [InlineData("~$Document.docx", true)]
    [InlineData("~$Presentation.pptx", true)]
    [InlineData("report.tmp", true)]
    [InlineData("file.lock", true)]
    [InlineData(".archive.swp", true)]
    [InlineData("download.crdownload", true)]
    [InlineData("huge_file.part", true)]
    [InlineData("data.laccdb", true)]
    [InlineData("normal_document.docx", false)]
    [InlineData("invoice.pdf", false)]
    [InlineData("notes.txt", false)]
    public void IsTemporaryOrLockFile_DetectsOfficeAndTemporaryArtifacts(string filename, bool expected)
    {
        bool actual = Rules.IsTemporaryOrLockFile(filename);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(".sync", true)]
    [InlineData(".dropbox.cache", true)]
    [InlineData("$RECYCLE.BIN", true)]
    [InlineData("System Volume Information", true)]
    [InlineData("node_modules", true)]
    [InlineData("MyDocuments", false)]
    [InlineData("Work", false)]
    public void SkippedFolderNames_ContainsCloudSyncAndSystemFolders(string folderName, bool expected)
    {
        bool actual = Rules.SkippedFolderNames.Contains(folderName);
        Assert.Equal(expected, actual);
    }
}
