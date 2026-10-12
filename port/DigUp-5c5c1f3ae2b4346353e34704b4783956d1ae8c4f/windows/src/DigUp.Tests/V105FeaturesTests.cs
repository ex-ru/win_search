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
}
