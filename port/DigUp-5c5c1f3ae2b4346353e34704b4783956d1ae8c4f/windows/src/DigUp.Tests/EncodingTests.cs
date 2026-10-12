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
}
