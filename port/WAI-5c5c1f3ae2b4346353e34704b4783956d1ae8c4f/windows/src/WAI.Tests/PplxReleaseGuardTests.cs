using System.Text.Json;
using WAI.App;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public sealed class PplxReleaseGuardTests
{
    [Fact]
    public void UnsupportedModels_AreNotOffered_AndHaveNoDownloadFiles()
    {
        Assert.Equal(new[] { "gemma2", "bge-m3" }, ModelCatalog.All.Select(model => model.Id));
        foreach (var oldModelId in UnsupportedLegacyModels)
            Assert.Throws<ArgumentException>(() => ModelCatalog.FilesFor(oldModelId));
    }

    [Theory]
    [InlineData("pplx-v2")]
    [InlineData("perplexity")]
    [InlineData("pplx")]
    [InlineData("nomic")]
    [InlineData("nomic-embed")]
    public void SettingsLoad_MigratesRemovedModel_AndPreservesOtherPreferences(string oldModelId)
    {
        var previousHome = Environment.GetEnvironmentVariable("WAI_HOME");
        var temporaryHome = Path.Combine(Path.GetTempPath(), "wai-model-migration-" + Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(temporaryHome, "documents");
        var skipped = Path.Combine(temporaryHome, "cache");

        try
        {
            Environment.SetEnvironmentVariable("WAI_HOME", temporaryHome);
            Directory.CreateDirectory(Settings.Dir);
            var persisted = new Settings
            {
                Folders = [documents],
                Skipped = [skipped],
                Hotkey = "ctrl+shift+space",
                Language = "en",
                SetupDone = true,
                EmbeddingModel = oldModelId,
                Theme = "light",
                Code = true,
                CheckUpdates = false,
                UpdateSource = "https://updates.example.test/feed",
                HardwareMode = "cpu",
                QueryFastPathCpu = false,
                WindowWidth = 910
            };
            File.WriteAllText(Settings.Path, JsonSerializer.Serialize(persisted));

            var loaded = Settings.Load();

            Assert.Equal(ModelCatalog.DefaultModelId, loaded.EmbeddingModel);
            Assert.Equal(new[] { documents }, loaded.Folders);
            Assert.Equal(new[] { skipped }, loaded.Skipped);
            Assert.Equal("ctrl+shift+space", loaded.Hotkey);
            Assert.Equal("en", loaded.Language);
            Assert.True(loaded.SetupDone);
            Assert.Equal("light", loaded.Theme);
            Assert.True(loaded.Code);
            Assert.False(loaded.CheckUpdates);
            Assert.Equal("https://updates.example.test/feed", loaded.UpdateSource);
            Assert.Equal("cpu", loaded.HardwareMode);
            Assert.False(loaded.QueryFastPathCpu);
            Assert.Equal(910, loaded.WindowWidth);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAI_HOME", previousHome);
            if (Directory.Exists(temporaryHome)) Directory.Delete(temporaryHome, recursive: true);
        }
    }

    private static readonly string[] UnsupportedLegacyModels = ["pplx-v2", "perplexity", "pplx", "nomic", "nomic-embed"];
}
