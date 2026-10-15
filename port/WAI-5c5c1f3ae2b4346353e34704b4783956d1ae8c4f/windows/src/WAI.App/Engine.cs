// Coordinates indexing, search snapshots and embedding workers.
using System.IO;
using WAI.Core;

namespace WAI.App;

public sealed class Engine : IDisposable
{
    private sealed record Configuration(string[] Folders, bool Code, bool SetupDone, string Model, string Hardware, bool QueryFastPathCpu)
    {
        public static Configuration Capture(Settings settings) => new(settings.Folders.ToArray(), settings.Code,
            settings.SetupDone, settings.EmbeddingModel, settings.HardwareMode, settings.QueryFastPathCpu);
    }

    private readonly Settings _settings;
    private Configuration _configuration;
    private readonly object _controlGate = new(), _loopGate = new(), _searchGate = new(), _queryGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _queryCancellation = new();
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private bool _requested, _fullScan, _loopRunning;
    private volatile bool _transitioning, _paused, _disposed, _batterySaver;
    private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _powerTimer;
    private IndexStore? _store, _codeStore;
    private Searcher? _searcher, _codeSearcher;
    private string? _activeStoreModel;
    private IEmbedder? _queryWorker;

    public string Status { get; private set; } = "Подготовка…";
    public event Action? Changed;
    public bool IsPaused => _paused;
    public bool IsIndexing { get; private set; }
    public bool IsSemanticAvailable { get; private set; }
    public int ProgressDone { get; private set; }
    public int ProgressTotal { get; private set; }

    public Engine(Settings settings)
    {
        _settings = settings;
        _configuration = Configuration.Capture(settings);
        _batterySaver = OnBatterySaver();
        if (_batterySaver) Status = "Обработка приостановлена для экономии батареи";
        _powerTimer = new System.Threading.Timer(_ => PowerChanged(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Start() => RequestIndex();

    public void SetPaused(bool paused)
    {
        lock (_controlGate)
        {
            if (_disposed || _paused == paused) return;
            _paused = paused;
            StopIndexing();
            DropQueryWorker();
            ResumeIndexing();
            Say(paused ? "Приостановлено" : "Подготовка индекса…");
            if (!paused) RequestIndex();
        }
    }

    public void ApplySettings()
    {
        lock (_controlGate)
        {
            if (_disposed) return;
            StopIndexing();
            DropQueryWorker();
            lock (_searchGate)
            {
                _configuration = Configuration.Capture(_settings);
                OpenStores();
                RefreshSearchers();
            }
            ResumeIndexing();
            RequestIndex();
        }
    }

    // A full scan supersedes queued paths. Only this loop owns indexer lifetimes.
    public void RequestIndex(IReadOnlyCollection<string>? changedPaths = null)
    {
        lock (_loopGate)
        {
            if (_disposed) return;
            _requested = true;
            if (changedPaths is null) { _fullScan = true; _changedPaths.Clear(); }
            else if (!_fullScan) foreach (string path in changedPaths) _changedPaths.Add(path);
            if (_transitioning || _paused || _batterySaver || _loopRunning) return;
            _loopRunning = true;
            _loop = Task.Run(IndexLoop);
        }
    }

    private void IndexLoop()
    {
        while (true)
        {
            CancellationTokenSource run;
            Configuration configuration;
            string[]? changes;
            lock (_loopGate)
            {
                if (_disposed || _transitioning || _paused || _batterySaver || !_requested) { _loopRunning = false; return; }
                configuration = _configuration;
                changes = _fullScan ? null : _changedPaths.ToArray();
                _requested = false; _fullScan = false; _changedPaths.Clear();
                _run = run = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            }
            IsIndexing = true;
            Changed?.Invoke();
            var previousPriority = Thread.CurrentThread.Priority;
            try
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                IndexPass(configuration, changes, run.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ErrorLog.Write("index: " + ex); Say(ex is IOException ? "Обработка остановлена: проверьте свободное место и доступ к файлам." : "Обработка остановлена. Подробности сохранены в журнале."); }
            finally
            {
                try { Thread.CurrentThread.Priority = previousPriority; } catch { }
                lock (_loopGate) { if (ReferenceEquals(_run, run)) _run = null; }
                run.Dispose();
                IsIndexing = false;
                Changed?.Invoke();
            }
        }
    }

    private void StopIndexing()
    {
        Task loop;
        lock (_loopGate)
        {
            _transitioning = true;
            _run?.Cancel();
            _queryCancellation.Cancel();
            loop = _loop;
        }
        loop.GetAwaiter().GetResult();
    }

    private void ResumeIndexing()
    {
        lock (_loopGate)
        {
            _queryCancellation.Dispose();
            _queryCancellation = new CancellationTokenSource();
            _transitioning = false;
        }
    }

    private void IndexPass(Configuration configuration, string[]? changes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!configuration.SetupDone) { Say("Выберите папки для поиска при первом запуске."); return; }
        if (configuration.Folders.Length == 0) { Say("Выберите папки для поиска в настройках."); return; }
        var definition = ModelCatalog.Get(configuration.Model);
        if (!definition.IsDownloaded(ModelFiles.InstalledDirectory))
            ModelFiles.EnsureModelAsync(definition, ModelFiles.InstalledDirectory, null, ct).GetAwaiter().GetResult();
        lock (_searchGate) OpenStores();
        Say(changes is null ? "Проверка выбранных папок…" : "Обновление изменённых файлов…");
        int failures = IndexOne(_store!, new IndexOptions(), configuration, changes, ct);
        if (configuration.Code) failures += IndexOne(_codeStore!, new IndexOptions { Code = true }, configuration, changes, ct);
        ct.ThrowIfCancellationRequested();
        RefreshSearchers();
        Say(failures == 0 ? "Готово к поиску" : $"Готово к поиску. Не удалось прочитать файлов: {failures}. Подробности в журнале.");
        WarmQueryWorker(ct);
    }

    private int IndexOne(IndexStore store, IndexOptions options, Configuration configuration, string[]? changes, CancellationToken ct)
    {
        var extractor = new DefaultExtractor(options, new WicImageDecoder(), new CompositeOcrEngine(),
            new PdfiumEngine(ExeNextTo("pdfium.dll")), FindFFmpeg(), null, ct);
        using var indexer = new Indexer(store, options, new Crawler(), () => MakeEmbedder(configuration, ct), extractor);
        indexer.ResetOnModelChange = (_, _) => DropQueryWorker();
        indexer.WorkerCount = 1;
        indexer.PrepareDegree = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        indexer.BatchYieldMs = 25;
        var sync = changes is null ? indexer.Sync(configuration.Folders, ct) : indexer.SyncChanges(configuration.Folders, changes, ct);
        int recovered = File.Exists(ExeNextTo("pdfium.dll")) ? store.RetryMissingPdfiumFailures() : 0;
        Say($"Ожидают обработки: {sync.Queued.Count + recovered}");
        int failures = 0;
        var refresh = System.Diagnostics.Stopwatch.StartNew();
        indexer.Run(shouldStop: () => ct.IsCancellationRequested, onEvent: e => {
            ProgressDone = e.Done; ProgressTotal = e.Total;
            if (e.State == FileState.Failed)
            {
                failures++;
                ErrorLog.Write($"Cannot read {e.Path}: {e.Note}");
            }
            Say($"Обрабатывается {e.Done} из {e.Total}: {Short(e.Path)}");
            if (refresh.Elapsed >= TimeSpan.FromSeconds(5)) { RefreshSearchers(); refresh.Restart(); }
        });
        ct.ThrowIfCancellationRequested();
        indexer.FinishLongFiles(shouldStop: () => ct.IsCancellationRequested);
        return failures;
    }

    public async Task EnsureModelsAsync(IProgress<(string File, double Frac)>? progress, CancellationToken ct)
    {
        var configuration = Configuration.Capture(_settings);
        await ModelFiles.EnsureModelAsync(ModelCatalog.Get(configuration.Model), ModelFiles.InstalledDirectory, progress, ct).ConfigureAwait(false);
        await Task.Run(() => {
            using var probe = new WorkerClient(CreateWorkerConfig(true, configuration, ct), ct);
            var vectors = probe.EmbedTexts([Prompts.Query("startup check")], ct);
            if (vectors.Length != 1 || vectors[0].Length != probe.Info.Dim) throw new InvalidOperationException("The search model failed its startup check.");
        }, ct).ConfigureAwait(false);
    }

    public bool ModelsReady => ModelCatalog.Get(_settings.EmbeddingModel).IsDownloaded(ModelFiles.InstalledDirectory);

    public record Hit(string Path, string Kind, string Where, string? Excerpt, double Score);

    public List<Hit> Search(string query, int limit = 20, HashSet<FileKind>? kinds = null, CancellationToken cancellationToken = default) =>
        SearchInternal(query, limit, kinds, cancellationToken, semantic: true);

    public List<Hit> SearchText(string query, int limit = 20, HashSet<FileKind>? kinds = null, CancellationToken cancellationToken = default) =>
        SearchInternal(query, limit, kinds, cancellationToken, semantic: false);

    private List<Hit> SearchInternal(string query, int limit, HashSet<FileKind>? kinds, CancellationToken cancellationToken, bool semantic)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = TextSanitize.Clean(query);
        lock (_searchGate)
        {
            if (_disposed || _transitioning) return [];
            OpenStores();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, _queryCancellation.Token);
            string? codeText = CodeQuery.TextOf(query);
            var codeSearcher = _configuration.Code && _codeStore is not null
                ? _codeSearcher ??= new Searcher(_codeStore, loadVectors: false) : null;
            var searcher = codeText is not null && codeSearcher is not null
                ? codeSearcher : _searcher ??= new Searcher(_store!, loadVectors: false);
            IReadOnlyList<float>? vector = semantic && !_paused && !_batterySaver ? TryEmbedQuery(query, linked.Token) : null;
            linked.Token.ThrowIfCancellationRequested();
            if (semantic) IsSemanticAvailable = vector is not null;
            var hits = searcher.Search(query, vector, limit: limit, kinds: kinds, cancellationToken: linked.Token);
            if (hits.Count == 0 && codeText is null && codeSearcher is not null)
                hits = codeSearcher.Search(query, vector, limit: limit, kinds: kinds, cancellationToken: linked.Token);
            return hits.Select(h => new Hit(h.Path, h.Kind.ToString(), WhereOf(h), EncodingHelper.FixMojibake(Clean(h.Excerpt)), h.Score)).ToList();
        }
    }

    // This lock pins stores and searchers for each complete operation.
    private T ReadStores<T>(Func<T> read, T fallback)
    {
        lock (_searchGate)
        {
            if (_disposed) return fallback;
            try { OpenStores(); return read(); }
            catch (Exception ex) { ErrorLog.Write("index statistics: " + ex.Message); return fallback; }
        }
    }

    public (int Indexed, int Remaining) GetIndexProgress() => ReadStores(() => {
        if (IsIndexing && ProgressTotal > 0) return (ProgressDone, Math.Max(0, ProgressTotal - ProgressDone));
        var (done, _, pending, _) = _store!.GetFileCounts();
        if (_codeStore is not null) { var (cDone, _, cPending, _) = _codeStore.GetFileCounts(); done += cDone; pending += cPending; }
        return (done, pending);
    }, (0, 0));

    public (int DoneFiles, int TotalFiles, int PendingFiles, int Vectors) GetDetailedProgress() => ReadStores(() => {
        var (done, total, pending, vectors) = _store!.GetFileCounts();
        if (_codeStore is not null) { var (d, t, p, v) = _codeStore.GetFileCounts(); done += d; total += t; pending += p; vectors += v; }
        return (done, total, pending, vectors);
    }, (0, 0, 0, 0));

    public FolderVectorStats? GetOrphanedStats() => ReadStores<FolderVectorStats?>(() => {
        var main = _store!.GetOrphanedStats(_configuration.Folders);
        if (_codeStore is null) return main;
        var code = _codeStore.GetOrphanedStats(_configuration.Folders);
        return new FolderVectorStats("Неактивные папки", main.Files + code.Files, main.Segments + code.Segments,
            main.Vectors + code.Vectors, main.Bytes + code.Bytes);
    }, null);

    public int PurgeOrphaned()
    {
        lock (_controlGate)
        {
            if (_disposed) return 0;
            StopIndexing();
            try
            {
                return ReadStores(() => {
                    int count = _store!.PurgeOrphaned(_configuration.Folders) + (_codeStore?.PurgeOrphaned(_configuration.Folders) ?? 0);
                    RefreshSearchers();
                    return count;
                }, 0);
            }
            finally { ResumeIndexing(); RequestIndex(); }
        }
    }
    public FolderVectorStats? GetFolderStats(string folder) => ReadStores<FolderVectorStats?>(() => _store!.GetFolderStats(folder), null);
    public FolderVectorStats? GetTotalStats() => ReadStores<FolderVectorStats?>(() => _store!.GetTotalStats(), null);
    public long GetDatabaseFileSize() => IndexStore.SizeOf(Path.Combine(Settings.IndexDir, GetMainIndexFileName()));

    public void ResetIndex(IReadOnlyList<string>? roots = null)
    {
        lock (_controlGate)
        {
            if (_disposed) return;
            StopIndexing(); DropQueryWorker();
            try
            {
                lock (_searchGate) { OpenStores(); _store?.ClearVectors(roots); _codeStore?.ClearVectors(roots); RefreshSearchers(); }
                Say("Индекс очищен. Подготовка к повторной обработке…");
            }
            catch (Exception ex) { ErrorLog.Write("ResetIndex: " + ex); Say("Не удалось очистить индекс: " + FirstLine(ex.Message)); }
            finally { ResumeIndexing(); }
            RequestIndex();
        }
    }

    public static (int Files, double Seconds, Dictionary<FileKind, int> ByKind) Estimate(IEnumerable<string> folders, bool code)
    {
        var options = new IndexOptions { Code = code };
        var report = Crawler.CrawlDetailed(folders, options);
        var estimate = Estimator.Estimate(report.Candidates, options);
        return (report.Candidates.Count, estimate.Seconds, estimate.Files);
    }

    public string GetMainIndexFileName(string? model = null) => (model ?? _configuration.Model) is "gemma2" ? IndexStore.MainFile : $"index_{model ?? _configuration.Model}.db";
    public string GetCodeIndexFileName(string? model = null) => (model ?? _configuration.Model) is "gemma2" ? IndexStore.CodeFile : $"code_{model ?? _configuration.Model}.db";

    // Caller holds _searchGate; stores are switched only after the index loop stopped.
    private void OpenStores()
    {
        string model = _configuration.Model;
        if (_activeStoreModel is not null && !string.Equals(_activeStoreModel, model, StringComparison.OrdinalIgnoreCase))
        {
            _searcher?.Dispose(); _searcher = null; _codeSearcher?.Dispose(); _codeSearcher = null;
            _store?.Dispose(); _store = null; _codeStore?.Dispose(); _codeStore = null;
        }
        _activeStoreModel = model;
        Directory.CreateDirectory(Settings.IndexDir);
        _store ??= OpenIndex(Settings.IndexDir, GetMainIndexFileName(model));
        _store.Scope = _configuration.Folders.ToList();
        if (_configuration.Code) { _codeStore ??= OpenIndex(Settings.IndexDir, GetCodeIndexFileName(model)); _codeStore.Scope = _configuration.Folders.ToList(); }
        else if (_codeStore is not null) { _codeSearcher?.Dispose(); _codeSearcher = null; _codeStore.Dispose(); _codeStore = null; }
    }

    private IndexStore OpenIndex(string dir, string file)
    {
        try { return new IndexStore(dir, file, isCodeIndex: file.StartsWith("code", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (IsCorrupt(ex))
        {
            string path = Path.Combine(dir, file);
            string aside = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            // Keep every journal beside the corrupt database for recovery.
            File.Move(path, aside);
            foreach (string suffix in new[] { "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Move(path + suffix, aside + suffix);
            Say("Повреждённый индекс сохранён отдельно. Создаётся новый индекс.");
            return new IndexStore(dir, file, isCodeIndex: file.StartsWith("code", StringComparison.OrdinalIgnoreCase));
        }
    }
    private static bool IsCorrupt(Exception ex) => ex.Message.ToLowerInvariant() is var text &&
        (text.Contains("not a database") || text.Contains("malformed") || text.Contains("corrupt"));

    private void RefreshSearchers()
    {
        lock (_searchGate)
        {
            _searcher?.Dispose(); _searcher = _store is null ? null : new Searcher(_store, loadVectors: false);
            _codeSearcher?.Dispose(); _codeSearcher = _codeStore is null ? null : new Searcher(_codeStore, loadVectors: false);
        }
    }

    private WorkerConfig CreateWorkerConfig(bool textOnly, Configuration configuration, CancellationToken ct)
    {
        var (backend, device, _) = HardwareDetector.ResolveBackend(configuration.Hardware, HardwareDetector.Detect());
        if (textOnly && configuration.QueryFastPathCpu) { backend = "cpu"; device = -1; }
        var args = new List<string> { "--backend", backend, "--model", configuration.Model };
        if (!textOnly) args.Add("--background");
        var environment = new Dictionary<string, string> {
            ["OMP_NUM_THREADS"] = (textOnly ? Math.Clamp(Environment.ProcessorCount / 2, 1, 8) : Math.Clamp(Environment.ProcessorCount / 4, 1, 4)).ToString()
        };
        if (device >= 0 && backend is "cuda" or "vulkan")
        {
            args.AddRange(["--device", device.ToString()]);
            environment[backend == "cuda" ? "CUDA_VISIBLE_DEVICES" : "GGML_VULKAN_DEVICE"] = device.ToString();
        }
        return new WorkerConfig(ExeNextTo("wai-embed.exe"), args, environment,
            Path.Combine(Settings.LogsDir, textOnly ? "query-worker.log" : "worker.log"), textOnly,
            RequestTimeout: textOnly ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(3), LifetimeCancellation: ct, IsBackground: !textOnly);
    }

    private IEmbedder MakeEmbedder(Configuration configuration, CancellationToken ct) =>
        File.Exists(ExeNextTo("wai-embed.exe")) ? new WorkerClient(CreateWorkerConfig(false, configuration, ct)) : new WordsOnly();

    private IReadOnlyList<float>? TryEmbedQuery(string query, CancellationToken ct)
    {
        try
        {
            lock (_queryGate)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(ExeNextTo("wai-embed.exe"))) return null;
                _queryWorker ??= new WorkerClient(CreateWorkerConfig(true, _configuration, _queryCancellation.Token), ct);
                string prompted = CodeQuery.TextOf(query) is not null ? Prompts.CodeQuery(query) : Prompts.Query(query);
                var rows = _queryWorker.EmbedTexts([prompted], ct);
                return rows.Length > 0 && rows[0].Length == _queryWorker.Info.Dim ? rows[0] : null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { DropQueryWorker(); throw; }
        catch (Exception ex) { ErrorLog.Write("query helper: " + FirstLine(ex.Message)); DropQueryWorker(); return null; }
    }

    private void WarmQueryWorker(CancellationToken ct)
    {
        if (_paused || _batterySaver) return;
        try { TryEmbedQuery("warmup", ct); }
        catch (OperationCanceledException) { }
    }

    public void ResetEmbedderWorker() => DropQueryWorker();
    private void DropQueryWorker()
    {
        // Interrupt first, then wait for ownership of the handle.
        try { Volatile.Read(ref _queryWorker)?.Close(); } catch { }
        lock (_queryGate) { try { _queryWorker?.Dispose(); } catch { } _queryWorker = null; }
    }

    private sealed class WordsOnly : IEmbedder
    {
        public WorkerInfo Info { get; } = new("none", "words-only", "words-only", "none", 768, true, null);
        public float[][] EmbedTexts(IReadOnlyList<string> texts) => texts.Select(_ => new float[768]).ToArray();
        public Embedding[] EmbedImages(IReadOnlyList<string> paths, int budget) => paths.Select(_ => new Embedding(null, "words only")).ToArray();
        public Embedding[] EmbedAudio(IReadOnlyList<string> paths) => paths.Select(_ => new Embedding(null, "words only")).ToArray();
        public void Close() { }
        public void Dispose() { }
    }

    private static string ExeNextTo(string name) => Path.Combine(AppContext.BaseDirectory, name);
    private static FFmpegPaths FindFFmpeg()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WAI", "tools", "ffmpeg");
        string dir = Environment.GetEnvironmentVariable("WAI_FFMPEG") is { Length: > 0 } configured ? configured :
            File.Exists(ExeNextTo("ffmpeg.exe")) ? AppContext.BaseDirectory : local;
        return new FFmpegPaths(Path.Combine(dir, "ffmpeg.exe"), Path.Combine(dir, "ffprobe.exe"));
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    private static bool OnBatterySaver() { try { return GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1; } catch { return false; } }
    private void PowerChanged()
    {
        lock (_controlGate)
        {
            if (_disposed) return;
            bool saver = OnBatterySaver();
            if (saver == _batterySaver) return;
            _batterySaver = saver;
            StopIndexing(); DropQueryWorker(); ResumeIndexing();
            Say(saver ? "Обработка приостановлена для экономии батареи" : "Подготовка индекса…");
            RequestIndex();
        }
    }

    private void Say(string text) { Status = text; Changed?.Invoke(); }
    private static string WhereOf(SearchHit hit) => hit.Loc is null ? "" : hit.Segment is SegmentKind.Lines or SegmentKind.Cells
        ? $"lines {Num(hit.Loc)}-{Num(hit.LocEnd)}" : $"@{Num(hit.Loc)}";
    private static string Num(double? value) => value?.ToString("0.##") ?? "";
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string clean = TextSanitize.Clean(text.Replace("«", "").Replace("»", "").Trim());
        return clean.Length > 160 ? TextSanitize.Cut(clean, 160) + "…" : clean;
    }
    private static string Short(string path) => path.Length > 70 ? "…" + path[^69..] : path;
    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    public void Dispose()
    {
        lock (_controlGate)
        {
            if (_disposed) return;
            _disposed = true;
            _powerTimer.Dispose();
            _shutdown.Cancel();
            StopIndexing(); DropQueryWorker();
            lock (_searchGate)
            {
                _searcher?.Dispose(); _searcher = null; _codeSearcher?.Dispose(); _codeSearcher = null;
                _store?.Dispose(); _store = null; _codeStore?.Dispose(); _codeStore = null;
            }
            _queryCancellation.Dispose(); _shutdown.Dispose();
        }
    }
}

// Debounce restarts on every event. Rename includes both names; overflow forces reconciliation.
public sealed class FileSystemWatcherRunner : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Timers.Timer _debounce;
    private readonly Action<IReadOnlyCollection<string>?> _onCalm;
    private readonly object _gate = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _fullScan, _disposed;

    public FileSystemWatcherRunner(Settings settings, Action onCalm) : this(settings, _ => onCalm()) { }
    public FileSystemWatcherRunner(Settings settings, Action<IReadOnlyCollection<string>?> onCalm, double debounceMilliseconds = 15000)
    {
        _onCalm = onCalm;
        _debounce = new System.Timers.Timer(debounceMilliseconds) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
        foreach (string folder in settings.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder) {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size
                };
                watcher.Changed += (_, e) => Queue(e.FullPath);
                watcher.Created += (_, e) => Queue(e.FullPath);
                watcher.Deleted += (_, e) => Queue(e.FullPath);
                watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                watcher.Error += (_, _) => Queue(null);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { ErrorLog.Write("watch folder: " + ex.Message); Queue(null); }
        }
    }

    private void Queue(string? path)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Missing paths can be deleted/renamed directories, even if their names end in .tmp.
            if (path is not null && File.Exists(path) && Rules.IsTemporaryOrLockFile(Path.GetFileName(path))) return;
            if (path is null || _paths.Count >= 10000) { _fullScan = true; _paths.Clear(); }
            else if (!_fullScan) _paths.Add(path);
            _debounce.Stop(); _debounce.Start();
        }
    }
    private void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var paths = _fullScan ? null : _paths.ToArray();
            _fullScan = false; _paths.Clear();
            _onCalm(paths);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Stop(); _debounce.Dispose();
            foreach (var watcher in _watchers) watcher.Dispose();
        }
    }
}
