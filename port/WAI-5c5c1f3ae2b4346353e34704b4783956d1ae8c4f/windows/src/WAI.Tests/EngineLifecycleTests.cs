using System.Reflection;
using WAI.App;
using Xunit;

namespace WAI.Tests;

public sealed class EngineLifecycleTests
{
    [Fact]
    public async Task ConcurrentRequestsCoalesceAndDisposeWaitsForLoop()
    {
        using var engine = new Engine(new Settings { SetupDone = false });
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => engine.RequestIndex())));
        engine.Dispose();
        Assert.False(engine.IsIndexing);
        var loop = (Task)typeof(Engine).GetField("_loop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        Assert.True(loop.IsCompletedSuccessfully);
        engine.RequestIndex();
        Assert.Empty(engine.SearchText("file"));
    }

    [Fact]
    public void PauseKeepsRequestsPendingUntilResume()
    {
        using var engine = new Engine(new Settings { SetupDone = false });
        engine.SetPaused(true);
        engine.RequestIndex([@"C:\folder\file.txt"]);
        Assert.True(engine.IsPaused);
        Assert.False(engine.IsIndexing);
        engine.SetPaused(false);
        engine.Dispose();
        Assert.False(engine.IsIndexing);
    }

    [Fact]
    public void CanceledSearchDoesNotCreateStores()
    {
        using var engine = new Engine(new Settings());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.SearchText("file", cancellationToken: cancellation.Token));
        Assert.Null(typeof(Engine).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine));
    }
}
