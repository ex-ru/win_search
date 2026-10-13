// Settings, changed once and saved. Hotkey box records Space with
// modifiers; everything applies on Save (hotkey and folders at once).

using System.Diagnostics;
using System.IO;
using System.Windows;
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

        SliderOpacity.Value = Math.Clamp((int)Math.Round(_settings.WidgetOpacity * 100), 10, 100);
        TxtOpacityValue.Text = $"{(int)SliderOpacity.Value}%";
        ChkWidgetBlur.IsChecked = _settings.WidgetBlur;

        ComboTheme.ItemsSource = new[] { "Системная (как в Windows)", "Тёмная (Dark)", "Светлая (Light)" };
        ComboTheme.SelectedIndex = _settings.Theme switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0
        };

        UpdateLanguageToggleButtons(_settings.Language);
        UpdateFolderStats();
        InitHardwareOptions();

        Updates.IsChecked = settings.CheckUpdates;
        UpdateSource.Text = settings.UpdateSource;
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
            "DigUp — Windows Port\n" +
            "Версия: 1.0.7\n\n" +
            "Быстрый локальный семантический и лексический поиск по файлам,\n" +
            "документам, коду и изображениям с поддержкой RAG и MCP.\n\n" +
            "Репозиторий GitHub:\nhttps://github.com/ex-ru/win_search\n\n" +
            "Лицензия: MIT License\n\n" +
            "Открыть страницу проекта на GitHub?";

        var res = owner is not null
            ? MessageBox.Show(owner, message, "О программе DigUp", MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(message, "О программе DigUp", MessageBoxButton.YesNo, MessageBoxImage.Information);

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
        bool isRu = Loc.IsRu;
        Title = isRu ? "DigUp — Настройки" : "DigUp — Settings";
        if (BtnApply is not null) BtnApply.Content = isRu ? "Применить" : "Apply";
        if (BtnCancel is not null) BtnCancel.Content = isRu ? "Отмена" : "Cancel";
        if (BtnSave is not null) BtnSave.Content = isRu ? "Сохранить" : "Save";
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
        DialogResult = false;
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
        DialogResult = true;
        Close();
    }
}
