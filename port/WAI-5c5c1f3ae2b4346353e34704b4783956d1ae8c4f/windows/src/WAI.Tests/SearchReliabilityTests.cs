using WAI.Core;
using Xunit;

namespace WAI.Tests;

public sealed class SearchReliabilityTests
{
    [Fact]
    public async Task Transaction_RollbackCannotUndoAnotherThreadsWrite()
    {
        using var fixture = new Fixture();
        var db = fixture.Store.Db;
        db.Execute("CREATE TABLE regression(value TEXT)");
        using var transactionEntered = new ManualResetEventSlim();
        using var writerAttempted = new ManualResetEventSlim();
        using var writerFinished = new ManualResetEventSlim();
        var rollback = Task.Run(() => Assert.Throws<InvalidOperationException>(() => db.Transaction(() =>
        {
            db.Run("INSERT INTO regression VALUES('rolled back')");
            transactionEntered.Set();
            Assert.True(writerAttempted.Wait(TimeSpan.FromSeconds(5)));
            writerFinished.Wait(TimeSpan.FromMilliseconds(150));
            throw new InvalidOperationException("test rollback");
        })));
        var independent = Task.Run(() =>
        {
            Assert.True(transactionEntered.Wait(TimeSpan.FromSeconds(5)));
            writerAttempted.Set();
            db.Run("INSERT INTO regression VALUES('committed')");
            writerFinished.Set();
        });
        await Task.WhenAll(rollback, independent).WaitAsync(TimeSpan.FromSeconds(10));
        var rows = new List<string>();
        db.Query("SELECT value FROM regression", r => rows.Add(r.GetString(0)));
        Assert.Equal(new[] { "committed" }, rows);
    }

    [Fact]
    public void NestedTransaction_RollsBackOnlyItsOwnSavepoint()
    {
        using var fixture = new Fixture();
        var db = fixture.Store.Db;
        db.Execute("CREATE TABLE regression(value TEXT)");
        db.Transaction(() =>
        {
            db.Run("INSERT INTO regression VALUES('outer')");
            Assert.Throws<InvalidOperationException>(() => db.Transaction(() =>
            {
                db.Run("INSERT INTO regression VALUES('inner')");
                throw new InvalidOperationException("test rollback");
            }));
        });
        var rows = new List<string>();
        db.Query("SELECT value FROM regression", r => rows.Add(r.GetString(0)));
        Assert.Equal(new[] { "outer" }, rows);
    }

    [Fact]
    public async Task Search_UsesCommittedSnapshotWithoutWaitingForWriter()
    {
        using var fixture = new Fixture();
        long id = fixture.Add("C:/docs/one.txt", "needle");
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        using var changed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => Assert.Throws<InvalidOperationException>(() => fixture.Store.Transact(() =>
        {
            fixture.Store.Delete(id);
            changed.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            throw new InvalidOperationException("test rollback");
        })));
        try
        {
            Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
            var hits = await Task.Run(() => searcher.Search("needle", null)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Single(hits);
        }
        finally { release.Set(); await writer; }
    }

    [Fact]
    public void FileIdentity_RoundtripsWithoutSwappingVolumeAndInode()
    {
        using var fixture = new Fixture();
        fixture.Store.Insert(new CrawlCandidate("C:/docs/identity.txt", 42, 1, FileKind.Doc, Device: 17, Inode: 123456));
        var file = Assert.Single(fixture.Store.FilesUnder(new[] { "C:/docs" }));
        Assert.Equal(17, file.Device);
        Assert.Equal(123456, file.Inode);
    }

    [Fact]
    public void ConcurrentInserts_KeepNameAndContentAttachedToTheirFile()
    {
        using var fixture = new Fixture();
        Parallel.For(0, 80, i => fixture.Add($"C:/docs/item{i}.txt", $"content{i}"));
        int rows = 0;
        fixture.Store.Db.Query("SELECT f.path, fts.body FROM fts JOIN segments s ON s.id = fts.rowid JOIN files f ON f.id = s.file WHERE s.kind = 'chunk'", r =>
        {
            string name = Path.GetFileNameWithoutExtension(r.GetString(0));
            Assert.Equal("content" + name[4..], r.GetString(1));
            rows++;
        });
        Assert.Equal(80, rows);
    }

    [Fact]
    public void Scope_ChangesImmediatelyAndUsesWindowsCaseForCyrillicPaths()
    {
        using var fixture = new Fixture();
        fixture.Add("C:/данные/отчеты/a.txt", "needle");
        fixture.Add("C:/данные/отчеты2/b.txt", "needle");
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        var roots = new List<string> { @"c:\ДАННЫЕ\ОТЧЕТЫ" };
        fixture.Store.Scope = roots;
        roots.Clear(); // Setter owns its copy.
        Assert.EndsWith("a.txt", Assert.Single(searcher.Search("needle", null)).Path);
        fixture.Store.Scope = new();
        Assert.Empty(searcher.Search("needle", null));
        fixture.Store.Scope = null;
        Assert.Equal(2, searcher.Search("needle", null).Count);
    }

    [Fact]
    public void ScopeRange_UsesItsWindowsCollationIndex()
    {
        using var fixture = new Fixture();
        fixture.Add("C:/docs/one.txt", "needle");
        var details = new List<string>();
        fixture.Store.Db.Query("EXPLAIN QUERY PLAN SELECT id, kind FROM files WHERE path COLLATE WAI_PATH >= ? AND path COLLATE WAI_PATH < ?",
            new[] { SqlValue.Text("c:/DOCS/"), SqlValue.Text("c:/DOCS0") }, r => details.Add(r.GetString(3)));
        Assert.Contains(details, detail => detail.Contains("files_path_windows", StringComparison.Ordinal));
    }

    [Fact]
    public void ScopeAndType_AreAppliedBeforeLexicalCandidateLimit()
    {
        using var fixture = new Fixture();
        fixture.Store.Transact(() =>
        {
            for (int i = 0; i < 1050; i++) fixture.Add($"C:/excluded/{i}.txt", "needle");
            fixture.Add("C:/included/target.pdf", "needle", FileKind.Pdf);
        });
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        Assert.EndsWith("target.pdf", Assert.Single(searcher.Search("needle", null, new() { FileKind.Pdf }, limit: 1)).Path);
        fixture.Store.Scope = new() { "C:/included" };
        Assert.EndsWith("target.pdf", Assert.Single(searcher.Search("needle", null, limit: 1)).Path);
    }

    [Fact]
    public void SemanticScope_ExcludesRemovedFoldersAndEmptyScope()
    {
        using var fixture = new Fixture();
        fixture.Add("C:/included/one.txt", "", vector: new float[] { 1, 0, 0 });
        fixture.Add("C:/excluded/two.txt", "", vector: new float[] { 0, 1, 0 });
        using var searcher = new Searcher(fixture.Store);
        fixture.Store.Scope = new() { "C:/included" };
        Assert.EndsWith("one.txt", Assert.Single(searcher.Search("", new float[] { 1, 0, 0 }, zGate: -100)).Path);
        fixture.Store.Scope = new();
        Assert.Empty(searcher.Search("", new float[] { 1, 0, 0 }, zGate: -100));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SmallCorpus_CanReturnAStandoutSemanticMatch(int count)
    {
        using var fixture = new Fixture();
        for (int i = 0; i < count; i++)
            fixture.Add($"C:/small/{i}.txt", "", vector: new float[] { i == 0 ? 0.84f : 0.66f, 0 });
        using var searcher = new Searcher(fixture.Store);
        Assert.EndsWith("0.txt", Assert.Single(searcher.Search("unseen paraphrase", new float[] { 1, 0 })).Path);
    }

    [Theory]
    [InlineData(-0.1f, -0.8f, -0.9f)]
    [InlineData(0.7f, 0.7f, 0.7f)]
    [InlineData(0.700f, 0.701f, 0.699f)]
    [InlineData(0.6794f, 0.7049f, 0.6331f)]
    [InlineData(0.6696f, 0.6995f, 0.6331f)]
    public void SmallCorpus_DoesNotPromoteNegativeEqualOrWeakMatches(float a, float b, float c)
    {
        using var fixture = new Fixture();
        var cosines = new[] { a, b, c };
        for (int i = 0; i < cosines.Length; i++) fixture.Add($"C:/small/{i}.txt", "", vector: new float[] { cosines[i], 0 });
        using var searcher = new Searcher(fixture.Store);
        Assert.Empty(searcher.Search("unseen paraphrase", new float[] { 1, 0 }));
    }

    [Fact]
    public void SingleVector_RequiresLexicalEvidenceAtTheDefaultThreshold()
    {
        using var fixture = new Fixture();
        fixture.Add("C:/small/one.txt", "known", vector: new float[] { 1, 0 });
        using var searcher = new Searcher(fixture.Store);
        Assert.Empty(searcher.Search("unseen paraphrase", new float[] { 1, 0 }));
        Assert.Single(searcher.Search("known", new float[] { 1, 0 }));
    }

    [Fact]
    public void RequeuedFile_DoesNotReuseItsStaleSemanticVectorBeforeReload()
    {
        using var fixture = new Fixture();
        long id = fixture.Add("C:/docs/changed.txt", "before", vector: new float[] { 1, 0, 0 });
        fixture.Store.Finish(id, "done");
        using var searcher = new Searcher(fixture.Store);
        fixture.Store.Requeue(id, new CrawlCandidate("C:/docs/changed.txt", 42, 2, FileKind.Doc));
        fixture.Store.AddSegment(id, SegmentKind.Chunk, Modality.Text, text: "replacement", vector: new float[] { 0, 1, 0 });
        fixture.Store.Finish(id, "done");
        Assert.Null(Assert.Single(searcher.Search("replacement", new float[] { 1, 0, 0 }, zGate: -100)).Cosine);
        searcher.Reload();
        Assert.Equal(1d, Assert.Single(searcher.Search("replacement", new float[] { 0, 1, 0 }, zGate: -100)).Cosine);
    }

    [Fact]
    public void DeletedAndReusedFileId_DoesNotInheritAnotherFilesVector()
    {
        using var fixture = new Fixture();
        long first = fixture.Add("C:/docs/first.txt", "before", vector: new float[] { 1, 0, 0 });
        fixture.Store.Finish(first, "done");
        using var searcher = new Searcher(fixture.Store);
        fixture.Store.Delete(first);
        long replacement = fixture.Add("C:/docs/second.txt", "replacement", vector: new float[] { 0, 1, 0 });
        fixture.Store.Finish(replacement, "done");
        Assert.Equal(first, replacement); // SQLite may reuse a deleted INTEGER PRIMARY KEY.
        Assert.Empty(searcher.Search("", new float[] { 1, 0, 0 }, zGate: -100));
        var hit = Assert.Single(searcher.Search("replacement", new float[] { 1, 0, 0 }, zGate: -100));
        Assert.EndsWith("second.txt", hit.Path);
        Assert.Null(hit.Cosine);
    }

    [Theory]
    [InlineData("договор", "договора")]
    [InlineData("договора", "договор")]
    [InlineData("счета", "счёт")]
    [InlineData("счёт", "счета")]
    [InlineData("отчет", "отчёты")]
    [InlineData("попытками", "попыток")]
    [InlineData("попыток", "попытками")]
    [InlineData("поршнем", "поршень")]
    [InlineData("поршень", "поршнем")]
    [InlineData("акварелью", "акварели")]
    [InlineData("акварели", "акварелью")]
    [InlineData("глазурью", "глазури")]
    [InlineData("глазури", "глазурью")]
    [InlineData("TRX50", "TRX50")]
    [InlineData("123456", "123456")]
    public void KeywordSearch_FindsWordFormsAndExactIdentifiers(string query, string content)
    {
        using var fixture = new Fixture();
        fixture.Add("C:/docs/positive.txt", content);
        fixture.Add("C:/docs/negative.txt", "счетчик договоренность TRX500 1234567");
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        Assert.EndsWith("positive.txt", Assert.Single(searcher.Search(query, null)).Path);
    }

    [Fact]
    public void RussianInflections_HighlightTheOriginalSpelling()
    {
        Assert.Contains("«счёт»", SearchText.Snippet("Оплата на счёт компании", new[] { "счета" }));
    }

    [Fact]
    public void DifferentDimensions_StayIndependentInTheSameProcess()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        first.Add("C:/one.txt", "needle", vector: new float[] { 1, 0, 0 });
        second.Add("C:/two.txt", "needle", vector: new float[] { 1, 0, 0, 0, 0 });
        using var a = new Searcher(first.Store);
        using var b = new Searcher(second.Store);
        Assert.Equal(3, first.Store.VectorDimension);
        Assert.Equal(5, second.Store.VectorDimension);
        Assert.NotNull(Assert.Single(a.Search("needle", new float[] { 1, 0, 0 })).Cosine);
        Assert.NotNull(Assert.Single(b.Search("needle", new float[] { 1, 0, 0, 0, 0 })).Cosine);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Add("C:/invalid.txt", "", vector: new float[5]));
    }

    [Theory]
    [InlineData("code.sqlite")]
    [InlineData("code_bge-m3.db")]
    [InlineData("CODE_perplexity.db")]
    public void CodeIndexes_AreRecognizedAcrossModels(string fileName)
    {
        using var fixture = new Fixture(fileName);
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        Assert.True(searcher.IsCode);
    }

    [Fact]
    public void AlternativeCodeIndex_PointsToDefinitionInsteadOfEarlierUse()
    {
        using var fixture = new Fixture("code_bge-m3.db");
        long id = fixture.Store.Insert(new CrawlCandidate("C:/code/sample.cs", 42, 1, FileKind.Code));
        fixture.Store.AddSegment(id, SegmentKind.Lines, Modality.None, loc: 1, text: "var result = ProcessPayment();");
        fixture.Store.AddSegment(id, SegmentKind.Lines, Modality.None, loc: 20, text: "void ProcessPayment() { }");
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        Assert.Equal(20, Assert.Single(searcher.Search("ProcessPayment", null)).Focus);
    }

    [Fact]
    public void ExplicitIndexIdentity_DoesNotDependOnFilename()
    {
        using var fixture = new Fixture("arbitrary.db", isCodeIndex: true);
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        Assert.True(searcher.IsCode);
    }

    [Fact]
    public void Search_CanCancelAndThenBeUsedAgain()
    {
        using var fixture = new Fixture();
        fixture.Add("C:/one.txt", "needle");
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => searcher.Search("needle", null, cancellationToken: cancellation.Token));
        Assert.Single(searcher.Search("needle", null));
        Assert.Empty(searcher.Search("needle", null, kinds: new()));
    }

    [Fact]
    public void Prune_PreservesFilesUnderConfiguredOfflineRoot()
    {
        using var fixture = new Fixture();
        fixture.Add("Z:/offline/shared.txt", "needle");
        Assert.Equal(0, fixture.Store.Prune(new[] { "Z:/offline" }));
    }

    [Fact]
    public void StrongCount_CountsScoresAtOrAboveThreshold()
    {
        using var fixture = new Fixture();
        fixture.Add("C:/one.txt", "needle");
        using var searcher = new Searcher(fixture.Store, loadVectors: false);
        var hit = Assert.Single(searcher.Search("needle", null));
        Assert.Equal(2, Searcher.StrongCount(new() { hit with { Score = 10 }, hit with { Score = 5 }, hit with { Score = 4.9 } }));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wai-core-tests-" + Guid.NewGuid().ToString("N"));
        public IndexStore Store { get; }
        public Fixture(string fileName = IndexStore.MainFile, bool? isCodeIndex = null) => Store = new(_root, fileName, isCodeIndex);
        public long Add(string path, string text, FileKind kind = FileKind.Doc, float[]? vector = null)
        {
            long id = Store.Insert(new CrawlCandidate(path, 42, 1, kind));
            Store.AddSegment(id, SegmentKind.Chunk, Modality.Text, text: text, vector: vector);
            return id;
        }
        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
