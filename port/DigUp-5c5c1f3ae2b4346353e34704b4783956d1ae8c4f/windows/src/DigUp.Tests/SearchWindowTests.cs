using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DigUp.App;
using DigUp.Core;
using Xunit;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace DigUp.Tests;

[Collection("WPF search window")]
public sealed class SearchWindowTests
{
    private const string SearchTerm = "crystalmarker";

    [Fact]
    public void Clear_button_does_not_allow_pending_search_to_restore_results_or_preview()
    {
        using var fixture = new SearchFixture();
        using var ui = new WindowDispatcher(fixture.Engine);
        object searchGate = fixture.SearchGate;

        Monitor.Enter(searchGate);
        try
        {
            ui.Invoke(window =>
            {
                RaiseClearClick(window);
                Get<WpfTextBox>(window, "Query").Text = SearchTerm;
            });

            // Allow the 180 ms debounce to dispatch the search, which then
            // blocks on the gate held by this test.
            Thread.Sleep(450);

            ui.Invoke(window =>
            {
                Assert.Equal(SearchTerm, Get<WpfTextBox>(window, "Query").Text);
                Assert.Empty(Get<WpfListBox>(window, "Results").Items);
                RaiseClearClick(window);
                Assert.Empty(Get<WpfListBox>(window, "Results").Items);
                Assert.Equal(Visibility.Collapsed, Get<FrameworkElement>(window, "PreviewBox").Visibility);
            });
        }
        finally
        {
            Monitor.Exit(searchGate);
        }

        // Give the old lexical search time to finish and dispatch its result.
        Thread.Sleep(350);
        AssertEmptyWindow(ui);
    }

    [Fact]
    public void New_text_invalidates_pending_result_while_debounce_is_waiting()
    {
        using var fixture = new SearchFixture();
        using var ui = new WindowDispatcher(fixture.Engine);
        object searchGate = fixture.SearchGate;

        Monitor.Enter(searchGate);
        try
        {
            ui.Invoke(window =>
            {
                RaiseClearClick(window);
                Get<WpfTextBox>(window, "Query").Text = SearchTerm;
            });

            Thread.Sleep(450);

            ui.Invoke(window =>
            {
                // Extend the debounce so the old completion can be observed
                // before a search for the new text has a chance to replace it.
                GetDebounceTimer(window).Interval = 10_000;
                Get<WpfTextBox>(window, "Query").Text = "no matching phrase";
            });
        }
        finally
        {
            Monitor.Exit(searchGate);
        }

        Thread.Sleep(350);
        var state = ui.Invoke(window => (
            Query: Get<WpfTextBox>(window, "Query").Text,
            ResultCount: Get<WpfListBox>(window, "Results").Items.Count,
            PreviewVisibility: Get<FrameworkElement>(window, "PreviewBox").Visibility));

        Assert.Equal("no matching phrase", state.Query);
        Assert.Equal(0, state.ResultCount);
        Assert.Equal(Visibility.Collapsed, state.PreviewVisibility);
    }

    private static void AssertEmptyWindow(WindowDispatcher ui)
    {
        var state = ui.Invoke(window => (
            Query: Get<WpfTextBox>(window, "Query").Text,
            ResultCount: Get<WpfListBox>(window, "Results").Items.Count,
            PreviewVisibility: Get<FrameworkElement>(window, "PreviewBox").Visibility));

        Assert.Equal(string.Empty, state.Query);
        Assert.Equal(0, state.ResultCount);
        Assert.Equal(Visibility.Collapsed, state.PreviewVisibility);
    }

    private static void RaiseClearClick(MainWindow window)
    {
        var button = Get<WpfButton>(window, "ClearButton");
        button.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent, button));
    }

    private static T Get<T>(MainWindow window, string name) where T : class =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static System.Timers.Timer GetDebounceTimer(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_debounce", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<System.Timers.Timer>(field!.GetValue(window));
    }

    private sealed class SearchFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "digup_search_window_" + Guid.NewGuid().ToString("N"));
        private readonly IndexStore _store;

        public Engine Engine { get; }
        public object SearchGate { get; }

        public SearchFixture()
        {
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "reference.txt");
            File.WriteAllText(path, "A small indexed note for the WPF search regression.");

            _store = new IndexStore(Path.Combine(_folder, "index"));
            long fileId = _store.Insert(new CrawlCandidate(path, new FileInfo(path).Length, 1, FileKind.Doc));
            _store.AddSegment(fileId, SegmentKind.Chunk, Modality.Text,
                excerpt: "The note contains the unique word crystalmarker.",
                text: "The note contains the unique word crystalmarker.");
            _store.Finish(fileId, "indexed");

            Engine = new Engine(new Settings());
            SetField(Engine, "_store", _store);
            SetField(Engine, "_searcher", new Searcher(_store, loadVectors: false));
            SetField(Engine, "_queryWorker", new NoOpEmbedder());
            SearchGate = GetField(Engine, "_searchGate");

            Assert.Contains(Engine.Search(SearchTerm), hit =>
                Path.GetFullPath(hit.Path).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            Engine.Dispose();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class NoOpEmbedder : IEmbedder
    {
        public WorkerInfo Info { get; } = new("test", "fixture", "words-only", "none", Vectors.Dimension, true, null);

        public float[][] EmbedTexts(IReadOnlyList<string> texts) =>
            texts.Select(_ => new float[Vectors.Dimension]).ToArray();

        public Embedding[] EmbedImages(IReadOnlyList<string> paths, int budget) =>
            paths.Select(_ => new Embedding(null, "test fixture")).ToArray();

        public Embedding[] EmbedAudio(IReadOnlyList<string> paths) =>
            paths.Select(_ => new Embedding(null, "test fixture")).ToArray();

        public void Close() { }
        public void Dispose() { }
    }

    private sealed class WindowDispatcher : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new();
        private Dispatcher? _dispatcher;
        private MainWindow? _window;
        private Exception? _startupError;

        public WindowDispatcher(Engine engine)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    _window = new MainWindow(engine, new Settings());
                    _started.Set();
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    _startupError = ex;
                    _started.Set();
                }
            })
            {
                IsBackground = true,
                Name = "DigUp WPF search test STA",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            Assert.True(_started.Wait(TimeSpan.FromSeconds(10)), "WPF dispatcher did not start.");
            if (_startupError is not null)
                throw new InvalidOperationException("Could not create the WPF search window.", _startupError);
        }

        public void Invoke(Action<MainWindow> action) =>
            _dispatcher!.Invoke(() => action(_window!), DispatcherPriority.Send);

        public T Invoke<T>(Func<MainWindow, T> action) =>
            _dispatcher!.Invoke(() => action(_window!), DispatcherPriority.Send);

        public void Dispose()
        {
            if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
            {
                _dispatcher.Invoke(() =>
                {
                    try { _window?.Close(); } catch { }
                    _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                });
            }

            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "WPF dispatcher did not stop.");
            _started.Dispose();
        }
    }

    private static object GetField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target) ?? throw new InvalidOperationException($"Field {name} was null.");
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }
}

[CollectionDefinition("WPF search window", DisableParallelization = true)]
public sealed class WpfSearchWindowCollection { }
