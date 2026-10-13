// Search overlay: dimmed + blurred screen behind, crisp card in front.
// Type, arrows, Enter opens (PDF at page, code at line, sound/video note
// their moment), Esc or backdrop click hides. The preview shows a thumbnail
// for pictures and video frames, words for documents and code.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DigUp.Core;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;

namespace DigUp.App;

public sealed class ResultItemViewModel
{
    public required Engine.Hit Hit { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string BadgeText { get; init; }
    public required System.Windows.Media.Brush BadgeBackground { get; init; }
    public required System.Windows.Media.Brush BadgeBorder { get; init; }
    public required System.Windows.Media.Brush BadgeForeground { get; init; }
}

public partial class MainWindow : Window
{
    private readonly Engine _engine;
    private readonly Settings _settings;
    private readonly System.Timers.Timer _debounce = new(180) { AutoReset = false };
    private List<ResultItemViewModel> _allHits = [];
    private List<ResultItemViewModel> _currentItems = [];
    private string _activeFilter = "all";
    private bool _big;
    private int _searchGen; // only the newest background search may touch the list
    private PreviewWindow? _preview;
    // Mac rule: once arrows/click moved the selection, Space previews
    // instead of typing a space. Any edit (or a click back in the field)
    // hands Space back to typing.
    private bool _navigated;

    public MainWindow(Engine engine, Settings settings)
    {
        _engine = engine; _settings = settings;
        InitializeComponent();
        try { Icon = AppIcon.Wpf; } catch { }
        _engine.Changed += () => Dispatcher.Invoke(() => Status.Text = _engine.Status);
        Status.Text = _engine.Status;
        _debounce.Elapsed += (_, _) => Dispatcher.Invoke(SearchNow);
        Query.TextChanged += (_, _) =>
        {
            Interlocked.Increment(ref _searchGen);
            _navigated = false;
            ClearButton.Visibility = string.IsNullOrEmpty(Query.Text) ? Visibility.Collapsed : Visibility.Visible;
            _debounce.Stop();
            if (string.IsNullOrWhiteSpace(Query.Text)) SearchNow();
            else _debounce.Start();
        };
        Query.PreviewKeyDown += OnKey;
        Query.PreviewMouseLeftButtonDown += (_, _) => _navigated = false;
        Results.MouseDoubleClick += (_, _) => OpenSelected();
        Results.PreviewMouseLeftButtonDown += (_, _) => _navigated = true;
        Results.SelectionChanged += (_, _) => { ShowPreview(); FollowPreview(); };
        Results.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { TogglePreview(); e.Handled = true; }
        };
        Loaded += (_, _) => { AdjustCardSize(); FadeIn(); };
        SizeChanged += (_, _) => AdjustCardSize();
    }

    private void AdjustCardSize()
    {
        try
        {
            double screenH = SystemParameters.WorkArea.Height;
            Results.MaxHeight = Math.Clamp(screenH * 0.44, 340, 620);
        }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Topmost = _isPinned;
        ApplyWidgetAppearance();
    }

    /// <summary>Apply layout adjustments and appearance when search opens.</summary>
    public void ApplyLook()
    {
        try
        {
            AdjustCardSize();
            ApplyWidgetAppearance();
        }
        catch { }
    }

    private void ApplyWidgetAppearance()
    {
        try
        {
            WindowBlurHelper.SetBlur(this, _settings.WidgetBlur);

            // Theme application
            bool isDark = ThemeManager.IsDark(_settings.Theme);
            ApplyTheme(isDark);
        }
        catch { }
    }

    private void ApplyTheme(bool isDark)
    {
        try
        {
            var bc = new BrushConverter();
            if (isDark)
            {
                Root.Background = (Brush)bc.ConvertFrom("#242424")!;
                Root.BorderBrush = (Brush)bc.ConvertFrom("#444444")!;
                Query.Background = (Brush)bc.ConvertFrom("#333333")!;
                Query.Foreground = Brushes.White;
                Query.CaretBrush = Brushes.White;
                Status.Foreground = (Brush)bc.ConvertFrom("#888888")!;
            }
            else
            {
                Root.Background = (Brush)bc.ConvertFrom("#F9F9F9")!;
                Root.BorderBrush = (Brush)bc.ConvertFrom("#D0D0D0")!;
                Query.Background = (Brush)bc.ConvertFrom("#FFFFFF")!;
                Query.Foreground = (Brush)bc.ConvertFrom("#1A1A1A")!;
                Query.CaretBrush = (Brush)bc.ConvertFrom("#1A1A1A")!;
                Status.Foreground = (Brush)bc.ConvertFrom("#555555")!;
            }
        }
        catch { }
    }

    private void FadeIn()
    {
        ApplyWidgetAppearance();
        double target = Math.Clamp(_settings.WidgetOpacity, 0.6, 1.0);
        var anim = new DoubleAnimation(0, target, new Duration(TimeSpan.FromMilliseconds(130)));
        Root.BeginAnimation(OpacityProperty, anim);
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private bool _isPinned = true;
    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        BtnPin.Opacity = _isPinned ? 1.0 : 0.6;
        BtnPin.Background = _isPinned
            ? (Brush)new BrushConverter().ConvertFrom("#0078D4")!
            : (Brush)new BrushConverter().ConvertFrom("#383838")!;
        BtnPin.ToolTip = _isPinned ? "Открепить от верха" : "Закрепить поверх всех окон";
    }

    private void OnWidgetMinimize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnWidgetClose(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnOpenWithButton(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count) return;
        try
        {
            Editors.OpenWith(_currentItems[Results.SelectedIndex].Hit.Path);
        }
        catch (Exception ex) { Complain("не удалось открыть меню выбора программы", ex); }
    }

    public void FocusQuery()
    {
        ClearButton.Visibility = string.IsNullOrEmpty(Query.Text) ? Visibility.Collapsed : Visibility.Visible;
        Query.Focus();
        Query.SelectAll();
    }

    private void OnClearQuery(object sender, RoutedEventArgs e)
    {
        Query.Text = string.Empty;
        Query.Focus();
        SearchNow();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string tag = btn.Name switch
        {
            nameof(FilterPdf) => "pdf",
            nameof(FilterImg) => "img",
            nameof(FilterDoc) => "doc",
            nameof(FilterCode) => "code",
            nameof(FilterMedia) => "media",
            _ => "all"
        };

        _activeFilter = tag;
        foreach (var child in FilterBar.Children)
        {
            if (child is Button b)
                b.Tag = ReferenceEquals(b, btn) ? "Active" : string.Empty;
        }

        ApplyCurrentFilter();
        Query.Focus();
    }

    private void ApplyCurrentFilter()
    {
        _currentItems = _activeFilter switch
        {
            "pdf" => _allHits.Where(x => x.BadgeText == "PDF").ToList(),
            "img" => _allHits.Where(x => x.BadgeText is "IMG" or "СКРИН").ToList(),
            "doc" => _allHits.Where(x => x.BadgeText is "DOC" or "TXT").ToList(),
            "code" => _allHits.Where(x => x.BadgeText == "CODE").ToList(),
            "media" => _allHits.Where(x => x.BadgeText is "ВИДЕО" or "АУДИО").ToList(),
            _ => _allHits.ToList()
        };

        Results.ItemsSource = _currentItems;
        if (_currentItems.Count > 0) Results.SelectedIndex = 0;
        else Results.SelectedIndex = -1;

        ShowPreview();
        FollowPreview();
    }

    private void UpdateFilterCounts()
    {
        int allCount = _allHits.Count;
        int pdfCount = _allHits.Count(x => x.BadgeText == "PDF");
        int imgCount = _allHits.Count(x => x.BadgeText is "IMG" or "СКРИН");
        int docCount = _allHits.Count(x => x.BadgeText is "DOC" or "TXT");
        int codeCount = _allHits.Count(x => x.BadgeText == "CODE");
        int mediaCount = _allHits.Count(x => x.BadgeText is "ВИДЕО" or "АУДИО");

        FilterAll.Content = allCount > 0 ? $"Все ({allCount})" : "Все";
        FilterPdf.Content = pdfCount > 0 ? $"📄 PDF ({pdfCount})" : "📄 PDF";
        FilterImg.Content = imgCount > 0 ? $"🖼️ Картинки ({imgCount})" : "🖼️ Картинки";
        FilterDoc.Content = docCount > 0 ? $"📝 Текст / Док ({docCount})" : "📝 Текст / Док";
        FilterCode.Content = codeCount > 0 ? $"💻 Код ({codeCount})" : "💻 Код";
        FilterMedia.Content = mediaCount > 0 ? $"🎬 Медиа ({mediaCount})" : "🎬 Медиа";
    }

    // The query box never waits: each keystroke starts a background search,
    // and only the newest one paints (older ones throw their work away).
    private void SearchNow()
    {
        int gen = Interlocked.Increment(ref _searchGen);
        string q = Query.Text.Trim();
        if (q.Length == 0)
        {
            _allHits.Clear();
            _currentItems.Clear();
            Results.ItemsSource = null;
            PreviewImage.Source = null;
            PreviewText.Text = string.Empty;
            UpdateFilterCounts();
            ShowPreview();
            FollowPreview();
            return;
        }
        Task.Run(() =>
        {
            List<Engine.Hit> hits;
            try { hits = _engine.Search(q, limit: 50); }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (gen == _searchGen)
                    {
                        ErrorLog.Write("search: " + ex);
                        Status.Text = "search stopped: " + ex.Message.Split('\n')[0];
                    }
                });
                return;
            }
            Dispatcher.Invoke(() =>
            {
                if (gen != _searchGen) return; // a newer keystroke won
                _allHits = hits.Select(CreateItem).ToList();
                UpdateFilterCounts();
                ApplyCurrentFilter();

                if (CodeQuery.TextOf(q) is null && _allHits.Count > 0
                    && _allHits.All(h => h.Hit.Kind == "Code"))
                    Status.Text = "только код совпадает — подсказка: code: ищет по коду, обычные слова — по остальным файлам";
            });
        });
    }

    private static ResultItemViewModel CreateItem(Engine.Hit h)
    {
        string ext = Path.GetExtension(h.Path).ToLowerInvariant();
        string badgeText;
        string bg;
        string border;
        string fg;

        if (h.Kind == "Pdf" || ext == ".pdf")
        {
            badgeText = "PDF";
            bg = "#3D1818";
            border = "#E53935";
            fg = "#FF8A80";
        }
        else if (h.Kind == "Screenshot")
        {
            badgeText = "СКРИН";
            bg = "#0D2E35";
            border = "#00ACC1";
            fg = "#80DEEA";
        }
        else if (h.Kind == "Image" || ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tiff" or ".tif" or ".svg")
        {
            badgeText = "IMG";
            bg = "#2A183D";
            border = "#AB47BC";
            fg = "#E1BEE7";
        }
        else if (h.Kind == "Code" || ext is ".cs" or ".js" or ".ts" or ".py" or ".cpp" or ".c" or ".h" or ".rs" or ".go" or ".java" or ".html" or ".css" or ".php" or ".sh" or ".ps1")
        {
            badgeText = "CODE";
            bg = "#182E19";
            border = "#43A047";
            fg = "#A5D6A7";
        }
        else if (h.Kind == "Video" || ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm")
        {
            badgeText = "ВИДЕО";
            bg = "#3D2714";
            border = "#FB8C00";
            fg = "#FFCC80";
        }
        else if (h.Kind == "Audio" || ext is ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac")
        {
            badgeText = "АУДИО";
            bg = "#3A162B";
            border = "#E91E63";
            fg = "#F48FB1";
        }
        else if (ext is ".txt" or ".md" or ".markdown" or ".log" or ".csv" or ".tsv" or ".json" or ".yaml" or ".yml" or ".ini")
        {
            badgeText = "TXT";
            bg = "#1A2E26";
            border = "#26A69A";
            fg = "#80CBC4";
        }
        else
        {
            badgeText = "DOC";
            bg = "#0D243D";
            border = "#1E88E5";
            fg = "#90CAF9";
        }

        var bc = new BrushConverter();
        var bgBrush = (SolidColorBrush)(bc.ConvertFrom(bg) ?? Brushes.DarkGray);
        var borderBrush = (SolidColorBrush)(bc.ConvertFrom(border) ?? Brushes.Gray);
        var fgBrush = (SolidColorBrush)(bc.ConvertFrom(fg) ?? Brushes.White);
        bgBrush.Freeze();
        borderBrush.Freeze();
        fgBrush.Freeze();

        return new ResultItemViewModel
        {
            Hit = h,
            Title = Short(h.Path, 100) + (h.Where.Length > 0 ? "  " + h.Where : ""),
            Detail = h.Kind + (h.Excerpt is null ? "" : " — " + EncodingHelper.FixMojibake(h.Excerpt)),
            BadgeText = badgeText,
            BadgeBackground = bgBrush,
            BadgeBorder = borderBrush,
            BadgeForeground = fgBrush
        };
    }

    private void ShowPreview()
    {
        if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count)
        {
            PreviewBox.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewBox.Visibility = Visibility.Visible;
        var item = _currentItems[Results.SelectedIndex];
        var h = item.Hit;
        PreviewTitle.Text = Short(h.Path, 100) + (h.Where.Length > 0 ? "  " + h.Where : "");
        PreviewKind.Text = KindText(h);
        PreviewText.Text = (h.Excerpt is not null ? EncodingHelper.FixMojibake(h.Excerpt) : null) ?? "(нет текстового фрагмента — найдено по смыслу / изображению)";
        PreviewImage.Source = null;
        // Thumbnails load off the UI thread; a late one for an old selection is dropped.
        int at = Results.SelectedIndex;
        int gen = _searchGen;
        Task.Run(() => ThumbFor(h)).ContinueWith(t =>
        {
            string? file;
            try { file = t.Result; }
            catch { return; }
            if (file is null || gen != _searchGen || at != Results.SelectedIndex
                || at >= _currentItems.Count || !ReferenceEquals(h, _currentItems[at].Hit)) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 440;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                if (gen != _searchGen || at != Results.SelectedIndex) return;
                PreviewImage.Source = bmp;
            }
            catch { }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string? ThumbFor(Engine.Hit h)
    {
        try
        {
            string ext = Path.GetExtension(h.Path).ToLowerInvariant();
            if (h.Kind is "Image" or "Screenshot" || ext is ".jpg" or ".jpeg" or ".png" or ".webp")
                return Thumbs.ForImage(h.Path);
            if (h.Kind == "Video")
                return Thumbs.ForVideo(h.Path, ParseSeconds(h.Where));
            return null;
        }
        catch { return null; }
    }

    private static string KindText(Engine.Hit h) => h.Kind switch
    {
        "Video" or "Audio" => $"{h.Kind} · с момента {h.Where}",
        "Pdf" => $"PDF · страница {h.Where.TrimStart('@')}",
        "Code" => $"Код · {h.Where}",
        _ => h.Kind,
    };

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        else if (e.Key == Key.Space && (_navigated || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            TogglePreview();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Results.Items.Count > 0)
        {
            Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1);
            Results.ScrollIntoView(Results.SelectedItem);
            _navigated = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Results.Items.Count > 0)
        {
            Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
            Results.ScrollIntoView(Results.SelectedItem);
            _navigated = true;
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _big = !_big;
            Width = _big ? 1100 : 880;
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Results.SelectedIndex >= 0)
        {
            try
            {
                var cur = _currentItems[Results.SelectedIndex].Hit.Path;
                System.Windows.Clipboard.SetFileDropList([cur]);
            }
            catch { }
        }
    }

    public void NavigateSelection(int delta)
    {
        if (Results.Items.Count == 0) return;
        int next = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1);
        if (next != Results.SelectedIndex)
        {
            Results.SelectedIndex = next;
            Results.ScrollIntoView(Results.SelectedItem);
        }
    }

    internal static string FindPdfium()
    {
        string p1 = Path.Combine(AppContext.BaseDirectory, "pdfium.dll");
        if (File.Exists(p1)) return p1;
        string p2 = Path.Combine(AppContext.BaseDirectory, "native", "x64", "pdfium.dll");
        if (File.Exists(p2)) return p2;
        string p3 = @"D:\_projects\win_search\run-vulkan\pdfium.dll";
        if (File.Exists(p3)) return p3;
        string p4 = @"D:\_projects\win_search\port\DigUp-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\native\x64\pdfium.dll";
        if (File.Exists(p4)) return p4;
        return p1;
    }

    private readonly List<PreviewWindow> _openPreviews = [];

    /// <summary>Space: show the selected file itself, or hide it again. Shift+Space opens a new standalone preview window.</summary>
    private void TogglePreview()
    {
        try
        {
            if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count) return;
            var currentHit = _currentItems[Results.SelectedIndex].Hit;
            bool forceNew = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // If an active (non-minimized) preview is currently showing and not pinned, update or close it
            if (!forceNew && _preview is not null && _preview.IsLoaded && _preview.WindowState != WindowState.Minimized && !_preview.IsPinned)
            {
                if (_preview.CurrentHit?.Path == currentHit.Path)
                {
                    _preview.Close();
                    _preview = null;
                    return;
                }
                else
                {
                    _preview.ShowHit(currentHit, Query.Text);
                    return;
                }
            }

            // Otherwise, open a new preview window
            var win = new PreviewWindow(FindPdfium(), OpenSelected, (delta) => NavigateSelection(delta));
            _openPreviews.Add(win);
            _preview = win;
            win.Closed += (_, _) =>
            {
                _openPreviews.Remove(win);
                if (ReferenceEquals(_preview, win))
                {
                    _preview = _openPreviews.FirstOrDefault(p => p.WindowState != WindowState.Minimized && !p.IsPinned);
                }
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        this.Activate();
                        Query.Focus();
                    }
                    catch { }
                });
            };
            win.ShowHit(currentHit, Query.Text);
            win.Show();
            win.Focus();
        }
        catch (Exception ex)
        {
            Status.Text = "предпросмотр недоступен: " + ex.Message.Split('\n')[0].Trim();
        }
    }

    /// <summary>The open preview follows the selection, like Quick Look.</summary>
    private void FollowPreview()
    {
        if (_preview is null || !_preview.IsLoaded || _preview.WindowState == WindowState.Minimized || _preview.IsPinned) return;
        try
        {
            if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count) _preview.Close();
            else _preview.ShowHit(_currentItems[Results.SelectedIndex].Hit, Query.Text);
        }
        catch { try { _preview.Close(); } catch { } }
    }

    private void OnQuickLookButton(object sender, RoutedEventArgs e) => TogglePreview();

    private void OnPreviewImageClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) TogglePreview();
    }

    private void OnOpenButton(object sender, RoutedEventArgs e) => OpenSelected();

    private void Complain(string what, Exception ex)
    {
        string msg = $"{what}: {ex.Message.Split('\n')[0].Trim()}";
        Status.Text = msg;
        if (System.Windows.Application.Current is App app) app.ShowNote(msg);
    }

    private void OnShowButton(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count) return;
        try
        {
            Editors.OpenInExplorer(_currentItems[Results.SelectedIndex].Hit.Path);
            Hide();
        }
        catch (Exception ex) { Complain("не удалось открыть проводник", ex); }
    }

    private void OnCopyButton(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count) return;
        try
        {
            System.Windows.Clipboard.SetFileDropList([_currentItems[Results.SelectedIndex].Hit.Path]);
        }
        catch (Exception ex) { Complain("не удалось скопировать", ex); }
    }

    private void OpenSelected()
    {
        if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count) return;
        var h = _currentItems[Results.SelectedIndex].Hit;
        try
        {
            if (h.Kind == "Code")
            {
                int line = h.Where.StartsWith("lines ") ? ParseLine(h.Where) : 1;
                var t = Editors.ForCode(_settings.Editor, h.Path, line);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(t.Exe, t.Args) { UseShellExecute = true });
            }
            else if (h.Kind == "Pdf") Editors.OpenPdf(h.Path, ParsePage(h.Where));
            else Editors.Open(h.Path);
            Hide();
        }
        catch (Exception ex) { Complain($"не удалось открыть {Short(h.Path, 60)}", ex); }
    }

    internal static int ParseLine(string where)
    {
        var digits = new string(where.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out int n) ? Math.Max(1, n) : 1;
    }

    internal static double? ParsePage(string where)
    {
        var digits = new string(where.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, out double n) ? n : null;
    }

    internal static double ParseSeconds(string where)
    {
        var digits = new string(where.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.' || c == ':').ToArray());
        if (digits.Contains(':') && TimeSpan.TryParse("0:" + digits, out var ts)) return ts.TotalSeconds;
        return double.TryParse(digits, out double n) ? n : 0;
    }

    private static string Short(string p, int max = 80) => p.Length > max ? "…" + p[^Math.Max(1, max - 1)..] : p;

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Dispose();
        base.OnClosed(e);
    }
}
