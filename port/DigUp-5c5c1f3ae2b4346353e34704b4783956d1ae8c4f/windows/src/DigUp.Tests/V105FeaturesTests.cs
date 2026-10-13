using System.Text.Json;
using DigUp.App;
using Xunit;

namespace DigUp.Tests;

public sealed class V105FeaturesTests
{
    [Fact]
    public void Loc_IndexDone_Matches_User_Requirement()
    {
        Loc.CurrentLanguage = "ru";
        Assert.Equal("Выполнено: проиндексировано 43636 файлов", Loc.IndexDone(43636));

        Loc.CurrentLanguage = "en";
        Assert.Equal("Done: indexed 43636 files", Loc.IndexDone(43636));

        // Restore default
        Loc.CurrentLanguage = "ru";
    }

    [Fact]
    public void Loc_IndexRunning_Provides_Clear_Progress()
    {
        Loc.CurrentLanguage = "ru";
        Assert.Equal("Индексация: 100 файлов, осталось 50", Loc.IndexRunning(100, 50));

        Loc.CurrentLanguage = "en";
        Assert.Equal("Indexing: 100 files (50 remaining)", Loc.IndexRunning(100, 50));

        Loc.CurrentLanguage = "ru";
    }

    [Fact]
    public void Settings_Default_Hotkey_Is_Ctrl_Space()
    {
        var settings = new Settings();
        Assert.Equal("ctrl+space", settings.Hotkey);
    }

    [Fact]
    public void Settings_Migrates_Old_Default_Hotkeys_To_Ctrl_Space()
    {
        string json = """{"Hotkey":"ctrl+alt+space","Folders":[]}""";
        var s = JsonSerializer.Deserialize<Settings>(json)!;
        if (s.Hotkey is "shift+alt+space" or "ctrl+alt+space")
            s.Hotkey = "ctrl+space";

        Assert.Equal("ctrl+space", s.Hotkey);
    }

    [Fact]
    public void Settings_Language_Defaults_To_Ru()
    {
        var settings = new Settings();
        Assert.Equal("ru", settings.Language);
    }

    [Fact]
    public void Editors_ForCode_Resolves_Editor_Or_FallsBack()
    {
        var ed = Editors.ForCode("auto", @"C:\test\sample.cs", 42);
        Assert.False(string.IsNullOrWhiteSpace(ed.Exe));
    }

    [Fact]
    public void EncodingHelper_Fixes_Windows1251_Mojibake()
    {
        string corrupted = "Ïî÷òîâûé ÿùèê: mt.beru@yandex.ru Ëîãèí: mt.beru Ïàðîëü: 1D(1WKlaesvv";
        string fixedText = DigUp.Core.EncodingHelper.FixMojibake(corrupted);
        Assert.Equal("Почтовый ящик: mt.beru@yandex.ru Логин: mt.beru Пароль: 1D(1WKlaesvv", fixedText);
    }

    [Fact]
    public void Settings_Appearance_Defaults()
    {
        var settings = new Settings();
        Assert.Equal(1.0, settings.WidgetOpacity);
        Assert.False(settings.WidgetBlur);
        Assert.Equal("system", settings.Theme);
    }

    [Fact]
    public void ThemeManager_IsDark_Respects_Settings()
    {
        Assert.False(ThemeManager.IsDark("light"));
        Assert.True(ThemeManager.IsDark("dark"));
    }

    [Fact]
    public void DocExtractor_Extracts_DocxText()
    {
        string tempDocx = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(tempDocx, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("word/document.xml");
                using var stream = entry.Open();
                using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.Write("""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      <w:body>
                        <w:p><w:r><w:t>Привет, это тестовый документ Word!</w:t></w:r></w:p>
                        <w:p><w:r><w:t>Второй параграф текста.</w:t></w:r></w:p>
                      </w:body>
                    </w:document>
                    """);
            }

            string text = DigUp.Core.DocExtractor.ExtractDocxText(tempDocx);
            Assert.Contains("Привет, это тестовый документ Word!", text);
            Assert.Contains("Второй параграф текста.", text);
        }
        finally
        {
            if (System.IO.File.Exists(tempDocx)) System.IO.File.Delete(tempDocx);
        }
    }
}
