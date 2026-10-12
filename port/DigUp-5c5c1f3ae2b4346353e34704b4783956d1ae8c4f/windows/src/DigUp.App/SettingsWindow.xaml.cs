// Settings, changed once and saved. Hotkey box records Space with
// modifiers; everything applies on Save (hotkey and folders at once).

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace DigUp.App;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Engine? _engine;
    private string _hotkey;

    public SettingsWindow(Settings settings, Engine? engine = null)
    {
        _settings = settings;
        _engine = engine;
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

        UiLanguage.ItemsSource = new[] { "Русский (RU)", "English (EN)" };
        UiLanguage.SelectedIndex = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        Updates.IsChecked = settings.CheckUpdates;
        UpdateSource.Text = settings.UpdateSource;
        Startup.IsChecked = settings.StartWithWindows;
    }

    private void RefreshFolders()
    {
        Folders.ItemsSource = null;
        Folders.ItemsSource = _settings.Folders.ToList();
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
            "Версия: 1.0.5\n\n" +
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

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
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
        _settings.Language = UiLanguage.SelectedIndex == 1 ? "en" : "ru";
        Loc.CurrentLanguage = _settings.Language;
        _settings.CheckUpdates = Updates.IsChecked == true;
        if (!string.Equals(_settings.UpdateSource, updateSource, StringComparison.OrdinalIgnoreCase))
            _settings.LastUpdateCheck = null;
        _settings.UpdateSource = updateSource;
        _settings.StartWithWindows = Startup.IsChecked == true;
        _settings.Save();
        DialogResult = true;
        Close();
    }
}
