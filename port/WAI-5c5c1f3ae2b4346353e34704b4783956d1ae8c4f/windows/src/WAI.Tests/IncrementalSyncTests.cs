using Xunit;
using WAI.Core;

namespace WAI.Tests;

public sealed class IncrementalSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WAI-sync-tests-" + Guid.NewGuid().ToString("N"));
    private sealed class ReportCrawler(CrawlReport report) : ICrawler
    {
        public IReadOnlyList<string> Visited { get; private set; } = [];
        public CrawlReport Crawl(IReadOnlyList<string> roots, IndexOptions options) { Visited = roots; return report; }
    }
    private Indexer Indexer(IndexStore store, ICrawler crawler) => new(store, new IndexOptions(), crawler,
        () => throw new InvalidOperationException("Sync must never load a model"), null!);

    [Fact]
    public void InaccessibleSubtreeAndFileKeepPreviousIndexedContent()
    {
        string files = Path.Combine(_root, "files");
        Directory.CreateDirectory(files);
        using var store = new IndexStore(Path.Combine(_root, "db"));
        string denied = Path.Combine(files, "denied");
        string busy = Path.Combine(files, "busy.txt");
        store.Insert(new(Path.Combine(denied, "kept.txt"), 5, 1, FileKind.Doc));
        store.Insert(new(busy, 5, 1, FileKind.Doc));
        store.Insert(new(Path.Combine(files, "deleted.txt"), 5, 1, FileKind.Doc));
        var crawler = new ReportCrawler(new([]) { InaccessiblePaths = [denied, busy] });
        using var indexer = Indexer(store, crawler);
        var summary = indexer.Sync([files]);
        Assert.Equal(1, summary.Removed);
        Assert.Equal(2, store.FilesUnder([files]).Count);
    }

    [Fact]
    public void TargetedSyncDoesNotWalkOrDeleteUnchangedSiblingSubtree()
    {
        string files = Path.Combine(_root, "files"), changed = Path.Combine(files, "changed"), untouched = Path.Combine(files, "untouched");
        Directory.CreateDirectory(changed); Directory.CreateDirectory(untouched);
        using var store = new IndexStore(Path.Combine(_root, "db"));
        store.Insert(new(Path.Combine(changed, "deleted.txt"), 5, 1, FileKind.Doc));
        store.Insert(new(Path.Combine(untouched, "kept.txt"), 5, 1, FileKind.Doc));
        var crawler = new ReportCrawler(new([]));
        using var indexer = Indexer(store, crawler);
        var summary = indexer.SyncChanges([files], [Path.Combine(changed, "deleted.txt")]);
        Assert.Equal([changed], crawler.Visited);
        Assert.Equal(1, summary.Removed);
        Assert.Single(store.FilesUnder([files]));
    }

    [Fact]
    public void RealCrawlerObservesChangesRenameAndDeletionWithoutModel()
    {
        string files = Path.Combine(_root, "files"); Directory.CreateDirectory(files);
        string first = Path.Combine(files, "original.txt"), renamed = Path.Combine(files, "renamed.txt");
        File.WriteAllText(first, "original content");
        using var store = new IndexStore(Path.Combine(_root, "db"));
        using var indexer = Indexer(store, new Crawler());
        Assert.Equal(1, indexer.Sync([files]).Added);
        File.AppendAllText(first, " more content");
        Assert.Equal(1, indexer.SyncChanges([files], [first]).Changed);
        File.Move(first, renamed);
        var move = indexer.SyncChanges([files], [first, renamed]);
        Assert.Equal(1, move.Moved);
        File.Delete(renamed);
        Assert.Equal(1, indexer.SyncChanges([files], [renamed]).Removed);
        Assert.Empty(store.FilesUnder([files]));
    }

    [Fact]
    public void CancelledCrawlDoesNotMutateIndex()
    {
        Directory.CreateDirectory(_root);
        using var store = new IndexStore(Path.Combine(_root, "db"));
        store.Insert(new(Path.Combine(_root, "keep.txt"), 1, 1, FileKind.Doc));
        using var indexer = Indexer(store, new Crawler());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => indexer.Sync([_root], cancelled.Token));
        Assert.Single(store.FilesUnder([_root]));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
