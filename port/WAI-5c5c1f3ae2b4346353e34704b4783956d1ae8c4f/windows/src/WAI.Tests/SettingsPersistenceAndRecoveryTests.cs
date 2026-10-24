using System.IO;
using System.Text.Json;
using WAI.App;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public sealed class SettingsPersistenceAndRecoveryTests
{
    [Fact]
    public void Save_CreatesBackupFile()
    {
        var prevHome = Environment.GetEnvironmentVariable("WAI_HOME");
        var tempHome = Path.Combine(Path.GetTempPath(), "wai-backup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("WAI_HOME", tempHome);
            var settings = new Settings
            {
                Folders = [Path.Combine(tempHome, "docs")],
                Theme = "dark"
            };

            // First save: creates settings.json
            settings.Save();
            Assert.True(File.Exists(Settings.Path));

            // Second save: creates settings.json.bak
            settings.Theme = "light";
            settings.Save();
            Assert.True(File.Exists(Settings.BackupPath));

            // Backup contains previous state ("dark")
            var bakContent = File.ReadAllText(Settings.BackupPath);
            Assert.Contains("\"Theme\": \"dark\"", bakContent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAI_HOME", prevHome);
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void Load_RecoversFromBackup_WhenPrimaryCorrupted()
    {
        var prevHome = Environment.GetEnvironmentVariable("WAI_HOME");
        var tempHome = Path.Combine(Path.GetTempPath(), "wai-corrupt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("WAI_HOME", tempHome);
            var validSettings = new Settings
            {
                Folders = [Path.Combine(tempHome, "docs")],
                Theme = "dark",
                Hotkey = "ctrl+space"
            };
            validSettings.Save();
            validSettings.Save(); // Ensure .bak exists

            // Corrupt primary settings.json
            File.WriteAllText(Settings.Path, "{ corrupted json: invalid !!!");

            var loaded = Settings.Load();
            Assert.Equal("dark", loaded.Theme);
            Assert.Equal(validSettings.Folders, loaded.Folders);
            Assert.True(loaded.SetupDone);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAI_HOME", prevHome);
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void Load_HealsSetupDone_WhenFoldersExist()
    {
        var prevHome = Environment.GetEnvironmentVariable("WAI_HOME");
        var tempHome = Path.Combine(Path.GetTempPath(), "wai-heal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("WAI_HOME", tempHome);
            Directory.CreateDirectory(tempHome);
            string json = """
            {
              "Folders": [ "C:\\TestFolder" ],
              "SetupDone": false
            }
            """;
            File.WriteAllText(Settings.Path, json);

            var loaded = Settings.Load();
            Assert.True(loaded.SetupDone);
            Assert.False(loaded.IsNew);
            Assert.Single(loaded.Folders);
            Assert.Equal("C:\\TestFolder", loaded.Folders[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAI_HOME", prevHome);
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void Load_RecoversFoldersFromIndex_WhenSettingsFileMissing()
    {
        var prevHome = Environment.GetEnvironmentVariable("WAI_HOME");
        var prevIndex = Environment.GetEnvironmentVariable("WAI_INDEX");
        var tempHome = Path.Combine(Path.GetTempPath(), "wai-index-rec-test-" + Guid.NewGuid().ToString("N"));
        var tempIndex = Path.Combine(tempHome, "index");
        var userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        try
        {
            Environment.SetEnvironmentVariable("WAI_HOME", tempHome);
            Environment.SetEnvironmentVariable("WAI_INDEX", tempIndex);
            Directory.CreateDirectory(tempIndex);

            // Create fake SQLite database simulating an existing index
            using (var store = new IndexStore(tempIndex))
            {
                string fakeFile = Path.Combine(userDocs, "subfolder", "sample.txt");
                store.Insert(new CrawlCandidate(fakeFile, 100, 1000.0, FileKind.Doc));
            }

            // At this point, there is no settings.json
            Assert.False(File.Exists(Settings.Path));

            var loaded = Settings.Load();
            Assert.True(loaded.SetupDone);
            Assert.False(loaded.IsNew);
            Assert.Contains(userDocs, loaded.Folders);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAI_HOME", prevHome);
            Environment.SetEnvironmentVariable("WAI_INDEX", prevIndex);
            if (Directory.Exists(tempHome)) Directory.Delete(tempHome, recursive: true);
        }
    }
}
