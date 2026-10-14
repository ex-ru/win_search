using System.IO;
using System.Text.Json;
using WAI.App;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Xunit;

namespace WAI.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateServiceSettingsCollection
{
    public const string Name = "Update service settings";
}

[Collection(UpdateServiceSettingsCollection.Name)]
public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "wai-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("WAI_HOME");
    private readonly string? _previousUpdateSource = Environment.GetEnvironmentVariable("WAI_UPDATE_SOURCE");

    public UpdateServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("WAI_HOME", Path.Combine(_tempRoot, "home"));
        Environment.SetEnvironmentVariable("WAI_UPDATE_SOURCE", null);
    }

    [Fact]
    public async Task DisabledUpdates_DoNotCreateManagerOrReadFeed()
    {
        var feed = Path.Combine(_tempRoot, "feed-that-must-not-be-opened");
        var settings = new Settings { CheckUpdates = false, UpdateSource = feed };
        int managerCreations = 0;
        using var service = new UpdateService(settings, _ => { }, source =>
        {
            Interlocked.Increment(ref managerCreations);
            return new UpdateManager(source);
        });

        await service.CheckOnceAsync();

        Assert.Equal(0, managerCreations);
        Assert.False(Directory.Exists(feed));
        Assert.Null(settings.LastUpdateCheck);
    }

    [Fact]
    public async Task RecentSuccessfulCheck_SkipsManagerCreation()
    {
        var settings = new Settings
        {
            CheckUpdates = true,
            UpdateSource = Path.GetFullPath(_tempRoot),
            LastUpdateCheck = DateTimeOffset.UtcNow.AddHours(-1)
        };
        int managerCreations = 0;
        using var service = new UpdateService(settings, _ => { }, source =>
        {
            Interlocked.Increment(ref managerCreations);
            return new UpdateManager(source);
        });

        await service.CheckOnceAsync();

        Assert.Equal(0, managerCreations);
        Assert.NotNull(settings.LastUpdateCheck);
    }

    [Fact]
    public async Task LooseDevelopmentBuild_DoesNotRecordCheckTimestamp()
    {
        string feed = Path.Combine(_tempRoot, "empty-feed");
        Directory.CreateDirectory(feed);
        var probe = new UpdateManager(feed, options: null, locator: NewLooseBuildLocator());
        Assert.False(probe.IsInstalled);

        var settings = new Settings { CheckUpdates = true, UpdateSource = feed };
        using var service = new UpdateService(settings, _ => { }, source =>
            new UpdateManager(source, options: null, locator: NewLooseBuildLocator()));

        await service.CheckOnceAsync();

        Assert.Null(settings.LastUpdateCheck);
        Assert.False(File.Exists(Settings.Path));
    }

    [Fact]
    public async Task LocalFeedAtInstalledVersion_RecordsCheckWithoutDownloading()
    {
        string feed = Path.Combine(_tempRoot, "same-version-feed");
        Directory.CreateDirectory(feed);
        WriteLocalReleaseFeed(feed, "WAI", "1.0.0", "WAI-1.0.0-full.nupkg");

        var notifications = new List<string>();
        var settings = new Settings { CheckUpdates = true, UpdateSource = feed };
        var locator = NewInstalledTestLocator(Path.Combine(_tempRoot, "packages"));
        Assert.Equal("WAI", locator.AppId);
        Assert.NotNull(locator.CurrentlyInstalledVersion);
        using var service = new UpdateService(settings, notifications.Add, source =>
            new UpdateManager(source, options: null, locator: locator));

        await service.CheckOnceAsync();

        Assert.NotNull(settings.LastUpdateCheck);
        Assert.Empty(notifications);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "packages")));
        Assert.True(File.Exists(Settings.Path));
    }

    [Fact]
    public async Task LocalFeedForDifferentPackage_DoesNotRecordCheckOrDownload()
    {
        string feed = Path.Combine(_tempRoot, "foreign-package-feed");
        Directory.CreateDirectory(feed);
        WriteLocalReleaseFeed(feed, "OtherApp", "2.0.0", "OtherApp-2.0.0-full.nupkg");

        string packageCache = Path.Combine(_tempRoot, "foreign-package-cache");
        var notifications = new List<string>();
        var settings = new Settings { CheckUpdates = true, UpdateSource = feed };
        var locator = NewInstalledTestLocator(packageCache);
        Assert.Equal("WAI", locator.AppId);
        Assert.NotNull(locator.CurrentlyInstalledVersion);
        using var service = new UpdateService(settings, notifications.Add, source =>
            new UpdateManager(source, options: null, locator: locator));

        await service.CheckOnceAsync();

        Assert.Null(settings.LastUpdateCheck);
        Assert.Empty(notifications);
        Assert.False(Directory.Exists(packageCache));
        Assert.False(File.Exists(Settings.Path));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WAI_HOME", _previousHome);
        Environment.SetEnvironmentVariable("WAI_UPDATE_SOURCE", _previousUpdateSource);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static WindowsVelopackLocator NewLooseBuildLocator() =>
        new(null!, new NullVelopackLogger());

    private static TestVelopackLocator NewInstalledTestLocator(string packageCache) =>
        new("WAI", "1.0.0", packageCache, new NullVelopackLogger());

    private static void WriteLocalReleaseFeed(string feedDirectory, string packageId, string version, string fileName)
    {
        var release = new
        {
            Assets = new[]
            {
                new
                {
                    PackageId = packageId,
                    Version = version,
                    Type = "Full",
                    FileName = fileName,
                    SHA1 = "8CEFA59F1F38631B85FBAEA16C6D31B18920C37A",
                    SHA256 = "F7CB6A4F424E03FBB255F6ECCF7CEA77F931DE6FC47690E0275A3211438046E9",
                    Size = 300098894
                }
            }
        };
        File.WriteAllText(Path.Combine(feedDirectory, "releases.win.json"), JsonSerializer.Serialize(release));
    }
}
