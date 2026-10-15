using System.Reflection;
using WAI.App;
using Xunit;

namespace WAI.Tests;

public sealed class WatcherTests
{
    private static void Queue(FileSystemWatcherRunner watcher, string? path) =>
        typeof(FileSystemWatcherRunner).GetMethod("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watcher, [path]);

    [Fact]
    public async Task DebounceWaitsForQuietAndDeduplicatesPaths()
    {
        var completion = new TaskCompletionSource<IReadOnlyCollection<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcherRunner(new Settings(), paths => completion.TrySetResult(paths), 250);
        Queue(watcher, @"C:\folder\file.txt");
        await Task.Delay(150);
        Queue(watcher, @"C:\folder\FILE.txt");
        Queue(watcher, @"C:\folder\other.txt");
        await Task.Delay(150);
        Assert.False(completion.Task.IsCompleted);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task OverflowSupersedesQueuedPathsWithFullScan()
    {
        var completion = new TaskCompletionSource<IReadOnlyCollection<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcherRunner(new Settings(), paths => completion.TrySetResult(paths), 50);
        Queue(watcher, @"C:\folder\file.txt");
        Queue(watcher, null);
        Queue(watcher, @"C:\folder\other.txt");
        Assert.Null(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RenameFromTemporaryNamedDirectoryStillReconcilesOldPath()
    {
        var completion = new TaskCompletionSource<IReadOnlyCollection<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcherRunner(new Settings(), paths => completion.TrySetResult(paths), 50);
        Queue(watcher, @"C:\folder\old.tmp");
        Queue(watcher, @"C:\folder\renamed");
        var paths = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(paths);
        Assert.Contains(@"C:\folder\old.tmp", paths);
        Assert.Contains(@"C:\folder\renamed", paths);
    }

    [Fact]
    public async Task DisposeCancelsPendingNotification()
    {
        int callbacks = 0;
        var watcher = new FileSystemWatcherRunner(new Settings(), _ => Interlocked.Increment(ref callbacks), 100);
        Queue(watcher, @"C:\folder\file.txt");
        watcher.Dispose();
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }
}
