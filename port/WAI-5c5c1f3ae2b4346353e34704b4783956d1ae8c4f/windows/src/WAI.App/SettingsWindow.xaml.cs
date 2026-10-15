// Settings, changed once and saved. Hotkey box records Space with
// modifiers; everything applies on Save (hotkey and folders at once).

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WAI.Core;
using MessageBox = System.Windows.MessageBox;

namespace WAI.App;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Engine? _engine;
    private readonly MainWindow? _mainWindow;
    private readonly Action? _onApplyCallback;
    private string _hotkey;
    private CancellationTokenSource? _modelDownloadCts;
    private bool _isDownloadingModel;

    public SettingsWindow(Settings settings, Engine? engine = null, MainWindow? mainWindow = null, Action? onApplyCallback = null)
    {
        _settings = settings;
        _engine = engine;
        _mainWindow = mainWindow;
        _onApplyCallback = onApplyCallback;
        Loc.CurrentLanguage = _settings.Language;
        _hotkey = string.IsNullOrWhiteSpace(settings.Hotkey) ? "ctrl+space" : settings.Hotkey;
        InitializeComponent();
        try { Icon = AppIcon.Wpf; } catch { }
        Closing += (_, _) => { try { _modelDownloadCts?.Cancel(); } catch { } };

        RefreshFolders();
        Hotkey.Text = Display(_hotkey);
        Hotkey.PreviewKeyDown += OnHotkeyKey;
        Hotkey.GotFocus += (_, _) => Hotkey.SelectAll();
        Code.IsChecked = settings.Code;
        Editor.ItemsSource = Settings.Editors;
        Editor.SelectedItem = Settings.Editors.Contains(settings.Editor) ? settings.Editor : "auto";

        SliderOpacity.Value = Math.Clamp((int)Math.Round(_settings.WidgetOpacity * 100), 50, 100);
        TxtOpacityValue.Text = $"{(int)SliderOpacity.Value}%";

        TextAppVersion.Text = $"v{AppConstants.Version} Windows";
        UpdateLanguageToggleButtons(_settings.Language);
        UpdateTextsForLanguage();
        ApplyTheme(ThemeManager.IsDark(_settings.Theme));

        Updates.IsChecked = settings.CheckUpdates;
        UpdateSource.Text = string.IsNullOrWhiteSpace(settings.UpdateSource) ? AppConstants.DefaultUpdateSource : settings.UpdateSource;
        Startup.IsChecked = settings.StartWithWindows;
        CheckQueryFastPathCpu.IsChecked = settings.QueryFastPathCpu;
    }

    private void RefreshFolders()
    {
        Folders.ItemsSource = null;
        Folders.ItemsSource = _settings.Folders.ToList();
        UpdateFolderStats();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = Loc.IsRu ? "Выберите папку для индексации WAI" : "Select folder for WAI indexing" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
            !_settings.Folders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Folders.Add(dlg.SelectedPath);
            RefreshFolders();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Folders.SelectedItem is string s)
        {
            _settings.Folders.Remove(s);
            RefreshFolders();
        }
    }

    private void OnClearFolderVectors(object sender, RoutedEventArgs e)
    {
        if (Folders.SelectedItem is not string selectedFolder)
        {
            MessageBox.Show(this,
                Loc.IsRu ? "Пожалуйста, сначала выберите папку из списка выше." : "Please select a folder from the list above first.",
                Loc.IsRu ? "Очистка векторов папки" : "Clear folder vectors",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            Loc.IsRu
                ? $"Очистить векторный индекс только для папки:\n{selectedFolder}?\n\nЭмбеддинги для файлов в этой папке будут удалены и пересчитаны заново."
                : $"Clear vector index for folder only:\n{selectedFolder}?\n\nEmbeddings for files in this folder will be deleted and recomputed.",
            Loc.IsRu ? "Подтверждение сброса векторов папки" : "Confirm folder vectors reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            if (_engine is not null)
            {
                _engine.ResetIndex([selectedFolder]);
                MessageBox.Show(this,
                    Loc.IsRu
                        ? $"Векторный индекс для папки:\n{selectedFolder}\nуспешно сброшен. Фоновая переиндексация запущена."
                        : $"Vector index for folder:\n{selectedFolder}\nreset successfully. Background re-indexing started.",
                    Loc.IsRu ? "Готово" : "Done",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    Loc.IsRu ? "Индексатор в данный момент недоступен. Настройки сохранены." : "Indexer is currently unavailable. Settings saved.",
                    Loc.IsRu ? "Информация" : "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }

    private void OnClearAllVectors(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            Loc.IsRu
                ? "Вы действительно хотите очистить весь векторный индекс для ВСЕХ папок?\n\nВсе вычисленные семантические эмбеддинги будут удалены, а файлы помечены для повторной индексации.\nПроцесс может занять некоторое время в фоновом режиме."
                : "Are you sure you want to clear the entire vector index for ALL folders?\n\nAll computed semantic embeddings will be deleted and files queued for re-indexing.\nThis process may take some time in background.",
            Loc.IsRu ? "Подтверждение полного сброса векторов" : "Confirm full vectors reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            if (_engine is not null)
            {
                _engine.ResetIndex(_settings.Folders);
                MessageBox.Show(this,
                    Loc.IsRu ? "Векторный индекс успешно очищен.\nФоновая переиндексация запущена." : "Vector index cleared successfully.\nBackground re-indexing started.",
                    Loc.IsRu ? "Готово" : "Done",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    Loc.IsRu ? "Индексатор в данный момент недоступен. Настройки сохранены." : "Indexer is currently unavailable. Settings saved.",
                    Loc.IsRu ? "Информация" : "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        ShowAboutDialog(this);
    }

    public static void ShowAboutDialog(Window? owner = null)
    {
        string message = Loc.IsRu
            ? $"{AppConstants.AppFullName}\n" +
              $"Версия: {AppConstants.Version}\n\n" +
              "Быстрый локальный семантический и лексический поиск по файлам,\n" +
              "документам, коду и изображениям с поддержкой RAG и MCP.\n\n" +
              $"Репозиторий GitHub:\n{AppConstants.RepoUrl}\n\n" +
              "Лицензия: MIT License\n\n" +
              "Открыть страницу проекта на GitHub?"
            : $"{AppConstants.AppFullName}\n" +
              $"Version: {AppConstants.Version}\n\n" +
              "Fast local semantic and keyword search across files,\n" +
              "documents, code, and images with RAG & MCP support.\n\n" +
              $"GitHub Repository:\n{AppConstants.RepoUrl}\n\n" +
              "License: MIT License\n\n" +
              "Open project page on GitHub?";

        var res = owner is not null
            ? MessageBox.Show(owner, message, Loc.IsRu ? $"О программе {AppConstants.AppName}" : $"About {AppConstants.AppName}", MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(message, Loc.IsRu ? $"О программе {AppConstants.AppName}" : $"About {AppConstants.AppName}", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (res == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/ex-ru/win_search") { UseShellExecute = true });
            }
            catch { }
        }
    }

    // Space combos can be global hotkeys here: record modifiers + Space.
    private void OnHotkeyKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Space)
        {
            var parts = new List<string>();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("win");
            if (parts.Count == 0) parts.Add("ctrl");
            parts.Add("space");
            _hotkey = string.Join("+", parts);
            Hotkey.Text = Display(_hotkey);
            if (_hotkey.Contains("shift") && _hotkey.Contains("alt"))
                Hotkey.Text += Loc.IsRu ? " — может конфликтовать со сменой раскладки Windows" : " — may conflict with Windows layout switch";
            e.Handled = true;
        }
        else if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Tab))
        {
            Hotkey.Text = Loc.IsRu ? "нажмите Пробел (например: с зажатым Ctrl)" : "press Space (e.g. with Ctrl held)";
            e.Handled = true;
        }
    }

    internal static string Display(string spec)
    {
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts.Select(p =>
            p.ToLowerInvariant() switch { "ctrl" => "Ctrl", "alt" => "Alt", "win" => "Win", "space" => "Space", "shift" => "Shift", _ => p }));
    }

    private void OnFoldersSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateFolderStats();
    }

    private void UpdateFolderStats()
    {
        if (_engine is null)
        {
            if (TxtFolderStats is not null) TxtFolderStats.Text = Loc.IsRu ? "Индексатор недоступен." : "Indexer unavailable.";
            return;
        }

        try
        {
            long dbBytes = _engine.GetDatabaseFileSize();
            double dbMb = Math.Round(dbBytes / (1024.0 * 1024.0), 1);
            if (TxtDbTotalSize is not null) TxtDbTotalSize.Text = Loc.IsRu ? $"База index: {dbMb} МБ" : $"Index DB: {dbMb} MB";

            var (doneFiles, totalFiles, pendingFiles, _) = _engine.GetDetailedProgress();

            if (Folders.SelectedItem is string selectedFolder)
            {
                var stats = _engine.GetFolderStats(selectedFolder);
                if (stats is not null && TxtFolderStats is not null)
                {
                    TxtFolderStats.Text = $"📁 {selectedFolder}\n" + (Loc.IsRu
                        ? $"Файлов в базе: {stats.FileCount:N0} · Векторов: {stats.VectorCount:N0} · Объем эмбеддингов: {stats.VectorMegabytes:F2} МБ"
                        : $"Files in index: {stats.FileCount:N0} · Vectors: {stats.VectorCount:N0} · Embeddings size: {stats.VectorMegabytes:F2} MB");
                }
            }
            else
            {
                var total = _engine.GetTotalStats();
                if (total is not null && TxtFolderStats is not null)
                {
                    TxtFolderStats.Text = (Loc.IsRu ? "📁 Суммарно в базе поиска:\n" : "📁 Total in search index:\n") + (Loc.IsRu
                        ? $"Индексируемых файлов: {totalFiles:N0} (готово: {doneFiles:N0}, в очереди: {pendingFiles:N0}) · Векторов: {total.VectorCount:N0} · Объем: {total.VectorMegabytes:F2} МБ"
                        : $"Indexed files: {totalFiles:N0} (ready: {doneFiles:N0}, queued: {pendingFiles:N0}) · Vectors: {total.VectorCount:N0} · Size: {total.VectorMegabytes:F2} MB");
                }
            }

            // Check for orphaned vectors of removed folders
            var orphaned = _engine.GetOrphanedStats();
            if (CardOrphaned is not null)
            {
                if (orphaned is not null && orphaned.Files > 0)
                {
                    CardOrphaned.Visibility = Visibility.Visible;
                    if (TxtOrphanedTitle is not null) TxtOrphanedTitle.Text = Loc.OrphanedTitle;
                    if (TxtOrphanedInfo is not null)
                    {
                        string sizeStr = $"{orphaned.VectorMegabytes:F2} {(Loc.IsRu ? "МБ" : "MB")}";
                        TxtOrphanedInfo.Text = Loc.OrphanedInfo(orphaned.Files, orphaned.Vectors, sizeStr);
                    }
                    if (BtnPurgeOrphaned is not null) BtnPurgeOrphaned.Content = Loc.PurgeOrphanedBtn;
                }
                else
                {
                    CardOrphaned.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch { }
    }

    private void OnPurgeOrphaned(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var orphaned = _engine.GetOrphanedStats();
        if (orphaned is null || orphaned.Files == 0) return;

        string sizeStr = $"{orphaned.VectorMegabytes:F2} {(Loc.IsRu ? "МБ" : "MB")}";
        var res = MessageBox.Show(this,
            Loc.PurgeConfirmText(orphaned.Files, sizeStr),
            Loc.PurgeConfirmTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            int purged = _engine.PurgeOrphaned();
            UpdateFolderStats();
            MessageBox.Show(this,
                Loc.IsRu ? $"Очищено {purged} файлов и векторов удаленных папок из базы." : $"Purged {purged} files and vectors of removed folders from index.",
                Loc.IsRu ? "Готово" : "Done",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        ErrorLog.OpenLogsFolder();
    }

    private void OnOpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtOpacityValue is not null)
        {
            TxtOpacityValue.Text = $"{(int)e.NewValue}%";
        }
    }

    private void OnLangRuClick(object sender, RoutedEventArgs e) => SetLanguage("ru");
    private void OnLangEnClick(object sender, RoutedEventArgs e) => SetLanguage("en");

    private void SetLanguage(string lang)
    {
        _settings.Language = lang;
        Loc.CurrentLanguage = lang;
        UpdateLanguageToggleButtons(lang);
        UpdateTextsForLanguage();
        _mainWindow?.UpdateLocalization();
    }

    private void UpdateTextsForLanguage()
    {
        Title = Loc.SettingsTitle;
        if (AdvancedPanel is not null) AdvancedPanel.Header = Loc.Advanced;
        if (TxtHeaderTitle is not null) TxtHeaderTitle.Text = Loc.SettingsHeader;
        if (TxtBannerDesc is not null) TxtBannerDesc.Text = Loc.BannerInitialIndex;
        if (TxtFoldersTitle is not null) TxtFoldersTitle.Text = Loc.FoldersTitle;
        if (TxtFoldersDesc is not null) TxtFoldersDesc.Text = Loc.FoldersDesc;
        if (BtnAddFolder is not null) BtnAddFolder.Content = Loc.AddFolder;
        if (BtnRemoveFolder is not null) BtnRemoveFolder.Content = Loc.RemoveFolder;
        if (TxtStatsHeader is not null) TxtStatsHeader.Text = Loc.StatsHeader;
        if (TxtVectorsTitle is not null) TxtVectorsTitle.Text = Loc.VectorsTitle;
        if (TxtVectorsDesc is not null) TxtVectorsDesc.Text = Loc.VectorsDesc;
        if (BtnClearFolderVectors is not null) BtnClearFolderVectors.Content = Loc.ClearFolderVectors;
        if (BtnClearAllVectors is not null) BtnClearAllVectors.Content = Loc.ClearAllVectors;
        if (TxtVectorsNote is not null) TxtVectorsNote.Text = Loc.VectorsNote;
        if (TxtModelTitle is not null) TxtModelTitle.Text = Loc.ModelTitle;
        if (TxtModelDesc is not null) TxtModelDesc.Text = Loc.ModelDesc;
        if (TxtModelActiveLabel is not null) TxtModelActiveLabel.Text = Loc.ModelActiveLabel;
        if (TxtModelStatusHeader is not null) TxtModelStatusHeader.Text = Loc.ModelStatusHeader;
        if (TxtModelNote is not null) TxtModelNote.Text = Loc.ModelNote;
        if (BtnDownloadModel is not null) BtnDownloadModel.Content = Loc.ModelDownloadBtn;
        if (BtnCancelModelDownload is not null) BtnCancelModelDownload.Content = Loc.ModelCancelDownload;
        if (TxtModelDownloadStatus is not null) TxtModelDownloadStatus.Text = Loc.ModelDownloadingDefault;
        if (TxtHwTitle is not null) TxtHwTitle.Text = Loc.HwTitle;
        if (TxtHwDesc is not null) TxtHwDesc.Text = Loc.HwDesc;
        if (TxtHwDeviceLabel is not null) TxtHwDeviceLabel.Text = Loc.HwDeviceLabel;
        if (TxtHwStatusHeader is not null) TxtHwStatusHeader.Text = Loc.HwStatusHeader;
        if (TxtHwNote is not null) TxtHwNote.Text = Loc.HwNote;
        if (TxtCheckQueryFastPathCpu is not null) TxtCheckQueryFastPathCpu.Text = Loc.HwQueryFastPath;
        if (CheckQueryFastPathCpu is not null) CheckQueryFastPathCpu.ToolTip = Loc.HwQueryFastPathTooltip;
        if (TxtAppearanceTitle is not null) TxtAppearanceTitle.Text = Loc.AppearanceTitle;
        if (TxtAppearanceDesc is not null) TxtAppearanceDesc.Text = Loc.AppearanceDesc;
        if (TxtOpacityLabel is not null) TxtOpacityLabel.Text = Loc.OpacityLabel;
        if (TxtThemeLabel is not null) TxtThemeLabel.Text = Loc.ThemeLabel;
        if (TxtHotkeyTitle is not null) TxtHotkeyTitle.Text = Loc.HotkeyTitle;
        if (TxtHotkeyDesc is not null) TxtHotkeyDesc.Text = Loc.HotkeyDesc;
        if (TxtCodeTitle is not null) TxtCodeTitle.Text = Loc.CodeTitle;
        if (Code is not null) Code.Content = Loc.CodeIndexCheck;
        if (TxtCodeEditorLabel is not null) TxtCodeEditorLabel.Text = Loc.CodeEditorLabel;
        if (TxtSystemTitle is not null) TxtSystemTitle.Text = Loc.SystemTitle;
        if (Startup is not null) Startup.Content = Loc.StartupCheck;
        if (Updates is not null) Updates.Content = Loc.UpdatesCheck;
        if (TxtUpdateSourceLabel is not null) TxtUpdateSourceLabel.Text = Loc.UpdateSourceLabel;
        if (TxtSystemNote is not null) TxtSystemNote.Text = Loc.SystemNote;
        if (TxtOrphanedTitle is not null) TxtOrphanedTitle.Text = Loc.OrphanedTitle;
        if (BtnPurgeOrphaned is not null) BtnPurgeOrphaned.Content = Loc.PurgeOrphanedBtn;
        if (BtnOpenLogs is not null) BtnOpenLogs.Content = Loc.OpenLogsBtn;
        if (TxtLogsDesc is not null) TxtLogsDesc.Text = Loc.LogsDesc;
        if (BtnAbout is not null) BtnAbout.Content = Loc.About;
        if (BtnApply is not null) BtnApply.Content = Loc.Apply;
        if (BtnCancel is not null) BtnCancel.Content = Loc.Cancel;
        if (BtnSave is not null) BtnSave.Content = Loc.Save;

        if (ComboHardware is not null) InitHardwareOptions();
        if (ComboEmbeddingModel is not null) InitModelOptions();
        UpdateThemeOptions();
        UpdateFolderStats();
        if (ComboEmbeddingModel?.SelectedItem is ModelComboItem selModel)
        {
            UpdateModelStatus(selModel.Model);
        }
    }

    private void UpdateThemeOptions()
    {
        if (ComboTheme is null) return;
        int idx = ComboTheme.SelectedIndex >= 0
            ? ComboTheme.SelectedIndex
            : _settings.Theme switch
            {
                ThemeManager.ThemeDark => 1,
                ThemeManager.ThemeLight => 2,
                _ => 0
            };
        ComboTheme.SelectionChanged -= OnThemeSelectionChanged;
        ComboTheme.ItemsSource = Loc.IsRu
            ? new[] { "Системная (как в Windows)", "Тёмная (Dark)", "Светлая (Light)" }
            : new[] { "System (Windows default)", "Dark", "Light" };
        ComboTheme.SelectedIndex = idx;
        ComboTheme.SelectionChanged += OnThemeSelectionChanged;
    }

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (ComboTheme is null || ComboTheme.SelectedIndex < 0) return;
        string theme = ComboTheme.SelectedIndex switch
        {
            1 => ThemeManager.ThemeDark,
            2 => ThemeManager.ThemeLight,
            _ => ThemeManager.ThemeSystem
        };
        ApplyTheme(ThemeManager.IsDark(theme));
    }

    private void UpdateLanguageToggleButtons(string lang)
    {
        bool isRu = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        bool isDark = ThemeManager.IsDark(ComboTheme?.SelectedIndex switch
        {
            1 => ThemeManager.ThemeDark,
            2 => ThemeManager.ThemeLight,
            _ => _settings.Theme
        });
        var bc = new System.Windows.Media.BrushConverter();
        var activeBrush = (System.Windows.Media.Brush)bc.ConvertFrom("#0078D4")!;
        var inactiveBrush = isDark
            ? (System.Windows.Media.Brush)bc.ConvertFrom("#282828")!
            : (System.Windows.Media.Brush)bc.ConvertFrom("#E2E8F0")!;
        var inactiveFg = isDark
            ? (System.Windows.Media.Brush)bc.ConvertFrom("#888888")!
            : (System.Windows.Media.Brush)bc.ConvertFrom("#475569")!;

        if (BtnLangRu is not null)
        {
            BtnLangRu.Background = isRu ? activeBrush : inactiveBrush;
            BtnLangRu.Foreground = isRu ? System.Windows.Media.Brushes.White : inactiveFg;
        }
        if (BtnLangEn is not null)
        {
            BtnLangEn.Background = !isRu ? activeBrush : inactiveBrush;
            BtnLangEn.Foreground = !isRu ? System.Windows.Media.Brushes.White : inactiveFg;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch { }
        Close();
    }

    public sealed record HardwareComboItem(string Id, string Title);

    private void InitHardwareOptions()
    {
        try
        {
            var hwInfo = HardwareDetector.Detect();
            var items = new List<HardwareComboItem>();
            bool isRu = Loc.IsRu;

            // 1. Auto
            var (_, _, autoSummary) = HardwareDetector.ResolveBackend("auto", hwInfo, isRu);
            items.Add(new HardwareComboItem("auto", Loc.HwAuto(autoSummary)));

            // 2. Discrete GPU (if available)
            if (hwInfo.DiscreteGpu != null)
            {
                var d = hwInfo.DiscreteGpu;
                items.Add(new HardwareComboItem("gpu_ext", Loc.HwDiscrete(d.Name, d.VramMb)));
            }

            // 3. Integrated GPU (if available)
            if (hwInfo.IntegratedGpu != null)
            {
                var ig = hwInfo.IntegratedGpu;
                items.Add(new HardwareComboItem("gpu_int", Loc.HwIntegrated(ig.Name)));
            }

            // 4. CPU Only
            items.Add(new HardwareComboItem("cpu", Loc.HwCpu(hwInfo.CpuCores)));

            string currentId = (ComboHardware.SelectedItem as HardwareComboItem)?.Id ?? _settings.HardwareMode;
            ComboHardware.ItemsSource = items;
            var selected = items.FirstOrDefault(x => string.Equals(x.Id, currentId, StringComparison.OrdinalIgnoreCase)) ?? items[0];
            ComboHardware.SelectedItem = selected;

            TxtHardwareStatus.Text = hwInfo.GetSummary(isRu);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("hardware detector: " + ex);
            TxtHardwareStatus.Text = Loc.IsRu ? "Не удалось определить оборудование. Можно выбрать процессор." : "Could not detect hardware. You can select CPU.";
        }
    }

    public sealed record ModelComboItem(EmbeddingModelDef Model, string Title);

    private void InitModelOptions()
    {
        try
        {
            var models = ModelCatalog.All;
            bool isRu = Loc.IsRu;
            var items = models.Select(m => new ModelComboItem(m, m.GetDisplayName(isRu))).ToList();
            string currentId = (ComboEmbeddingModel.SelectedItem as ModelComboItem)?.Model.Id ?? _settings.EmbeddingModel;

            ComboEmbeddingModel.SelectionChanged -= OnModelSelectionChanged;
            ComboEmbeddingModel.ItemsSource = items;
            var selected = items.FirstOrDefault(x => string.Equals(x.Model.Id, currentId, StringComparison.OrdinalIgnoreCase)) ?? items[0];
            ComboEmbeddingModel.SelectedItem = selected;
            ComboEmbeddingModel.SelectionChanged += OnModelSelectionChanged;

            UpdateModelStatus(selected.Model);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("model catalog: " + ex);
            TxtModelStatus.Text = Loc.IsRu ? "Не удалось прочитать список моделей. Подробности в журнале." : "Could not read the model list. Details are in the log.";
        }
    }

    private async void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboEmbeddingModel.SelectedItem is ModelComboItem item)
        {
            var model = item.Model;
            UpdateModelStatus(model);
            if (!string.Equals(model.Id, _settings.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            {
                if (!model.IsDownloaded())
                {
                    string title = Loc.IsRu ? "Загрузка модели эмбеддингов" : "Download Embedding Model";
                    string msg = Loc.IsRu
                        ? $"Модель '{model.Name}' ещё не загружена на этот компьютер.\n\n" +
                          $"• Размер загрузки: ~{model.EstimatedBytes / (1024 * 1024)} МБ.\n" +
                          $"• Модель будет скачана с Hugging Face в каталог моделей WAI.\n\n" +
                          $"Начать загрузку модели прямо сейчас?"
                        : $"Model '{model.Name}' is not yet downloaded on this computer.\n\n" +
                          $"• Download size: ~{model.EstimatedBytes / (1024 * 1024)} MB.\n" +
                          $"• The model will be downloaded from Hugging Face into WAI models directory.\n\n" +
                          $"Start downloading the model now?";

                    var res = MessageBox.Show(this, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes)
                    {
                        await StartModelDownloadAsync(model);
                    }
                }
            }
        }
    }

    private void UpdateModelStatus(EmbeddingModelDef model)
    {
        bool downloaded = model.IsDownloaded();
        string statusIcon = downloaded
            ? (Loc.IsRu ? "✅ Установлена и готова к работе" : "✅ Installed and ready")
            : (Loc.IsRu ? "⏳ Требуется загрузка весов (GGUF)" : "⏳ Weights download required (GGUF)");
        TxtModelStatus.Text = $"{model.Name} — {statusIcon}";
        TxtModelDetails.Text = $"{model.GetDescription(Loc.IsRu)} ({(Loc.IsRu ? "Размерность векторов" : "Vector dimensions")}: {model.Dimension} dim, {(Loc.IsRu ? "файл" : "file")}: {model.TextFile})";

        if (_isDownloadingModel)
        {
            if (PanelModelDownloadAction is not null) PanelModelDownloadAction.Visibility = Visibility.Collapsed;
            if (PanelModelDownloadProgress is not null) PanelModelDownloadProgress.Visibility = Visibility.Visible;
        }
        else if (downloaded)
        {
            if (PanelModelDownloadAction is not null) PanelModelDownloadAction.Visibility = Visibility.Collapsed;
            if (PanelModelDownloadProgress is not null) PanelModelDownloadProgress.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (PanelModelDownloadAction is not null) PanelModelDownloadAction.Visibility = Visibility.Visible;
            if (PanelModelDownloadProgress is not null) PanelModelDownloadProgress.Visibility = Visibility.Collapsed;
            if (BtnDownloadModel is not null) BtnDownloadModel.Content = Loc.ModelDownloadBtn;
            if (TxtModelDownloadHint is not null)
            {
                long mb = model.EstimatedBytes / (1024 * 1024);
                TxtModelDownloadHint.Text = Loc.IsRu ? $"~{mb} МБ с Hugging Face" : $"~{mb} MB from Hugging Face";
            }
        }
    }

    private async void OnDownloadModelClick(object sender, RoutedEventArgs e)
    {
        if (ComboEmbeddingModel.SelectedItem is ModelComboItem item)
        {
            await StartModelDownloadAsync(item.Model);
        }
    }

    private void OnCancelModelDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_modelDownloadCts is not null && !_modelDownloadCts.IsCancellationRequested)
        {
            try
            {
                _modelDownloadCts.Cancel();
            }
            catch { }
        }
    }

    private async Task StartModelDownloadAsync(EmbeddingModelDef model)
    {
        if (_isDownloadingModel) return;

        _modelDownloadCts?.Cancel();
        _modelDownloadCts = new CancellationTokenSource();
        var ct = _modelDownloadCts.Token;

        _isDownloadingModel = true;
        if (PanelModelDownloadAction is not null) PanelModelDownloadAction.Visibility = Visibility.Collapsed;
        if (PanelModelDownloadProgress is not null) PanelModelDownloadProgress.Visibility = Visibility.Visible;
        if (ProgressModelDownload is not null) ProgressModelDownload.Value = 0;
        if (TxtModelDownloadPercent is not null) TxtModelDownloadPercent.Text = "0%";
        if (TxtModelDownloadStatus is not null) TxtModelDownloadStatus.Text = Loc.IsRu ? $"Загрузка {model.Name}…" : $"Downloading {model.Name}…";
        if (TxtModelDownloadBytes is not null) TxtModelDownloadBytes.Text = $"0 / {model.EstimatedBytes / (1024.0 * 1024.0):F1} {(Loc.IsRu ? "МБ" : "MB")}";

        try
        {
            string dir = ModelFiles.InstalledDirectory;
            Directory.CreateDirectory(dir);

            var progress = new Progress<(string File, double Frac)>(value =>
            {
                if (ct.IsCancellationRequested) return;
                double fraction = Math.Clamp(value.Frac, 0, 1);
                ProgressModelDownload.Value = fraction * 100;
                TxtModelDownloadPercent.Text = $"{fraction * 100:0}%";
                TxtModelDownloadBytes.Text = $"{fraction * model.EstimatedBytes / (1024.0 * 1024.0):F1} / {model.EstimatedBytes / (1024.0 * 1024.0):F1} {(Loc.IsRu ? "МБ" : "MB")}";
            });
            await Task.Run(() => ModelFiles.EnsureModelAsync(model, dir, progress, ct), ct);
            ct.ThrowIfCancellationRequested();

            _isDownloadingModel = false;
            UpdateModelStatus(model);
            MessageBox.Show(this, Loc.ModelDownloadComplete(model.Name), Loc.ModelDownloadCompleteTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _isDownloadingModel = false;
            UpdateModelStatus(model);
        }
        catch (Exception ex)
        {
            _isDownloadingModel = false;
            UpdateModelStatus(model);
            ErrorLog.Write("model download: " + ex);
            MessageBox.Show(this, Loc.DownloadFailed, Loc.ModelDownloadErrorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool ApplyCurrentSettings()
    {
        if (_isDownloadingModel)
        {
            MessageBox.Show(this, Loc.IsRu ? "Дождитесь загрузки модели или отмените её." : "Wait for the model download or cancel it.",
                Loc.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        string updateSource = UpdateSource.Text.Trim();
        if (string.IsNullOrWhiteSpace(updateSource) ||
            string.Equals(updateSource, "dist\\releases", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(updateSource, "dist/releases", StringComparison.OrdinalIgnoreCase))
        {
            updateSource = AppConstants.DefaultUpdateSource;
        }
        else if (!Path.IsPathFullyQualified(updateSource) && !updateSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                Loc.IsRu ? "Укажите абсолютный путь к папке обновлений или оставьте пустым для GitHub." : "Specify the absolute path to update directory or leave empty for GitHub.",
                Loc.SettingsTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _settings.Hotkey = _hotkey;
        _settings.Code = Code.IsChecked == true;
        _settings.Editor = Editor.SelectedItem as string ?? "auto";
        _settings.Language = Loc.CurrentLanguage;
        _settings.WidgetOpacity = SliderOpacity.Value / 100.0;
        _settings.Theme = ComboTheme.SelectedIndex switch
        {
            1 => ThemeManager.ThemeDark,
            2 => ThemeManager.ThemeLight,
            _ => ThemeManager.ThemeSystem
        };

        bool restartWorker = false;
        if (ComboEmbeddingModel.SelectedItem is ModelComboItem modelItem)
        {
            bool modelChanged = !string.Equals(_settings.EmbeddingModel, modelItem.Model.Id, StringComparison.OrdinalIgnoreCase);
            if (modelChanged)
            {
                _settings.EmbeddingModel = modelItem.Model.Id;
                restartWorker = true;
            }
        }

        if (ComboHardware.SelectedItem is HardwareComboItem hwItem)
        {
            bool modeChanged = !string.Equals(_settings.HardwareMode, hwItem.Id, StringComparison.OrdinalIgnoreCase);
            _settings.HardwareMode = hwItem.Id;
            if (modeChanged)
            {
                restartWorker = true;
            }
        }

        bool fastPath = CheckQueryFastPathCpu.IsChecked == true;
        if (_settings.QueryFastPathCpu != fastPath) { _settings.QueryFastPathCpu = fastPath; restartWorker = true; }
        if (restartWorker) _engine?.ResetEmbedderWorker();

        _settings.CheckUpdates = Updates.IsChecked == true;
        if (!string.Equals(_settings.UpdateSource, updateSource, StringComparison.OrdinalIgnoreCase))
            _settings.LastUpdateCheck = null;
        _settings.UpdateSource = updateSource;
        _settings.StartWithWindows = Startup.IsChecked == true;
        _settings.Save();
        return true;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!ApplyCurrentSettings()) return;
        _onApplyCallback?.Invoke();
        ApplyTheme(ThemeManager.IsDark(_settings.Theme));
        if (_mainWindow is not null)
        {
            _mainWindow.ApplyLook();
            _mainWindow.UpdateLocalization();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!ApplyCurrentSettings()) return;
        _onApplyCallback?.Invoke();
        if (_mainWindow is not null)
        {
            _mainWindow.ApplyLook();
            _mainWindow.UpdateLocalization();
        }
        try { DialogResult = true; } catch { }
        Close();
    }

    private void ApplyTheme(bool isDark)
    {
        try
        {
            var bc = new System.Windows.Media.BrushConverter();
            if (isDark)
            {
                Resources["WindowBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1E1E1E")!;
                Resources["WindowForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F0F0F0")!;
                Resources["CardBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#252525")!;
                Resources["CardBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#383838")!;
                Resources["TextPrimaryBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["TextSecondaryBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#AAAAAA")!;
                Resources["TextMutedBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#777777")!;
                Resources["TextAccentBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#80D8FF")!;

                Resources["InputBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#2A2A2A")!;
                Resources["InputBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#444444")!;
                Resources["InputForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["PopupBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#202020")!;
                Resources["PopupBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#444444")!;
                Resources["ItemSelectedBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1C4B72")!;
                Resources["ItemSelectedForegroundBrush"] = System.Windows.Media.Brushes.White;

                Resources["ButtonBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#353535")!;
                Resources["ButtonBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#444444")!;
                Resources["ButtonForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["ButtonHoverBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#454545")!;
                Resources["ButtonHoverBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#555555")!;

                Resources["DangerButtonBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#402020")!;
                Resources["DangerButtonBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#663030")!;
                Resources["DangerButtonForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFAAAA")!;

                Resources["BadgeBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#282828")!;
                Resources["BadgeBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#444444")!;
                Resources["BadgeForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#CCCCCC")!;

                Resources["BannerBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1C2E3D")!;
                Resources["BannerBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#256B9E")!;
                Resources["BannerForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#D0E8FF")!;

                Resources["StatsBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1A2733")!;
                Resources["StatsBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#256B9E")!;
                Resources["StatsBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#80D8FF")!;
                Resources["StatsBoxTextBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#E0E0E0")!;

                Resources["OrphanedBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#221C14")!;
                Resources["OrphanedBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#8A631E")!;
                Resources["OrphanedBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFD54F")!;
                Resources["OrphanedBoxTextBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFE082")!;

                Resources["ModelBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1A2826")!;
                Resources["ModelBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#257E70")!;
                Resources["ModelBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#80FFE8")!;

                Resources["HwBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1A2733")!;
                Resources["HwBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#256B9E")!;
                Resources["HwBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#80D8FF")!;

                Resources["LogsBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1C2228")!;
                Resources["LogsBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#334455")!;
                Resources["LogsBoxTextBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#A0B0C0")!;

                Resources["ScrollThumbBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#444444")!;
                Resources["ScrollThumbHoverBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#666666")!;
                Resources["ExpanderHeaderHoverBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#303030")!;

                Background = (System.Windows.Media.Brush)Resources["WindowBackgroundBrush"];
                Foreground = (System.Windows.Media.Brush)Resources["WindowForegroundBrush"];
            }
            else
            {
                Resources["WindowBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F1F5F9")!;
                Resources["WindowForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#0F172A")!;
                Resources["CardBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["CardBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["TextPrimaryBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#0F172A")!;
                Resources["TextSecondaryBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#475569")!;
                Resources["TextMutedBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#64748B")!;
                Resources["TextAccentBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#0284C7")!;

                Resources["InputBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["InputBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["InputForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#0F172A")!;
                Resources["PopupBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["PopupBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["ItemSelectedBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#E0F2FE")!;
                Resources["ItemSelectedForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#0078D4")!;

                Resources["ButtonBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["ButtonBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["ButtonForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1E293B")!;
                Resources["ButtonHoverBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F1F5F9")!;
                Resources["ButtonHoverBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#94A3B8")!;

                Resources["DangerButtonBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FEE2E2")!;
                Resources["DangerButtonBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FCA5A5")!;
                Resources["DangerButtonForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#DC2626")!;

                Resources["BadgeBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["BadgeBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["BadgeForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#334155")!;

                Resources["BannerBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#EFF6FF")!;
                Resources["BannerBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#93C5FD")!;
                Resources["BannerForegroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#1E40AF")!;

                Resources["StatsBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F0F9FF")!;
                Resources["StatsBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#7DD3FC")!;
                Resources["StatsBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#0369A1")!;
                Resources["StatsBoxTextBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#334155")!;

                Resources["OrphanedBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FFFBEB")!;
                Resources["OrphanedBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#FCD34D")!;
                Resources["OrphanedBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#B45309")!;
                Resources["OrphanedBoxTextBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#78350F")!;

                Resources["ModelBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F0FDF4")!;
                Resources["ModelBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#86EFAC")!;
                Resources["ModelBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#15803D")!;

                Resources["HwBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F0F9FF")!;
                Resources["HwBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#7DD3FC")!;
                Resources["HwBoxHeaderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#0369A1")!;

                Resources["LogsBoxBackgroundBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F8FAFC")!;
                Resources["LogsBoxBorderBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["LogsBoxTextBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#475569")!;

                Resources["ScrollThumbBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["ScrollThumbHoverBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#94A3B8")!;
                Resources["ExpanderHeaderHoverBrush"] = (System.Windows.Media.Brush)bc.ConvertFrom("#F1F5F9")!;

                Background = (System.Windows.Media.Brush)Resources["WindowBackgroundBrush"];
                Foreground = (System.Windows.Media.Brush)Resources["WindowForegroundBrush"];
            }
            UpdateLanguageToggleButtons(_settings.Language);
        }
        catch { }
    }
}
