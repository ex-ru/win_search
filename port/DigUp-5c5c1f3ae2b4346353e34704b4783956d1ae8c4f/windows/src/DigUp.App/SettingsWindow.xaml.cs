// Settings, changed once and saved. Hotkey box records Space with
// modifiers; everything applies on Save (hotkey and folders at once).

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DigUp.Core;
using MessageBox = System.Windows.MessageBox;

namespace DigUp.App;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Engine? _engine;
    private readonly MainWindow? _mainWindow;
    private readonly Action? _onApplyCallback;
    private string _hotkey;

    public SettingsWindow(Settings settings, Engine? engine = null, MainWindow? mainWindow = null, Action? onApplyCallback = null)
    {
        _settings = settings;
        _engine = engine;
        _mainWindow = mainWindow;
        _onApplyCallback = onApplyCallback;
        _hotkey = string.IsNullOrWhiteSpace(settings.Hotkey) ? "ctrl+space" : settings.Hotkey;
        InitializeComponent();
        try { Icon = AppIcon.Wpf; } catch { }

        RefreshFolders();
        Hotkey.Text = Display(_hotkey);
        Hotkey.PreviewKeyDown += OnHotkeyKey;
        Hotkey.GotFocus += (_, _) => Hotkey.SelectAll();
        Code.IsChecked = settings.Code;
        Editor.ItemsSource = Settings.Editors;
        Editor.SelectedItem = Settings.Editors.Contains(settings.Editor) ? settings.Editor : "auto";

        SliderOpacity.Value = Math.Clamp((int)Math.Round(_settings.WidgetOpacity * 100), 50, 100);
        TxtOpacityValue.Text = $"{(int)SliderOpacity.Value}%";
        ChkWidgetBlur.IsChecked = _settings.WidgetBlur;

        ComboTheme.ItemsSource = new[] { "Системная (как в Windows)", "Тёмная (Dark)", "Светлая (Light)" };
        ComboTheme.SelectedIndex = _settings.Theme switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0
        };

        TextAppVersion.Text = $"v{AppConstants.Version} Windows";
        UpdateLanguageToggleButtons(_settings.Language);
        UpdateTextsForLanguage();
        ApplyTheme(ThemeManager.IsDark(_settings.Theme));
        UpdateFolderStats();
        InitModelOptions();
        InitHardwareOptions();

        Updates.IsChecked = settings.CheckUpdates;
        UpdateSource.Text = string.IsNullOrWhiteSpace(settings.UpdateSource) ? AppConstants.DefaultUpdateSource : settings.UpdateSource;
        Startup.IsChecked = settings.StartWithWindows;
    }

    private void RefreshFolders()
    {
        Folders.ItemsSource = null;
        Folders.ItemsSource = _settings.Folders.ToList();
        UpdateFolderStats();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Выберите папку для индексации DigUp" };
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
                "Пожалуйста, сначала выберите папку из списка выше.",
                "Очистка векторов папки",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(this,
            $"Очистить векторный индекс только для папки:\n{selectedFolder}?\n\n" +
            "Эмбеддинги для файлов в этой папке будут удалены и пересчитаны заново.",
            "Подтверждение сброса векторов папки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            if (_engine is not null)
            {
                _engine.ResetIndex([selectedFolder]);
                MessageBox.Show(this,
                    $"Векторный индекс для папки:\n{selectedFolder}\nуспешно сброшен. Фоновая переиндексация запущена.",
                    "Готово",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    "Индексатор в данный момент недоступен. Настройки сохранены.",
                    "Информация",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }

    private void OnClearAllVectors(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Вы действительно хотите очистить весь векторный индекс для ВСЕХ папок?\n\n" +
            "Все вычисленные семантические эмбеддинги будут удалены, а файлы помечены для повторной индексации.\n" +
            "Процесс может занять некоторое время в фоновом режиме.",
            "Подтверждение полного сброса векторов",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            if (_engine is not null)
            {
                _engine.ResetIndex(_settings.Folders);
                MessageBox.Show(this,
                    "Векторный индекс успешно очищен.\nФоновая переиндексация запущена.",
                    "Готово",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    "Индексатор в данный момент недоступен. Настройки сохранены.",
                    "Информация",
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
        string message =
            $"{AppConstants.AppFullName}\n" +
            $"Версия: {AppConstants.Version}\n\n" +
            "Быстрый локальный семантический и лексический поиск по файлам,\n" +
            "документам, коду и изображениям с поддержкой RAG и MCP.\n\n" +
            $"Репозиторий GitHub:\n{AppConstants.RepoUrl}\n\n" +
            "Лицензия: MIT License\n\n" +
            "Открыть страницу проекта на GitHub?";

        var res = owner is not null
            ? MessageBox.Show(owner, message, $"О программе {AppConstants.AppName}", MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(message, $"О программе {AppConstants.AppName}", MessageBoxButton.YesNo, MessageBoxImage.Information);

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
                Hotkey.Text += " — может конфликтовать со сменой раскладки Windows";
            e.Handled = true;
        }
        else if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Tab))
        {
            Hotkey.Text = "нажмите Пробел (например: с зажатым Ctrl)";
            e.Handled = true;
        }
    }

    private static string Display(string spec)
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
            if (TxtFolderStats is not null) TxtFolderStats.Text = "Индексатор недоступен.";
            return;
        }

        try
        {
            long dbBytes = _engine.GetDatabaseFileSize();
            double dbMb = Math.Round(dbBytes / (1024.0 * 1024.0), 1);
            if (TxtDbTotalSize is not null) TxtDbTotalSize.Text = $"База index.sqlite: {dbMb} МБ";

            if (Folders.SelectedItem is string selectedFolder)
            {
                var stats = _engine.GetFolderStats(selectedFolder);
                if (stats is not null && TxtFolderStats is not null)
                {
                    TxtFolderStats.Text = $"📁 {selectedFolder}\n" +
                        $"Проиндексировано файлов: {stats.FileCount:N0} · Векторов: {stats.VectorCount:N0} · Объем эмбеддингов: {stats.VectorMegabytes:F2} МБ";
                }
            }
            else
            {
                var total = _engine.GetTotalStats();
                if (total is not null && TxtFolderStats is not null)
                {
                    TxtFolderStats.Text = $"📁 Суммарно по всем папкам:\n" +
                        $"Файлов: {total.FileCount:N0} · Векторов: {total.VectorCount:N0} · Объем эмбеддингов: {total.VectorMegabytes:F2} МБ";
                }
            }
        }
        catch { }
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
        if (TxtHwTitle is not null) TxtHwTitle.Text = Loc.HwTitle;
        if (TxtHwDesc is not null) TxtHwDesc.Text = Loc.HwDesc;
        if (TxtHwDeviceLabel is not null) TxtHwDeviceLabel.Text = Loc.HwDeviceLabel;
        if (TxtHwStatusHeader is not null) TxtHwStatusHeader.Text = Loc.HwStatusHeader;
        if (TxtHwNote is not null) TxtHwNote.Text = Loc.HwNote;
        if (TxtAppearanceTitle is not null) TxtAppearanceTitle.Text = Loc.AppearanceTitle;
        if (TxtAppearanceDesc is not null) TxtAppearanceDesc.Text = Loc.AppearanceDesc;
        if (TxtOpacityLabel is not null) TxtOpacityLabel.Text = Loc.OpacityLabel;
        if (ChkWidgetBlur is not null) ChkWidgetBlur.Content = Loc.WidgetBlur;
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
        if (BtnAbout is not null) BtnAbout.Content = Loc.About;
        if (BtnApply is not null) BtnApply.Content = Loc.Apply;
        if (BtnCancel is not null) BtnCancel.Content = Loc.Cancel;
        if (BtnSave is not null) BtnSave.Content = Loc.Save;
    }

    private void UpdateLanguageToggleButtons(string lang)
    {
        bool isRu = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var activeBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#0078D4")!;
        var inactiveBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#282828")!;

        if (BtnLangRu is not null)
        {
            BtnLangRu.Background = isRu ? activeBrush : inactiveBrush;
            BtnLangRu.Foreground = isRu ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#888888")!;
        }
        if (BtnLangEn is not null)
        {
            BtnLangEn.Background = !isRu ? activeBrush : inactiveBrush;
            BtnLangEn.Foreground = !isRu ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#888888")!;
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

            // 1. Auto
            var (_, _, autoSummary) = HardwareDetector.ResolveBackend("auto", hwInfo);
            items.Add(new HardwareComboItem("auto", $"Автоопределение (Рекомендуется — {autoSummary})"));

            // 2. Discrete GPU (if available)
            if (hwInfo.DiscreteGpu != null)
            {
                var d = hwInfo.DiscreteGpu;
                items.Add(new HardwareComboItem("gpu_ext", $"Дискретная видеокарта: {d.Name} ({d.VramMb} МБ VRAM)"));
            }

            // 3. Integrated GPU (if available)
            if (hwInfo.IntegratedGpu != null)
            {
                var ig = hwInfo.IntegratedGpu;
                items.Add(new HardwareComboItem("gpu_int", $"Встроенная графика: {ig.Name}"));
            }

            // 4. CPU Only
            items.Add(new HardwareComboItem("cpu", $"Только процессор (CPU, {hwInfo.CpuCores} потоков)"));

            ComboHardware.ItemsSource = items;
            var selected = items.FirstOrDefault(x => string.Equals(x.Id, _settings.HardwareMode, StringComparison.OrdinalIgnoreCase)) ?? items[0];
            ComboHardware.SelectedItem = selected;

            TxtHardwareStatus.Text = hwInfo.Summary;
        }
        catch (Exception ex)
        {
            TxtHardwareStatus.Text = $"Ошибка детектора: {ex.Message}";
        }
    }

    private void InitModelOptions()
    {
        try
        {
            var models = ModelCatalog.All;
            ComboEmbeddingModel.ItemsSource = models;
            var current = models.FirstOrDefault(m => string.Equals(m.Id, _settings.EmbeddingModel, StringComparison.OrdinalIgnoreCase)) ?? models[0];
            ComboEmbeddingModel.SelectedItem = current;
            UpdateModelStatus(current);
        }
        catch (Exception ex)
        {
            TxtModelStatus.Text = $"Ошибка каталога моделей: {ex.Message}";
        }
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboEmbeddingModel.SelectedItem is EmbeddingModelDef model)
        {
            UpdateModelStatus(model);
            if (!string.Equals(model.Id, _settings.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            {
                string title = Loc.IsRu ? "Смена модели эмбеддингов" : "Switch Embedding Model";
                string dbName = _engine?.GetMainIndexFileName(model.Id) ?? $"index_{model.Id}.db";
                string msg = Loc.IsRu
                    ? $"Смена модели переключит векторный поиск на '{model.Name}'.\n\n" +
                      $"• Для этой модели будет использоваться отдельная изолированная база ('{dbName}').\n" +
                      $"• Все существующие индексы и векторы для предыдущей модели сохранятся и не будут удалены.\n" +
                      $"• Если веса модели ещё не загружены, начнётся их автоматическая загрузка с отображением прогресса.\n\n" +
                      $"Переключить модель на {model.Name}?"
                    : $"Changing the model will switch vector search to '{model.Name}'.\n\n" +
                      $"• An isolated index database ('{dbName}') will be used for this model.\n" +
                      $"• Existing indices for previous models will be preserved without loss.\n" +
                      $"• If model weights are missing, download will start automatically with progress.\n\n" +
                      $"Switch active model to {model.Name}?";

                var res = MessageBox.Show(this, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes)
                {
                    ComboEmbeddingModel.SelectionChanged -= OnModelSelectionChanged;
                    var prev = (ComboEmbeddingModel.ItemsSource as IEnumerable<EmbeddingModelDef>)?
                        .FirstOrDefault(m => string.Equals(m.Id, _settings.EmbeddingModel, StringComparison.OrdinalIgnoreCase));
                    if (prev is not null)
                    {
                        ComboEmbeddingModel.SelectedItem = prev;
                        UpdateModelStatus(prev);
                    }
                    ComboEmbeddingModel.SelectionChanged += OnModelSelectionChanged;
                    return;
                }
            }
        }
    }

    private void UpdateModelStatus(EmbeddingModelDef model)
    {
        bool downloaded = model.IsDownloaded();
        string statusIcon = downloaded ? "✅ Установлена и готова к работе" : "⏳ Требуется загрузка весов (GGUF)";
        TxtModelStatus.Text = $"{model.Name} — {statusIcon}";
        TxtModelDetails.Text = $"{model.Description} (Размерность векторов: {model.Dimension} dim, файл: {model.TextFile})";
    }

    private void ApplyCurrentSettings()
    {
        string updateSource = UpdateSource.Text.Trim();
        if (updateSource.Length > 0 && !Path.IsPathFullyQualified(updateSource) && !updateSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Укажите абсолютный путь к папке обновлений или оставьте пустым для GitHub.", "Настройки DigUp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.Hotkey = _hotkey;
        _settings.Code = Code.IsChecked == true;
        _settings.Editor = Editor.SelectedItem as string ?? "auto";
        _settings.Language = Loc.CurrentLanguage;
        _settings.WidgetOpacity = SliderOpacity.Value / 100.0;
        _settings.WidgetBlur = ChkWidgetBlur.IsChecked == true;
        _settings.Theme = ComboTheme.SelectedIndex switch
        {
            1 => ThemeManager.ThemeDark,
            2 => ThemeManager.ThemeLight,
            _ => ThemeManager.ThemeSystem
        };

        if (ComboEmbeddingModel.SelectedItem is EmbeddingModelDef modelItem)
        {
            bool modelChanged = !string.Equals(_settings.EmbeddingModel, modelItem.Id, StringComparison.OrdinalIgnoreCase);
            if (modelChanged)
            {
                _settings.EmbeddingModel = modelItem.Id;
                _engine?.ResetEmbedderWorker();
            }
        }

        if (ComboHardware.SelectedItem is HardwareComboItem hwItem)
        {
            bool modeChanged = !string.Equals(_settings.HardwareMode, hwItem.Id, StringComparison.OrdinalIgnoreCase);
            _settings.HardwareMode = hwItem.Id;
            if (modeChanged)
            {
                _engine?.ResetEmbedderWorker();
            }
        }

        _settings.CheckUpdates = Updates.IsChecked == true;
        if (!string.Equals(_settings.UpdateSource, updateSource, StringComparison.OrdinalIgnoreCase))
            _settings.LastUpdateCheck = null;
        _settings.UpdateSource = updateSource;
        _settings.StartWithWindows = Startup.IsChecked == true;
        _settings.Save();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ApplyCurrentSettings();
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
        ApplyCurrentSettings();
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
                Background = (System.Windows.Media.Brush)bc.ConvertFrom("#1E1E1E")!;
                Foreground = System.Windows.Media.Brushes.White;
            }
            else
            {
                Background = (System.Windows.Media.Brush)bc.ConvertFrom("#F8F9FA")!;
                Foreground = (System.Windows.Media.Brush)bc.ConvertFrom("#0F172A")!;
            }
        }
        catch { }
    }
}
