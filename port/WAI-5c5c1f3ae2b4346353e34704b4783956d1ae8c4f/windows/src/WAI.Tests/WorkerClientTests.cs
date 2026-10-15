using System.Diagnostics;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public sealed class WorkerClientTests
{
    private sealed class WorkerScript : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "WAI-worker-test-" + Guid.NewGuid().ToString("N"));
        public WorkerConfig Config { get; }
        public WorkerScript(bool ready = true, TimeSpan? requestTimeout = null, TimeSpan? startupTimeout = null)
        {
            Directory.CreateDirectory(_folder);
            string script = Path.Combine(_folder, "worker.ps1");
            string handshake = ready ? "[Console]::Out.WriteLine('{\"ready\":true,\"info\":{\"model\":\"test\",\"revision\":\"1\",\"runtime\":\"test\",\"dtype\":\"f32\",\"dim\":1,\"text_only\":true}}')" : "";
            File.WriteAllText(script, handshake + "\nwhile ($null -ne [Console]::In.ReadLine()) { Start-Sleep -Seconds 120 }\n");
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            Config = new WorkerConfig(powershell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
                RequestTimeout: requestTimeout, StartupTimeout: startupTimeout ?? TimeSpan.FromSeconds(20));
        }
        public void Dispose() { try { Directory.Delete(_folder, true); } catch { } }
    }

    [Fact]
    public void MissingReadyHandshakeTimesOutAndTerminatesProcess()
    {
        using var script = new WorkerScript(ready: false, startupTimeout: TimeSpan.FromMilliseconds(200));
        var elapsed = Stopwatch.StartNew();
        var error = Assert.Throws<WorkerError>(() => new WorkerClient(script.Config));
        Assert.Contains("timed out", error.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void HungRequestTimesOutAndClientCannotBeReused()
    {
        using var script = new WorkerScript(requestTimeout: TimeSpan.FromMilliseconds(200));
        using var client = new WorkerClient(script.Config);
        var error = Assert.Throws<WorkerError>(() => client.EmbedTexts(["query"]));
        Assert.Contains("timed out", error.Message);
        Assert.Throws<WorkerError>(() => client.EmbedTexts(["next query"]));
    }

    [Fact]
    public void RequestCancellationInterruptsBlockedRead()
    {
        using var script = new WorkerScript();
        using var client = new WorkerClient(script.Config);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var elapsed = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => client.EmbedTexts(["query"], cancellation.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CloseDoesNotWaitForBusyRequestLock()
    {
        using var script = new WorkerScript();
        using var client = new WorkerClient(script.Config);
        Task request = Task.Run(() => { try { client.EmbedTexts(["query"]); } catch (WorkerError) { } });
        await Task.Delay(150);
        var elapsed = Stopwatch.StartNew();
        client.Close();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }
}
