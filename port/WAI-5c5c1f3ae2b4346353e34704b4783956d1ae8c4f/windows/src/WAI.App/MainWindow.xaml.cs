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

public sealed class ResultItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));

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

    private System.Windows.Media.ImageSource? _thumbnail;
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }
    public bool HasThumbnail => _thumbnail != null;

    public string GalleryIcon => Hit.Kind switch
    {
        "Image" or "Screenshot" => "🖼️",
        "Video" => "🎬",
        "Audio" => "🎵",
        "Pdf" => "📄",
        "Code" => "💻",
        _ => "📝"
    };
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
    private readonly Action _engineChanged;
    private CancellationTokenSource? _searchCancellation;
    private SearchRequest? _pendingSearch;
    private bool _searchPumpRunning;
    private bool _closed;
    private const int SearchLimit = 50;
    private sealed record SearchRequest(int Generation, string Query, HashSet<FileKind>? Kinds);
    private bool _navigated;
    private bool _isGalleryMode;
    private bool _syncingSelection;

    public bool IsGalleryMode => _isGalleryMode;

    public MainWindow(Engine engine, Settings settings)
    {
        _engine = engine; _settings = settings;
        InitializeComponent();
        try { Icon = AppIcon.Wpf; } catch { }
        _engineChanged = () => Dispatcher.BeginInvoke(() =>
        {
            if (!_closed && string.IsNullOrWhiteSpace(Query.Text)) Status.Text = _engine.Status;
        });
        _engine.Changed += _engineChanged;
        Status.Text = _engine.Status;
        _debounce.Elapsed += (_, _) => Dispatcher.BeginInvoke(SearchNow);
        Query.TextChanged += (_, _) =>
        {
            InvalidateSearch();
            _navigated = false;
            ClearButton.Visibility = string.IsNullOrEmpty(Query.Text) ? Visibility.Collapsed : Visibility.Visible;
            SearchPrompt.Visibility = string.IsNullOrEmpty(Query.Text) ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop();
            if (string.IsNullOrWhiteSpace(Query.Text)) SearchNow();
            else _debounce.Start();
        };
        Query.PreviewKeyDown += OnKey;
        Query.PreviewMouseLeftButtonDown += (_, _) => _navigated = false;
        Results.MouseDoubleClick += (_, _) => OpenSelected();
        Results.PreviewMouseLeftButtonDown += (_, _) => _navigated = true;
        Results.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { TogglePreview(); e.Handled = true; }
        };
        GalleryResults.PreviewMouseLeftButtonDown += (_, _) => _navigated = true;
        GalleryResults.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { TogglePreview(); e.Handled = true; }
        };
        Activated += (_, _) => UpdateWidgetOpacity();
        Deactivated += (_, _) =>
        {
            UpdateWidgetOpacity();
            try { _settings.Save(); } catch { }
        };
        Loaded += (_, _) =>
        {
            _isGalleryMode = _settings.PreferGallery;
            UpdateViewMode();
            AdjustCardSize();
            FadeIn();
        };
        SizeChanged += (_, _) => AdjustCardSize();
        UpdateLocalization();
    }

    private void AdjustCardSize()
    {
        try
        {
            double screenH = SystemParameters.WorkArea.Height;
            double h = Math.Clamp(screenH * 0.44, 340, 620);
            Results.MaxHeight = h;
            if (GalleryResults != null) GalleryResults.MaxHeight = h;
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
            if (Query is not null)
            {
                Query.ToolTip = Loc.SearchTooltip;
                System.Windows.Automation.AutomationProperties.SetName(Query, Loc.SearchTooltip);
            }
            if (SearchPrompt is not null) SearchPrompt.Text = Loc.SearchPlaceholder;
            if (BtnQuickLook is not null) BtnQuickLook.Content = Loc.PreviewLabel;
            if (BtnMin is not null) BtnMin.ToolTip = Loc.Minimize;
            if (BtnClose is not null) BtnClose.ToolTip = Loc.HideSearch;
            if (BtnPin is not null) BtnPin.ToolTip = _isPinned ? Loc.Unpin : Loc.Pin;
            if (KeySpace is not null) KeySpace.Text = Loc.IsRu ? "Пробел" : "Space";
            if (HintPreview is not null) HintPreview.Text = Loc.PreviewLabel + " · ";
            if (HintOpen is not null) HintOpen.Text = Loc.Open + " · ";
            if (HintClose is not null) HintClose.Text = Loc.HideSearch;
            if (ClearButton is not null) ClearButton.ToolTip = Loc.ClearQueryTooltip;
            if (SemanticPillText is not null) SemanticPillText.Text = Loc.SearchingMeaning;
            if (SemanticPill is not null) SemanticPill.ToolTip = Loc.IsRu
                ? "Прямые совпадения найдены. Выполняется семантический поиск по смыслу…"
                : "Direct keyword matches found. Searching by meaning…";
            if (BtnViewList is not null) BtnViewList.ToolTip = Loc.ViewListTooltip;
            if (BtnViewGallery is not null) BtnViewGallery.ToolTip = Loc.ViewGalleryTooltip;
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

                // Window Header buttons & Clear button use dynamic resource brushes
                if (BtnPin is not null) { BtnPin.ClearValue(BackgroundProperty); BtnPin.ClearValue(ForegroundProperty); }
                if (BtnMin is not null) { BtnMin.ClearValue(BackgroundProperty); BtnMin.ClearValue(ForegroundProperty); }
                if (BtnClose is not null) { BtnClose.ClearValue(BackgroundProperty); BtnClose.ClearValue(ForegroundProperty); }
                if (ClearButton is not null) { ClearButton.ClearValue(BackgroundProperty); ClearButton.ClearValue(ForegroundProperty); }

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

                if (SemanticPill is not null)
                {
                    SemanticPill.Background = (Brush)bc.ConvertFrom("#15303E")!;
                    SemanticPill.BorderBrush = (Brush)bc.ConvertFrom("#00BCD4")!;
                }
                if (SemanticPillText is not null) SemanticPillText.Foreground = (Brush)bc.ConvertFrom("#80DEEA")!;
                if (SearchPrompt is not null) SearchPrompt.Foreground = (Brush)bc.ConvertFrom("#888888")!;

                Resources["ResultTitleBrush"] = Brushes.White;
                Resources["ResultDetailBrush"] = (Brush)bc.ConvertFrom("#AAAAAA")!;
                Resources["ResultBodyBrush"] = (Brush)bc.ConvertFrom("#DDDDDD")!;
                Resources["CardBackgroundBrush"] = (Brush)bc.ConvertFrom("#2C2C2C")!;
                Resources["CardBorderBrush"] = (Brush)bc.ConvertFrom("#444444")!;
                Resources["KeycapBackgroundBrush"] = (Brush)bc.ConvertFrom("#333333")!;
                Resources["KeycapBorderBrush"] = (Brush)bc.ConvertFrom("#555555")!;
                Resources["KeycapForegroundBrush"] = (Brush)bc.ConvertFrom("#DDDDDD")!;

                Resources["FilterChipBackgroundBrush"] = (Brush)bc.ConvertFrom("#2A2A2A")!;
                Resources["FilterChipBorderBrush"] = (Brush)bc.ConvertFrom("#3E3E3E")!;
                Resources["FilterChipForegroundBrush"] = (Brush)bc.ConvertFrom("#BBBBBB")!;
                Resources["FilterChipHoverBackgroundBrush"] = (Brush)bc.ConvertFrom("#383838")!;

                Resources["HeaderButtonBackgroundBrush"] = (Brush)bc.ConvertFrom("#383838")!;
                Resources["HeaderButtonForegroundBrush"] = Brushes.White;
                Resources["HeaderButtonHoverBrush"] = (Brush)bc.ConvertFrom("#484848")!;

                Resources["ClearButtonBackgroundBrush"] = (Brush)bc.ConvertFrom("#444444")!;
                Resources["ClearButtonForegroundBrush"] = (Brush)bc.ConvertFrom("#AAAAAA")!;
                Resources["ClearButtonHoverBrush"] = (Brush)bc.ConvertFrom("#5A5A5A")!;

                Resources["ScrollThumbBrush"] = (Brush)bc.ConvertFrom("#444444")!;
                Resources["ScrollThumbHoverBrush"] = (Brush)bc.ConvertFrom("#666666")!;
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

                // Window Header buttons & Clear button use dynamic resource brushes
                if (BtnPin is not null) { BtnPin.ClearValue(BackgroundProperty); BtnPin.ClearValue(ForegroundProperty); }
                if (BtnMin is not null) { BtnMin.ClearValue(BackgroundProperty); BtnMin.ClearValue(ForegroundProperty); }
                if (BtnClose is not null) { BtnClose.ClearValue(BackgroundProperty); BtnClose.ClearValue(ForegroundProperty); }
                if (ClearButton is not null) { ClearButton.ClearValue(BackgroundProperty); ClearButton.ClearValue(ForegroundProperty); }

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

                if (SemanticPill is not null)
                {
                    SemanticPill.Background = (Brush)bc.ConvertFrom("#E0F2FE")!;
                    SemanticPill.BorderBrush = (Brush)bc.ConvertFrom("#38BDF8")!;
                }
                if (SemanticPillText is not null) SemanticPillText.Foreground = (Brush)bc.ConvertFrom("#0284C7")!;
                if (SearchPrompt is not null) SearchPrompt.Foreground = (Brush)bc.ConvertFrom("#94A3B8")!;

                Resources["ResultTitleBrush"] = (Brush)bc.ConvertFrom("#0F172A")!;
                Resources["ResultDetailBrush"] = (Brush)bc.ConvertFrom("#475569")!;
                Resources["ResultBodyBrush"] = (Brush)bc.ConvertFrom("#1E293B")!;
                Resources["CardBackgroundBrush"] = (Brush)bc.ConvertFrom("#FFFFFF")!;
                Resources["CardBorderBrush"] = (Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["KeycapBackgroundBrush"] = (Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["KeycapBorderBrush"] = (Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["KeycapForegroundBrush"] = (Brush)bc.ConvertFrom("#1E293B")!;

                Resources["FilterChipBackgroundBrush"] = (Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["FilterChipBorderBrush"] = (Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["FilterChipForegroundBrush"] = (Brush)bc.ConvertFrom("#334155")!;
                Resources["FilterChipHoverBackgroundBrush"] = (Brush)bc.ConvertFrom("#F1F5F9")!;

                Resources["HeaderButtonBackgroundBrush"] = (Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["HeaderButtonForegroundBrush"] = (Brush)bc.ConvertFrom("#1E293B")!;
                Resources["HeaderButtonHoverBrush"] = (Brush)bc.ConvertFrom("#CBD5E1")!;

                Resources["ClearButtonBackgroundBrush"] = (Brush)bc.ConvertFrom("#E2E8F0")!;
                Resources["ClearButtonForegroundBrush"] = (Brush)bc.ConvertFrom("#475569")!;
                Resources["ClearButtonHoverBrush"] = (Brush)bc.ConvertFrom("#CBD5E1")!;

                Resources["ScrollThumbBrush"] = (Brush)bc.ConvertFrom("#CBD5E1")!;
                Resources["ScrollThumbHoverBrush"] = (Brush)bc.ConvertFrom("#94A3B8")!;
            }

            if (_allHits.Count > 0)
            {
                _allHits = _allHits.Select(x => CreateItem(x.Hit)).ToList();
                _currentItems = _allHits.ToList();
                Results.ItemsSource = _currentItems;
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
        BtnPin.ToolTip = _isPinned ? Loc.Unpin : Loc.Pin;
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

        _debounce.Stop();
        SearchNow();
        Query.Focus();
    }

    private void OnViewToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        bool gallery = btn == BtnViewGallery;
        SetViewMode(gallery);
        if (_isGalleryMode && GalleryResults.SelectedItem != null)
            GalleryResults.ScrollIntoView(GalleryResults.SelectedItem);
        else if (!_isGalleryMode && Results.SelectedItem != null)
            Results.ScrollIntoView(Results.SelectedItem);
        Query.Focus();
    }

    public void SetViewMode(bool gallery)
    {
        if (_isGalleryMode == gallery) return;
        _isGalleryMode = gallery;
        _settings.PreferGallery = gallery;
        try { _settings.Save(); } catch { }
        UpdateViewMode();
    }

    private void UpdateViewMode()
    {
        if (BtnViewList != null) BtnViewList.Tag = _isGalleryMode ? "" : "Active";
        if (BtnViewGallery != null) BtnViewGallery.Tag = _isGalleryMode ? "Active" : "";
        if (Results != null) Results.Visibility = _isGalleryMode ? Visibility.Collapsed : Visibility.Visible;
        if (GalleryResults != null)
        {
            GalleryResults.Visibility = _isGalleryMode ? Visibility.Visible : Visibility.Collapsed;
            if (_isGalleryMode)
            {
                if (GalleryResults.SelectedIndex != Results.SelectedIndex)
                    GalleryResults.SelectedIndex = Results.SelectedIndex;
                LoadThumbnailsForItems(_currentItems, _searchGen);
            }
        }
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            if (GalleryResults != null && GalleryResults.SelectedIndex != Results.SelectedIndex)
                GalleryResults.SelectedIndex = Results.SelectedIndex;
            ShowPreview();
            FollowPreview();
        }
        finally { _syncingSelection = false; }
    }

    private void OnGallerySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            if (Results != null && Results.SelectedIndex != GalleryResults.SelectedIndex)
                Results.SelectedIndex = GalleryResults.SelectedIndex;
            ShowPreview();
            FollowPreview();
        }
        finally { _syncingSelection = false; }
    }

    private void OnGalleryMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelected();
    }

    private void ApplyCurrentFilter()
    {
        // The engine already filtered candidates before ranking and limiting.
        _currentItems = _allHits.ToList();
        Results.ItemsSource = _currentItems;
        if (GalleryResults != null) GalleryResults.ItemsSource = _currentItems;
        Results.SelectedIndex = _currentItems.Count > 0 ? 0 : -1;
        if (GalleryResults != null) GalleryResults.SelectedIndex = _currentItems.Count > 0 ? 0 : -1;
        ShowPreview();
        FollowPreview();
        if (_isGalleryMode)
        {
            LoadThumbnailsForItems(_currentItems, _searchGen);
        }
    }

    private void UpdateFilterCounts()
    {
        // A limited result page cannot provide totals for the whole index.
        FilterAll.Content = Loc.FilterAll;
        FilterPdf.Content = Loc.FilterPdf;
        FilterImg.Content = Loc.FilterImg;
        FilterDoc.Content = Loc.FilterDoc;
        FilterCode.Content = Loc.FilterCode;
        FilterMedia.Content = Loc.FilterMedia;
    }

    private static HashSet<FileKind>? FilterKinds(string filter) => filter switch
    {
        "pdf" => [FileKind.Pdf],
        "img" => [FileKind.Image, FileKind.Screenshot],
        "doc" => [FileKind.Doc],
        "code" => [FileKind.Code],
        "media" => [FileKind.Audio, FileKind.Video],
        _ => null
    };

    private void InvalidateSearch()
    {
        Interlocked.Increment(ref _searchGen);
        _pendingSearch = null;
        _searchCancellation?.Cancel();
    }

    private void ClearSearchResults()
    {
        _allHits.Clear();
        _currentItems.Clear();
        Results.ItemsSource = null;
        if (GalleryResults != null) GalleryResults.ItemsSource = null;
        Results.SelectedIndex = -1;
        if (GalleryResults != null) GalleryResults.SelectedIndex = -1;
        SemanticPill.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewText.Text = string.Empty;
        PreviewTitle.Text = string.Empty;
        UpdateFilterCounts();
        ShowPreview();
        FollowPreview();
    }

    // One running search and one replaceable pending request. Every edit
    // invalidates completions immediately, including during debounce.
    private void SearchNow()
    {
        if (_closed) return;
        InvalidateSearch();
        string query = Query.Text.Trim();
        ClearButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchResults();
        if (query.Length == 0)
        {
            Status.Text = _engine.Status;
            return;
        }
        _pendingSearch = new SearchRequest(_searchGen, query, FilterKinds(_activeFilter));
        Status.Text = Loc.Searching;
        if (!_searchPumpRunning) _ = RunSearchQueueAsync();
    }

    private async Task RunSearchQueueAsync()
    {
        _searchPumpRunning = true;
        try
        {
            while (!_closed && _pendingSearch is { } request)
            {
                _pendingSearch = null;
                using var cancellation = new CancellationTokenSource();
                _searchCancellation = cancellation;
                try
                {
                    var lexical = await Task.Run(() => _engine.SearchText(request.Query, SearchLimit,
                        request.Kinds, cancellation.Token), cancellation.Token);
                    if (_closed || request.Generation != _searchGen || cancellation.IsCancellationRequested) continue;
                    if (lexical.Count > 0)
                    {
                        _allHits = lexical.Select(CreateItem).ToList();
                        ApplyCurrentFilter();
                        Status.Text = Loc.SearchResults(_allHits.Count, SearchLimit) + " · " + Loc.Searching;
                    }
                    SemanticPill.Visibility = Visibility.Visible;
                    var hits = await Task.Run(() => _engine.Search(request.Query, SearchLimit,
                        request.Kinds, cancellation.Token), cancellation.Token);
                    if (_closed || request.Generation != _searchGen || cancellation.IsCancellationRequested) continue;
                    SemanticPill.Visibility = Visibility.Collapsed;
                    _allHits = hits.Select(CreateItem).ToList();
                    UpdateFilterCounts();
                    ApplyCurrentFilter();
                    Status.Text = Loc.SearchResults(_allHits.Count, SearchLimit)
                        + (_engine.IsSemanticAvailable ? string.Empty : " · " + Loc.SemanticUnavailable);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    ErrorLog.Write("search: " + ex);
                    if (!_closed && request.Generation == _searchGen)
                        Status.Text = _allHits.Count > 0
                            ? Loc.SearchResults(_allHits.Count, SearchLimit) + " · " + Loc.SemanticUnavailable
                            : Loc.SearchFailed;
                }
                finally
                {
                    if (ReferenceEquals(_searchCancellation, cancellation)) { _searchCancellation = null; SemanticPill.Visibility = Visibility.Collapsed; }
                }
            }
        }
        finally { _searchPumpRunning = false; }
    }

    private ResultItemViewModel CreateItem(Engine.Hit h)
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
            badgeText = Loc.IsRu ? "СКРИН" : "SCREEN";
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
            badgeText = Loc.IsRu ? "ВИДЕО" : "VIDEO";
            bg = "#3D2714";
            border = "#FB8C00";
            fg = "#FFCC80";
        }
        else if (h.Kind == "Audio" || ext is ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac")
        {
            badgeText = Loc.IsRu ? "АУДИО" : "AUDIO";
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
        string relTooltip = Loc.IsRu
            ? $"Точность совпадения: {scorePct}% (score: {h.Score:F3})"
            : $"Match relevance: {scorePct}% (score: {h.Score:F3})";

        bool isDark = ThemeManager.IsDark(_settings.Theme);
        string relBgHex;
        string relFgHex;
        if (isDark)
        {
            relBgHex = scorePct >= 75 ? "#1B3D2F" : (scorePct >= 50 ? "#1C3B4E" : "#2D3748");
            relFgHex = scorePct >= 75 ? "#4ADE80" : (scorePct >= 50 ? "#67E8F9" : "#94A3B8");
        }
        else
        {
            relBgHex = scorePct >= 75 ? "#DCFCE7" : (scorePct >= 50 ? "#E0F2FE" : "#F1F5F9");
            relFgHex = scorePct >= 75 ? "#15803D" : (scorePct >= 50 ? "#0369A1" : "#475569");
        }

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
        PreviewText.Text = (h.Excerpt is not null ? EncodingHelper.FixMojibake(h.Excerpt) : null) ?? Loc.NoExcerpt;
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
        "Video" or "Audio" => $"{(h.Kind == "Video" ? Loc.Video : Loc.Audio)} · {h.Where}",
        "Pdf" => $"PDF · {Loc.Page} {h.Where.TrimStart('@')}",
        "Code" => $"{Loc.CodeLabel} · {h.Where}",
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
        else if (e.Key == Key.Right && _isGalleryMode && _currentItems.Count > 0)
        {
            NavigateSelection(1);
            _navigated = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Left && _isGalleryMode && _currentItems.Count > 0)
        {
            NavigateSelection(-1);
            _navigated = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _currentItems.Count > 0)
        {
            int step = _isGalleryMode ? GalleryColumns() : 1;
            NavigateSelection(step);
            _navigated = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _currentItems.Count > 0)
        {
            int step = _isGalleryMode ? GalleryColumns() : 1;
            NavigateSelection(-step);
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

    private int GalleryColumns()
    {
        double width = (GalleryResults != null && GalleryResults.ActualWidth > 0)
            ? GalleryResults.ActualWidth
            : (ActualWidth > 0 ? ActualWidth - 40 : 680);
        return Math.Max(1, (int)(width / 166));
    }

    public void NavigateSelection(int delta)
    {
        var list = _isGalleryMode && GalleryResults != null ? GalleryResults : Results;
        if (list.Items.Count == 0) return;
        int next = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        if (next != list.SelectedIndex)
        {
            list.SelectedIndex = next;
            list.ScrollIntoView(list.SelectedItem);
        }
    }

    private void LoadThumbnailsForItems(IEnumerable<ResultItemViewModel> items, int gen)
    {
        var list = items.ToList();
        Task.Run(() =>
        {
            foreach (var item in list)
            {
                if (_searchGen != gen || _closed) break;
                if (item.Thumbnail != null) continue;
                string? thumbPath = ThumbFor(item.Hit);
                if (thumbPath != null && File.Exists(thumbPath))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 320;
                        bmp.UriSource = new Uri(thumbPath);
                        bmp.EndInit();
                        bmp.Freeze();

                        if (_searchGen == gen && !_closed)
                        {
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                            {
                                if (_searchGen == gen) item.Thumbnail = bmp;
                            });
                        }
                    }
                    catch { }
                }
            }
        });
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
            ErrorLog.Write("preview: " + ex);
            Status.Text = Loc.PreviewFailed;
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
        ErrorLog.Write(what + ": " + ex);
        string msg = Loc.FileActionFailed;
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
                var (line, col) = ParseLineAndColumn(h.Where);
                var t = Editors.ForCode(_settings.Editor, h.Path, line, col);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(t.Exe, t.Args) { UseShellExecute = true });
            }
            else if (h.Kind == "Pdf") Editors.OpenPdf(h.Path, ParsePage(h.Where));
            else if (h.Kind is "Video" or "Audio")
            {
                double seconds = ParseSeconds(h.Where);
                Editors.OpenMedia(h.Path, seconds > 0 ? seconds : null);
            }
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

    internal static (int Line, int Column) ParseLineAndColumn(string where)
    {
        if (string.IsNullOrWhiteSpace(where)) return (1, 1);
        int line = ParseLine(where);
        int col = 1;
        int colonIdx = where.IndexOf(':');
        if (colonIdx >= 0 && colonIdx < where.Length - 1)
        {
            var colDigits = new string(where.Substring(colonIdx + 1).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(colDigits, out int c)) col = Math.Max(1, c);
        }
        return (line, col);
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
        _closed = true;
        InvalidateSearch();
        _engine.Changed -= _engineChanged;
        _debounce.Dispose();
        base.OnClosed(e);
    }
}
