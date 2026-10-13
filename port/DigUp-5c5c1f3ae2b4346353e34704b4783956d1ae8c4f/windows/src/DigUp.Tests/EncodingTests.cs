using System.Text;
using DigUp.Core;
using Xunit;

namespace DigUp.Tests;

public sealed class EncodingTests
{
    [Fact]
    public void ReadUtf8WithBom()
    {
        string text = "Привет, мир! Тестовая строка в UTF-8 с BOM.";
        byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        string decoded = EncodingHelper.Decode(bytes);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void ReadUtf8WithoutBom()
    {
        string text = "План разработки и архитектура системы: epic, feature, task.";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string decoded = EncodingHelper.Decode(bytes);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void ReadWindows1251RussianText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var win1251 = Encoding.GetEncoding("windows-1251");
        string original = "Задание: реализовать поиск документов и устранить ромбики в кодировке Windows-1251.";
        byte[] bytes = win1251.GetBytes(original);

        string decoded = EncodingHelper.Decode(bytes);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ReadCp866RussianText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp866 = Encoding.GetEncoding(866);
        string original = "Русский текст в кодировке DOS CP866 без кракозябр.";
        byte[] bytes = cp866.GetBytes(original);

        string decoded = EncodingHelper.Decode(bytes);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ReadEmptyAndAscii()
    {
        Assert.Equal(string.Empty, EncodingHelper.Decode(Array.Empty<byte>()));

        string ascii = "Hello world! 1234567890.";
        Assert.Equal(ascii, EncodingHelper.Decode(Encoding.ASCII.GetBytes(ascii)));
    }

    [Fact]
    public void PlainTextFromRtf_DecodesCyrillicHexEscapes()
    {
        // Cyrillic Windows-1251 hex escapes: \'f1\'e2\'e5\'ed\'e4\'e5\'f0\'e0\'ec\'e8 = "свендерами"
        string rtf = @"{\rtf1\ansi\ansicpg1251\deff0 {\fonttbl{\f0\fnil Segoe UI;}} \f0\fs20 \'f1\'e2\'e5\'ed\'e4\'e5\'f0\'e0\'ec\'e8 \'ef\'ee \'f0\'e0\'e7\'f0\'e0\'e1\'ee\'f2\'ea\'e5}";
        string text = DocExtractor.PlainTextFromRtf(rtf);
        Assert.Equal("свендерами по разработке", text);
    }

    [Fact]
    public void PlainTextFromRtf_StripsPictureHexDumps()
    {
        string rtf = @"{\rtf1\ansi\ansicpg1251 {\fonttbl{\f0\fnil Segoe UI;}} {\pict\wmetafile8 ffffffffffffffffff 8d957bf7bf7b} \'cf\'f0\'e8\'e2\'e5\'f2, \'ec\'e8\'f0!}";
        string text = DocExtractor.PlainTextFromRtf(rtf);
        Assert.Equal("Привет, мир!", text);
        Assert.DoesNotContain("ffffff", text);
        Assert.DoesNotContain("8d957b", text);
    }
}
