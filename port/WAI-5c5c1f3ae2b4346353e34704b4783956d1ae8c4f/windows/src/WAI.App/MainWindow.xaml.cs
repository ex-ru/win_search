// Search overlay: dimmed + blurred screen behind, crisp card in front.
// Type, arrows, Enter opens (PDF at page, code at line, sound/video note
// their moment), Esc or backdrop click hides. The preview shows a thumbnail
// for pictures and video frames, words for documents and code.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WAI.Core;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;

namespace WAI.App;

public sealed class ResultItemViewModel
{
    public required Engine.Hit Hit { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string BadgeText { get; init; }
    public required System.Windows.Media.Brush BadgeBackground { get; init; }
    public required System.Windows.Media.Brush BadgeBorder { get; init; }
    public required System.Windows.Media.Brush BadgeForeground { get; init; }
    public required string RelevancePercent { get; init; }
    public required string RelevanceTooltip { get; init; }
    public required System.Windows.Media.Brush RelevanceBackground { get; init; }
    public required System.Windows.Media.Brush RelevanceForeground { get; init; }
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
        Activated += (_, _) => UpdateWidgetOpacity();
        Deactivated += (_, _) =>
        {
            UpdateWidgetOpacity();
            try { _settings.Save(); } catch { }
        };
        Loaded += (_, _) => { AdjustCardSize(); FadeIn(); };
        SizeChanged += (_, _) => AdjustCardSize();
        UpdateLocalization();
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
        Width = _settings.WindowWidth;
        Topmost = _isPinned;
        ApplyWidgetAppearance();
    }

    private void OnResizeRightDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double newWidth = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
        Width = newWidth;
        _settings.WindowWidth = newWidth;
    }

    private void OnResizeLeftDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double newWidth = Math.Clamp(Width - e.HorizontalChange, MinWidth, MaxWidth);
        double delta = newWidth - Width;
        Left -= delta;
        Width = newWidth;
        _settings.WindowWidth = newWidth;
    }

    /// <summary>Apply layout adjustments and appearance when search opens.</summary>
    public void ApplyLook()
    {
        try
        {
            AdjustCardSize();
            ApplyWidgetAppearance();
            UpdateLocalization();
        }
        catch { }
    }

    public void ApplyWidgetAppearance()
    {
        try
        {
            UpdateWidgetOpacity();
            WindowBlurHelper.SetBlur(this, _settings.WidgetBlur);

            // Theme application
            bool isDark = ThemeManager.IsDark(_settings.Theme);
            ApplyTheme(isDark);
        }
        catch { }
    }

    private void UpdateWidgetOpacity()
    {
        try
        {
            Root.BeginAnimation(OpacityProperty, null);
            double baseOpacity = Math.Clamp(_settings.WidgetOpacity, 0.5, 1.0);
            double targetOpacity = IsActive ? Math.Clamp(baseOpacity + 0.20, 0.5, 1.0) : baseOpacity;
            Root.Opacity = targetOpacity;
        }
        catch { }
    }

    public void UpdateLocalization()
    {
        try
        {
            if (BtnOpen is not null) BtnOpen.Content = Loc.Open;
            if (BtnInExplorer is not null) BtnInExplorer.Content = Loc.InExplorer;
            if (BtnOpenWith is not null) BtnOpenWith.Content = Loc.OpenWith;
            if (BtnCopyPath is not null) BtnCopyPath.Content = Loc.CopyPath;
            if (MascotSubtitle is not null) MascotSubtitle.Text = Loc.MascotSubtitle;
            if (Query is not null) Query.ToolTip = Loc.SearchTooltip;
            if (ClearButton is not null) ClearButton.ToolTip = Loc.ClearQueryTooltip;
            if (SemanticPillText is not null) SemanticPillText.Text = Loc.SearchingMeaning;
            if (SemanticPill is not null) SemanticPill.ToolTip = Loc.IsRu
                ? "Прямые совпадения найдены. Выполняется семантический поиск по смыслу…"
                : "Direct keyword matches found. Searching by meaning…";
            UpdateFilterCounts();
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

                // Mascot & Logo
                if (MascotBadge is not null)
                {
                    MascotBadge.Background = (Brush)bc.ConvertFrom("#1C384E")!;
                    MascotBadge.BorderBrush = (Brush)bc.ConvertFrom("#256B9E")!;
                }
                if (MascotText is not null) MascotText.Foreground = (Brush)bc.ConvertFrom("#80D8FF")!;
                if (MascotSubtitle is not null) MascotSubtitle.Foreground = (Brush)bc.ConvertFrom("#777777")!;

                // Clear button
                if (ClearButton is not null)
                {
                    ClearButton.Background = (Brush)bc.ConvertFrom("#444444")!;
                    ClearButton.Foreground = (Brush)bc.ConvertFrom("#AAAAAA")!;
                }

                // Window Header buttons
                var headerBtnBg = (Brush)bc.ConvertFrom("#383838")!;
                if (BtnPin is not null) { BtnPin.Background = headerBtnBg; BtnPin.Foreground = Brushes.White; }
                if (BtnMin is not null) { BtnMin.Background = headerBtnBg; BtnMin.Foreground = Brushes.White; }
                if (BtnClose is not null) { BtnClose.Background = headerBtnBg; BtnClose.Foreground = Brushes.White; }

                // Preview container & action buttons
                if (PreviewImageBorder is not null) PreviewImageBorder.Background = (Brush)bc.ConvertFrom("#202020")!;
                var actionBtnBg = (Brush)bc.ConvertFrom("#353535")!;
                var actionBtnFg = Brushes.White;
                var actionBtnBorder = (Brush)bc.ConvertFrom("#444444")!;
                SetActionButtonStyle(BtnQuickLook, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnOpen, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnInExplorer, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnOpenWith, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnCopyPath, actionBtnBg, actionBtnFg, actionBtnBorder);

                Resources["ResultTitleBrush"] = Brushes.White;
                Resources["ResultDetailBrush"] = (Brush)bc.ConvertFrom("#AAAAAA")!;
                Resources["ResultBodyBrush"] = (Brush)bc.ConvertFrom("#DDDDDD")!;
                Resources["CardBackgroundBrush"] = (Brush)bc.ConvertFrom("#2C2C2C")!;
                Resources["CardBorderBrush"] = (Brush)bc.ConvertFrom("#444444")!;
                Resources["KeycapBackgroundBrush"] = (Brush)bc.ConvertFrom("#333333")!;
                Resources["KeycapBorderBrush"] = (Brush)bc.ConvertFrom("#555555")!;
                Resources["KeycapForegroundBrush"] = (Brush)bc.ConvertFrom("#DDDDDD")!;
            }
            else
            {
                Root.Background = (Brush)bc.ConvertFrom("#F8F9FA")!;
                Root.BorderBrush = (Brush)bc.ConvertFrom("#D1D5DB")!;
                Query.Background = (Brush)bc.ConvertFrom("#FFFFFF")!;
                Query.Foreground = (Brush)bc.ConvertFrom("#0F172A")!;
                Query.CaretBrush = (Brush)bc.ConvertFrom("#0F172A")!;
                Status.Foreground = (Brush)bc.ConvertFrom("#475569")!;

                // Mascot & Logo (light blue badge with dark accent)
                if (MascotBadge is not null)
                {
                    MascotBadge.Background = (Brush)bc.ConvertFrom("#E0F2FE")!;
                    MascotBadge.BorderBrush = (Brush)bc.ConvertFrom("#7DD3FC")!;
                }
                if (MascotText is not null) MascotText.Foreground = (Brush)bc.ConvertFrom("#0284C7")!;
                if (MascotSubtitle is not null) MascotSubtitle.Foreground = (Brush)bc.ConvertFrom("#64748B")!;

                // Clear button
                if (ClearButton is not null)
                {
                    ClearButton.Background = (Brush)bc.ConvertFrom("#E2E8F0")!;
                    ClearButton.Foreground = (Brush)bc.ConvertFrom("#475569")!;
                }

                // Window Header buttons
                var headerBtnBg = (Brush)bc.ConvertFrom("#E2E8F0")!;
                var headerBtnFg = (Brush)bc.ConvertFrom("#1E293B")!;
                if (BtnPin is not null) { BtnPin.Background = headerBtnBg; BtnPin.Foreground = headerBtnFg; }
                if (BtnMin is not null) { BtnMin.Background = headerBtnBg; BtnMin.Foreground = headerBtnFg; }
                if (BtnClose is not null) { BtnClose.Background = headerBtnBg; BtnClose.Foreground = headerBtnFg; }

                // Preview container & action buttons
                if (PreviewImageBorder is not null) PreviewImageBorder.Background = (Brush)bc.ConvertFrom("#F1F5F9")!;
                var actionBtnBg = (Brush)bc.ConvertFrom("#F1F5F9")!;
                var actionBtnFg = (Brush)bc.ConvertFrom("#0F172A")!;
                var actionBtnBorder = (Brush)bc.ConvertFrom("#CBD5E1")!;
                SetActionButtonStyle(BtnQuickLook, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnOpen, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnInExplorer, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnOpenWith, actionBtnBg, actionBtnFg, actionBtnBorder);
                SetActionButtonStyle(BtnCopyPath, actionBtnBg, actionBtnFg, actionBtnBorder);

                Resources["ResultTitleBrush"] = (Brush)bc.ConvertFrom("#0F172A")!;
                Resources["ResultDetailBrush"] = (Brush)bc.ConvertFrom("#475569")!;
                Resources["ResultBodyBrush"] = (Brush)bc.ConvertFrom("#1E293B")!;
                Resources["CardBackgroundBrush"] = (Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["CardBorderBrush"] = (Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["KeycapBackgroundBrush"] = (Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["KeycapBorderBrush"] = (Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["KeycapForegroundBrush"] = (Brush)bc.ConvertFrom("#1E293B")!;
            }
        }
        catch { }
    }

    private static void SetActionButtonStyle(Button? btn, Brush bg, Brush fg, Brush border)
    {
        if (btn is null) return;
        btn.Background = bg;
        btn.Foreground = fg;
        btn.BorderBrush = border;
    }

    private void FadeIn()
    {
        ApplyWidgetAppearance();
        double target = Math.Clamp(_settings.WidgetOpacity, 0.1, 1.0);
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

        FilterAll.Content = allCount > 0 ? $"{Loc.FilterAll} ({allCount})" : Loc.FilterAll;
        FilterPdf.Content = pdfCount > 0 ? $"{Loc.FilterPdf} ({pdfCount})" : Loc.FilterPdf;
        FilterImg.Content = imgCount > 0 ? $"{Loc.FilterImg} ({imgCount})" : Loc.FilterImg;
        FilterDoc.Content = docCount > 0 ? $"{Loc.FilterDoc} ({docCount})" : Loc.FilterDoc;
        FilterCode.Content = codeCount > 0 ? $"{Loc.FilterCode} ({codeCount})" : Loc.FilterCode;
        FilterMedia.Content = mediaCount > 0 ? $"{Loc.FilterMedia} ({mediaCount})" : Loc.FilterMedia;
    }

    // The query box never waits: each keystroke starts a background search,
    // and only the newest one paints (older ones throw their work away).
    private void SearchNow()
    {
        int gen = Interlocked.Increment(ref _searchGen);
        string q = Query.Text.Trim();
        ClearButton.Visibility = q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (q.Length == 0)
        {
            _allHits.Clear();
            _currentItems.Clear();
            Results.ItemsSource = null;
            PreviewImage.Source = null;
            PreviewText.Text = string.Empty;
            SemanticPill.Visibility = Visibility.Collapsed;
            UpdateFilterCounts();
            ShowPreview();
            FollowPreview();
            return;
        }
        Task.Run(() =>
        {
            // Phase 1: Instant lexical search (< 5 ms)
            List<Engine.Hit> lexicalHits;
            try
            {
                lexicalHits = _engine.SearchLexical(q, limit: 50);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (gen == _searchGen)
                    {
                        ErrorLog.Write("search lexical: " + ex);
                        Status.Text = "search stopped: " + ex.Message.Split('\n')[0];
                        SemanticPill.Visibility = Visibility.Collapsed;
                    }
                });
                return;
            }

            // Immediately show Phase 1 results and activate "Ищем по смыслу…" indicator
            Dispatcher.Invoke(() =>
            {
                if (gen != _searchGen) return;

                if (lexicalHits.Count > 0)
                {
                    _allHits = lexicalHits.Select(CreateItem).ToList();
                    UpdateFilterCounts();
                    ApplyCurrentFilter();
                    Status.Text = Loc.LexicalHitsSuffix(lexicalHits.Count);
                }
                else
                {
                    Status.Text = Loc.SearchingMeaning;
                }

                // Show semantic searching indicator pill
                SemanticPillText.Text = Loc.SearchingMeaning;
                SemanticPill.Visibility = Visibility.Visible;
            });

            // Phase 2: Vector embedding and hybrid re-ranking
            List<Engine.Hit> semanticHits;
            try
            {
                var vec = _engine.EmbedQuery(q);
                if (gen != _searchGen) return;

                semanticHits = _engine.SearchWithVector(q, vec, limit: 50);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    if (gen == _searchGen)
                    {
                        ErrorLog.Write("search semantic: " + ex);
                        SemanticPill.Visibility = Visibility.Collapsed;
                    }
                });
                return;
            }

            // Render final semantic / hybrid results
            Dispatcher.Invoke(() =>
            {
                if (gen != _searchGen) return; // a newer keystroke won

                SemanticPill.Visibility = Visibility.Collapsed;
                _allHits = semanticHits.Select(CreateItem).ToList();
                UpdateFilterCounts();
                ApplyCurrentFilter();

                if (CodeQuery.TextOf(q) is null && _allHits.Count > 0
                    && _allHits.All(h => h.Hit.Kind == "Code"))
                {
                    Status.Text = Loc.IsRu
                        ? "только код совпадает — подсказка: code: ищет по коду, обычные слова — по остальным файлам"
                        : "only code matches — hint: code: searches code, plain words search other files";
                }
                else if (_allHits.Count == 0)
                {
                    Status.Text = Loc.NoResultsFound;
                }
                else
                {
                    Status.Text = Loc.FoundResults(_allHits.Count);
                }
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

        int scorePct = Math.Clamp((int)Math.Round(h.Score * 100), 1, 99);
        string relPercent = $"{scorePct}%";
        string relTooltip = $"Точность совпадения: {scorePct}% (score: {h.Score:F3})";
        string relBgHex = scorePct >= 75 ? "#1B3D2F" : (scorePct >= 50 ? "#1C3B4E" : "#2D3748");
        string relFgHex = scorePct >= 75 ? "#4ADE80" : (scorePct >= 50 ? "#67E8F9" : "#94A3B8");

        var relBgBrush = (SolidColorBrush)(bc.ConvertFrom(relBgHex) ?? Brushes.DarkSlateGray);
        var relFgBrush = (SolidColorBrush)(bc.ConvertFrom(relFgHex) ?? Brushes.LightGray);
        relBgBrush.Freeze();
        relFgBrush.Freeze();

        return new ResultItemViewModel
        {
            Hit = h,
            Title = Short(h.Path, 100) + (h.Where.Length > 0 ? "  " + h.Where : ""),
            Detail = h.Kind + (h.Excerpt is null ? "" : " — " + EncodingHelper.FixMojibake(h.Excerpt)),
            BadgeText = badgeText,
            BadgeBackground = bgBrush,
            BadgeBorder = borderBrush,
            BadgeForeground = fgBrush,
            RelevancePercent = relPercent,
            RelevanceTooltip = relTooltip,
            RelevanceBackground = relBgBrush,
            RelevanceForeground = relFgBrush
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
        string p2b = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "pdfium.dll");
        if (File.Exists(p2b)) return p2b;

        // Traverse upwards looking for native\x64\pdfium.dll in dev / build trees
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "native", "x64", "pdfium.dll");
            if (File.Exists(candidate)) return candidate;
            string? parent = Path.GetDirectoryName(dir);
            if (parent == dir || string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }

        return p1;
    }

    private readonly List<PreviewWindow> _openPreviews = [];

    /// <summary>Space: show the selected file itself, or hide it again. Multi-select (Ctrl+Click) opens multiple preview windows. Shift+Space opens a new standalone preview window.</summary>
    private void TogglePreview()
    {
        try
        {
            if (Results.SelectedItems.Count > 1)
            {
                // Multi-select with Ctrl or Shift: open previews for all selected documents cascading
                int offset = 0;
                foreach (var obj in Results.SelectedItems)
                {
                    if (obj is ResultItemViewModel item)
                    {
                        var cascadeWin = new PreviewWindow(FindPdfium(), OpenSelected, (delta) => NavigateSelection(delta));
                        _openPreviews.Add(cascadeWin);
                        cascadeWin.Closed += (_, _) => _openPreviews.Remove(cascadeWin);
                        cascadeWin.ShowHit(item.Hit, Query.Text);
                        cascadeWin.WindowStartupLocation = WindowStartupLocation.Manual;
                        cascadeWin.Left = Math.Max(40, (SystemParameters.WorkArea.Width - 980) / 2 + offset);
                        cascadeWin.Top = Math.Max(40, (SystemParameters.WorkArea.Height - 720) / 2 + offset);
                        cascadeWin.Show();
                        offset += 32;
                    }
                }
                return;
            }

            if (Results.SelectedIndex < 0 || Results.SelectedIndex >= _currentItems.Count) return;
            var currentHit = _currentItems[Results.SelectedIndex].Hit;
            bool forceNew = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

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
            if (forceNew && _openPreviews.Count > 1)
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = Math.Max(40, (SystemParameters.WorkArea.Width - 980) / 2 + (_openPreviews.Count - 1) * 32);
                win.Top = Math.Max(40, (SystemParameters.WorkArea.Height - 720) / 2 + (_openPreviews.Count - 1) * 32);
            }
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
