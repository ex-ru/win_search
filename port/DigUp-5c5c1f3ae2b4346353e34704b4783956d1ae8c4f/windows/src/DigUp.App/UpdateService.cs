using System.IO;
using Velopack;

namespace DigUp.App;

/// <summary>Local release feed. WPF owns this service and its settings writes.</summary>
internal sealed class UpdateService : IDisposable
{
    private readonly Settings _settings;
    private readonly Action<string> _notify;
    private readonly Func<string, UpdateManager> _makeManager;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private CancellationTokenSource? _check;
    private bool _disposed;

    public UpdateService(Settings settings, Action<string> notify, Func<string, UpdateManager>? makeManager = null)
    {
        _settings = settings;
        _notify = notify;
        _makeManager = makeManager ?? (source => new UpdateManager(source));
        _timer.Tick += async (_, _) => await CheckOnceAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckOnceAsync();
    }

    public void Restart()
    {
        // Changing the source or opting out cancels the previous download.
        _check?.Cancel();
        _ = RestartAsync();
    }

    private async Task RestartAsync()
    {
        while (_check is not null && !_disposed) await Task.Delay(50);
        if (!_disposed) await CheckOnceAsync();
    }

    internal async Task CheckOnceAsync()
    {
        if (_disposed || _check is not null || !_settings.CheckUpdates || string.IsNullOrWhiteSpace(_settings.UpdateSource)) return;
        if (_settings.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1)) return;
        string source = _settings.UpdateSource;
        using var check = new CancellationTokenSource();
        _check = check;
        try
        {
            if (!Path.IsPathFullyQualified(source)) throw new InvalidOperationException("The update folder must be an absolute local path.");
            var manager = _makeManager(source);
            // Loose developer builds do not have Velopack's installed manifest.
            if (!manager.IsInstalled) return;
            var update = await manager.CheckForUpdatesAsync();
            check.Token.ThrowIfCancellationRequested();
            if (!_settings.CheckUpdates || _settings.UpdateSource != source) return;
            if (update is not null)
            {
                if (!string.Equals(update.TargetFullRelease.PackageId, manager.AppId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The update folder contains a release for a different application.");
                await manager.DownloadUpdatesAsync(update, cancelToken: check.Token);
                check.Token.ThrowIfCancellationRequested();
                _notify($"DigUp {update.TargetFullRelease.Version} is downloaded. Quit and open DigUp to apply it.");
            }
            _settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            _settings.Save();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Keep searching and retry next hour if a local/network drive was unavailable.
            ErrorLog.Write("updates: " + ex.Message);
        }
        finally { _check = null; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _check?.Cancel();
    }
}
