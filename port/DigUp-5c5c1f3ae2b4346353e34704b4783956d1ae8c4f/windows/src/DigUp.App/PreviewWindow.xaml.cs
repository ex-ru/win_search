// Quick Look for Windows: Space shows the selected file itself — a picture
// full-size, a PDF at the matched page, sound/video playing from the
// moment, code around the matched lines — and follows the selection while
// open, like the Mac panel. Space/Esc closes, Enter opens. Never throws.

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DigUp.App;

public partial class PreviewWindow : Window
{
    private readonly string _pdfiumDll;
    private readonly Action _open;
    private readonly Action<int>? _onNavigate;
    private string? _tempPng;
    private double _moment;
    private System.Windows.Threading.DispatcherTimer? _clock;
    private Engine.Hit? _currentHit;

    public Engine.Hit? CurrentHit => _currentHit;

    public PreviewWindow(string pdfiumDll, Action open, Action<int>? onNavigate = null)
    {
        _pdfiumDll = pdfiumDll;
        _open = open;
        _onNavigate = onNavigate;
        InitializeComponent();
        try { Icon = AppIcon.Wpf; } catch { }

        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key is Key.Space && !Words.IsFocused) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && !Words.IsFocused) { _open(); e.Handled = true; }
            else if (e.Key == Key.Down && !Words.IsFocused) { _onNavigate?.Invoke(1); e.Handled = true; }
            else if (e.Key == Key.Up && !Words.IsFocused) { _onNavigate?.Invoke(-1); e.Handled = true; }
            else if (e.Key == Key.E && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OnOpenInExplorer(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OnSaveAs(this, new RoutedEventArgs()); e.Handled = true; }
        };

        Player.MediaOpened += (_, _) =>
        {
            try
            {
                if (_moment > 0 && _moment < Player.NaturalDuration.TimeSpan.TotalSeconds)
                    Player.Position = TimeSpan.FromSeconds(_moment);
                Player.Play();
                PlayPause.Content = "Pause";
            }
            catch { }
        };
        Player.MediaFailed += (_, _) =>
        {
            Words.Text = "(этот файл не воспроизводится здесь — нажмите Enter для открытия в стандартной программе)";
            Words.Visibility = Visibility.Visible;
            WordsScroll.Visibility = Visibility.Visible;
            Player.Visibility = Visibility.Collapsed;
            Transport.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) => Cleanup();
    }

    /// <summary>Show another hit (follows the panel's selection).</summary>
    public void ShowHit(Engine.Hit hit, string query = "")
    {
        _currentHit = hit;
        Cleanup();
        ResetViews();
        string fileName = Path.GetFileName(hit.Path);
        HeaderTitle.Text = string.IsNullOrEmpty(fileName) ? hit.Path : fileName;
        base.Title = $"{HeaderTitle.Text} — Предпросмотр";
        DocTitle.Text = hit.Path + (hit.Where.Length > 0 ? "  " + hit.Where : "");
        Kind.Text = KindLine(hit);
        try
        {
            switch (PreviewContent.For(hit, _pdfiumDll, query))
            {
                case PreviewModel.Image img:
                    ShowPicture(img.Path, temp: false);
                    break;
                case PreviewModel.TempImage tmp:
                    ShowPicture(tmp.Path, temp: true);
                    break;
                case PreviewModel.Media m:
                    ShowMedia(m);
                    break;
                case PreviewModel.Text t:
                    ShowWords(t.Body, mono: hit.Kind == "Code", at: t.Highlight, length: t.HighlightLength);
                    break;
                case PreviewModel.None n:
                    ShowWords(n.Note, mono: false, at: -1, length: 0);
                    break;
            }
        }
        catch
        {
            ShowWords("(предпросмотр недоступен — нажмите Enter для открытия в стандартной программе)", mono: false, at: -1, length: 0);
        }
    }

    private void ResetViews()
    {
        Picture.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Collapsed;
        Words.Visibility = Visibility.Collapsed;
        WordsScroll.Visibility = Visibility.Collapsed;
        Transport.Visibility = Visibility.Collapsed;
    }

    private void ShowPicture(string path, bool temp)
    {
        if (temp) _tempPng = path;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 1600;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            Picture.Source = bmp;
            Picture.Visibility = Visibility.Visible;
        }
        catch
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    var frame = decoder.Frames[0];
                    frame.Freeze();
                    Picture.Source = frame;
                    Picture.Visibility = Visibility.Visible;
                    return;
                }
            }
            catch { }
            throw;
        }
    }

    private void ShowMedia(PreviewModel.Media m)
    {
        _moment = m.Seconds;
        Player.Visibility = m.IsVideo ? Visibility.Visible : Visibility.Collapsed;
        Transport.Visibility = Visibility.Visible;
        Time.Text = m.IsVideo ? "" : $"звук · с {Stamp(m.Seconds)}";
        try
        {
            Player.Source = new Uri(m.Path);
            // Position applies in MediaOpened (the file isn't ready before).
        }
        catch
        {
            Words.Text = "(этот файл не воспроизводится здесь — нажмите Enter для открытия в стандартной программе)";
            Words.Visibility = Visibility.Visible;
            WordsScroll.Visibility = Visibility.Visible;
            Player.Visibility = Visibility.Collapsed;
        }
        StartClock();
    }

    private void ShowWords(string body, bool mono, int at, int length)
    {
        Words.FontFamily = new System.Windows.Media.FontFamily(
            mono ? "Cascadia Mono, Consolas, Courier New" : "Segoe UI");
        Words.TextWrapping = mono ? TextWrapping.NoWrap : TextWrapping.Wrap;
        Words.Text = body;
        Words.Visibility = Visibility.Visible;
        WordsScroll.Visibility = Visibility.Visible;
        // Jump to the relevant passage and mark it.
        if (at >= 0 && at < body.Length && length > 0)
        {
            try
            {
                Words.Select(at, Math.Min(length, body.Length - at));
                Words.ScrollToLine(Words.GetLineIndexFromCharacterIndex(at));
            }
            catch { }
        }
    }

    private static string KindLine(Engine.Hit h) => h.Kind switch
    {
        "Video" or "Audio" => $"{h.Kind} · с момента {Stamp(MainWindow.ParseSeconds(h.Where))}",
        "Pdf" => $"PDF · страница {(int)(MainWindow.ParsePage(h.Where) ?? 1)}",
        "Code" => $"Код · {h.Where}",
        _ => h.Kind,
    };

    private static string Stamp(double seconds)
    {
        int s = Math.Max(0, (int)Math.Round(seconds));
        return $"{s / 60}:{s % 60:D2}";
    }

    private void StartClock()
    {
        StopClock();
        _clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) =>
        {
            try
            {
                if (Player.Source is not null && Player.NaturalDuration.HasTimeSpan)
                    Time.Text = $"{Stamp(Player.Position.TotalSeconds)} / {Stamp(Player.NaturalDuration.TimeSpan.TotalSeconds)}";
            }
            catch { }
        };
        _clock.Start();
    }

    private void StopClock() { try { _clock?.Stop(); } catch { } _clock = null; }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        try
        {
            bool playing = (string)PlayPause.Content == "Pause";
            if (playing) Player.Pause(); else Player.Play();
            PlayPause.Content = playing ? "Play" : "Pause";
        }
        catch { }
    }

    public bool IsPinned { get; private set; }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        IsPinned = !IsPinned;
        if (BtnPin is not null)
        {
            BtnPin.Opacity = IsPinned ? 1.0 : 0.6;
            BtnPin.Background = IsPinned ? (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#0078D4")! : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#383838")!;
            BtnPin.ToolTip = IsPinned ? "Зафиксировано (окно не переключается при смене файла в поиске)" : "Зафиксировать окно (не переключать при навигации поиска)";
        }
        if (MenuPin is not null)
        {
            MenuPin.IsChecked = IsPinned;
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => _open();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        BtnMaximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (e.ClickCount == 2)
            {
                OnMaximize(sender, e);
            }
            else
            {
                try { DragMove(); } catch { }
            }
        }
    }

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (_currentHit is null) return;
        Editors.OpenInExplorer(_currentHit.Path);
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_currentHit is null) return;
        string sourcePath = Editors.Native(_currentHit.Path);
        if (!File.Exists(sourcePath))
        {
            System.Windows.MessageBox.Show(this, "Исходный файл не найден на диске.", "Сохранить как", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string originalExt = Path.GetExtension(sourcePath);
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(sourcePath),
            Filter = string.IsNullOrEmpty(originalExt) ? "Все файлы (*.*)|*.*" : $"Файл (*{originalExt})|*{originalExt}|Все файлы (*.*)|*.*",
            Title = "Сохранить копию файла как…"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                File.Copy(sourcePath, sfd.FileName, overwrite: true);
                System.Windows.MessageBox.Show(this, $"Файл успешно сохранен:\n{sfd.FileName}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, $"Ошибка сохранения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_currentHit is null) return;
        try
        {
            System.Windows.Clipboard.SetText(Editors.Native(_currentHit.Path));
        }
        catch { }
    }

    private void Cleanup()
    {
        StopClock();
        try { Player.Stop(); } catch { }
        try { Player.Source = null; } catch { }
        try { Player.Close(); } catch { }
        Picture.Source = null;
        if (_tempPng is not null)
        {
            try { File.Delete(_tempPng); } catch { }
            _tempPng = null;
        }
    }
}
