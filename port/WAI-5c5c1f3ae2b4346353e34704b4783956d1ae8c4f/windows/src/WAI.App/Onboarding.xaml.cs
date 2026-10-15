// First-run setup: folders → verified model download → ready.
using System.Windows;

namespace WAI.App;

public partial class Onboarding : Window
{
    private readonly Settings _settings;
    private readonly Func<IProgress<(string File, double Frac)>?, CancellationToken, Task> _ensureModels;
    private readonly Action _saveSettings;
    private int _step;
    private int _estimateGeneration;
    private CancellationTokenSource? _dl;
    private bool _downloaded;
    private bool _downloading;
    private bool _closed;
    public bool Completed { get; private set; }

    public Onboarding(Settings settings, Engine engine)
        : this(settings, engine.EnsureModelsAsync, settings.Save) { }

    internal Onboarding(Settings settings,
        Func<IProgress<(string File, double Frac)>?, CancellationToken, Task> ensureModels,
        Action saveSettings)
    {
        _settings = settings;
        _ensureModels = ensureModels;
        _saveSettings = saveSettings;
        Loc.CurrentLanguage = settings.Language;
        InitializeComponent();
        try { Icon = AppIcon.Wpf; } catch { }
        Title = Loc.SetupTitle;
        Heading.Text = Loc.SetupHeading;
        FoldersIntro.Text = Loc.SetupFolders;
        DownloadIntro.Text = Loc.SetupDownload;
        ReadyIntro.Text = Loc.SetupReady;
        ReadyShortcut.Text = Loc.SetupShortcut(SettingsWindow.Display(settings.Hotkey));
        Back.Content = Loc.Back;
        AddFolder.Content = Loc.AddFolder;
        RemoveFolder.Content = Loc.RemoveFolder;
        RefreshFolders();
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = step;
        P1.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        P3.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        P4.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step.Text = Loc.SetupStep(step + 1);
        Back.IsEnabled = step == 1 && !_downloading;
        Next.Content = step == 2 ? Loc.OpenSearch : Loc.Next;
        if (step == 0) UpdateEstimate();
        if (step == 1 && !_downloaded) _ = StartDownloadAsync();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && !_downloading) ShowStep(0);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 0 && _settings.Folders.Count == 0)
        {
            System.Windows.MessageBox.Show(this, Loc.SetupPickFolder, Loc.SetupTitle,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        if (_step == 1 && !_downloaded)
        {
            if (!_downloading) _ = StartDownloadAsync();
            return;
        }
        if (_step == 2)
        {
            _settings.SetupDone = true;
            Completed = true;
            Close();
            return;
        }
        ShowStep(_step + 1);
    }

    private void RefreshFolders()
    {
        Folders.ItemsSource = null;
        Folders.ItemsSource = _settings.Folders.ToList();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = Loc.SetupFolders };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
            !_settings.Folders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Folders.Add(dlg.SelectedPath);
            RefreshFolders();
            UpdateEstimate();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Folders.SelectedItem is string folder)
        {
            _settings.Folders.Remove(folder);
            RefreshFolders();
            UpdateEstimate();
        }
    }

    private async void UpdateEstimate()
    {
        int generation = ++_estimateGeneration;
        Estimate.Text = Loc.EstimateRunning;
        var folders = _settings.Folders.ToList();
        try
        {
            var (count, _, _) = await Task.Run(() => Engine.Estimate(folders, false));
            if (!_closed && generation == _estimateGeneration)
                Estimate.Text = count == 0 ? Loc.NoReadableFiles : Loc.SetupEstimate(count);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("setup estimate: " + ex);
            if (!_closed && generation == _estimateGeneration) Estimate.Text = string.Empty;
        }
    }

    private async Task StartDownloadAsync()
    {
        if (_downloading || _closed) return;
        _downloading = true;
        Next.IsEnabled = false;
        Back.IsEnabled = false;
        DlStatus.Text = Loc.ModelDownloadingDefault;
        Bar.Value = 0;
        using var cancellation = new CancellationTokenSource();
        _dl = cancellation;
        var progress = new Progress<(string File, double Frac)>(value =>
        {
            if (_closed || cancellation.IsCancellationRequested) return;
            Bar.Value = value.Frac * 100;
            DlStatus.Text = $"{Loc.ModelDownloadingDefault} {value.Frac * 100:0}%";
        });
        try
        {
            await Task.Run(() => _ensureModels(progress, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            _downloaded = true;
            Bar.Value = 100;
            DlStatus.Text = Loc.ModelReady;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed) DlStatus.Text = Loc.DownloadStopped;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("setup model download: " + ex);
            if (!_closed) DlStatus.Text = Loc.DownloadFailed;
        }
        finally
        {
            _downloading = false;
            if (ReferenceEquals(_dl, cancellation)) _dl = null;
            if (!_closed)
            {
                Back.IsEnabled = true;
                Next.IsEnabled = true;
                Next.Content = _downloaded ? Loc.Next : Loc.Retry;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _dl?.Cancel();
        _saveSettings();
        base.OnClosed(e);
    }
}
