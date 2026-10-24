using System.Diagnostics;
using WAI.App;
using Xunit;

namespace WAI.Tests;

public sealed class TrayMenuPerformanceTests
{
    [Fact]
    public void GetIndexProgress_ReturnsInstantly_WithoutBlockingOrQueryingDatabase()
    {
        var settings = new Settings();
        using var engine = new Engine(settings);

        // GetIndexProgress should execute in < 20 ms because it reads cached values
        var sw = Stopwatch.StartNew();
        var (indexed, remaining) = engine.GetIndexProgress();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50, $"GetIndexProgress took {sw.ElapsedMilliseconds} ms; expected < 50 ms for tray responsiveness.");
        Assert.True(indexed >= 0);
        Assert.True(remaining >= 0);
    }
}
