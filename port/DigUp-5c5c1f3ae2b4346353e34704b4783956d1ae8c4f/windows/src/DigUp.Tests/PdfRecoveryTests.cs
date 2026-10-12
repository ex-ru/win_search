using DigUp.Core;
using Xunit;

namespace DigUp.Tests;

public class PdfRecoveryTests
{
    private const string MissingPdfiumDetail = "unreadable: pdfium.dll is missing";

    [Fact]
    public void RetryMissingPdfiumFailures_RequeuesOnlyMatchingFailedPdfs_AndIsIdempotent()
    {
        using var fixture = new IndexFixture();
        IndexStore store = fixture.Store;

        long missingWithSuffix = AddFile(fixture, "reports/missing-with-suffix.pdf", FileKind.Pdf);
        Mark(store, missingWithSuffix, "failed", MissingPdfiumDetail + ": load failed");
        long missingPage = AddPage(store, missingWithSuffix);

        long missingExact = AddFile(fixture, "reports/missing-exact.pdf", FileKind.Pdf);
        Mark(store, missingExact, "failed", MissingPdfiumDetail);
        long exactPage = AddPage(store, missingExact);

        long corruptPdf = AddFile(fixture, "reports/corrupt.pdf", FileKind.Pdf);
        const string corruptDetail = "unreadable: PDF is corrupt";
        Mark(store, corruptPdf, "failed", corruptDetail);
        long corruptPage = AddPage(store, corruptPdf);

        long passwordPdf = AddFile(fixture, "reports/password.pdf", FileKind.Pdf);
        const string passwordDetail = "unreadable: PDF is password protected";
        Mark(store, passwordPdf, "failed", passwordDetail);

        long otherFailure = AddFile(fixture, "reports/other-failure.pdf", FileKind.Pdf);
        const string otherFailureDetail = "unreadable: extractor failed";
        Mark(store, otherFailure, "failed", otherFailureDetail);

        long wrongPrefix = AddFile(fixture, "reports/wrong-prefix.pdf", FileKind.Pdf);
        const string wrongPrefixDetail = "retry: " + MissingPdfiumDetail;
        Mark(store, wrongPrefix, "failed", wrongPrefixDetail);

        long document = AddFile(fixture, "reports/document.doc", FileKind.Doc);
        Mark(store, document, "failed", MissingPdfiumDetail);

        long pendingPdf = AddFile(fixture, "reports/pending.pdf", FileKind.Pdf);
        Mark(store, pendingPdf, "pending", MissingPdfiumDetail);

        long donePdf = AddFile(fixture, "reports/done.pdf", FileKind.Pdf);
        Mark(store, donePdf, "done", MissingPdfiumDetail);

        long partialPdf = AddFile(fixture, "reports/partial.pdf", FileKind.Pdf);
        Mark(store, partialPdf, "partial", MissingPdfiumDetail);

        Assert.Equal(1, FtsRowCount(store, missingPage));
        Assert.Equal(1, FtsRowCount(store, exactPage));
        Assert.Equal(1, FtsRowCount(store, corruptPage));

        Assert.Equal(2, store.RetryMissingPdfiumFailures());
        Assert.Equal(0, store.RetryMissingPdfiumFailures());

        AssertRequeued(store, missingWithSuffix);
        AssertRequeued(store, missingExact);
        Assert.Equal(1, SegmentCount(store, missingWithSuffix)); // Keep the name segment.
        Assert.Equal(1, SegmentCount(store, missingExact));
        Assert.Equal(0, FtsRowCount(store, missingPage));
        Assert.Equal(0, FtsRowCount(store, exactPage));

        AssertUntouched(store, corruptPdf, "failed", corruptDetail);
        AssertUntouched(store, passwordPdf, "failed", passwordDetail);
        AssertUntouched(store, otherFailure, "failed", otherFailureDetail);
        AssertUntouched(store, wrongPrefix, "failed", wrongPrefixDetail);
        AssertUntouched(store, document, "failed", MissingPdfiumDetail);
        AssertUntouched(store, pendingPdf, "pending", MissingPdfiumDetail);
        AssertUntouched(store, donePdf, "done", MissingPdfiumDetail);
        AssertUntouched(store, partialPdf, "partial", MissingPdfiumDetail);
        Assert.Equal(2, SegmentCount(store, corruptPdf));
        Assert.Equal(1, FtsRowCount(store, corruptPage));
    }

    [Fact]
    public void RetryMissingPdfiumFailures_RespectsConfiguredScope()
    {
        using var fixture = new IndexFixture();
        IndexStore store = fixture.Store;

        long included = AddFile(fixture, "included/recover.pdf", FileKind.Pdf);
        Mark(store, included, "failed", MissingPdfiumDetail + ": load failed");
        long includedPage = AddPage(store, included);

        long excluded = AddFile(fixture, "excluded/recover.pdf", FileKind.Pdf);
        Mark(store, excluded, "failed", MissingPdfiumDetail + ": load failed");
        long excludedPage = AddPage(store, excluded);

        store.Scope = new List<string> { Path.Combine(fixture.Root, "included") };

        Assert.Equal(1, store.RetryMissingPdfiumFailures());

        AssertRequeued(store, included);
        Assert.Equal(0, FtsRowCount(store, includedPage));
        AssertUntouched(store, excluded, "failed", MissingPdfiumDetail + ": load failed");
        Assert.Equal(1, FtsRowCount(store, excludedPage));
    }

    private static long AddFile(IndexFixture fixture, string relativePath, FileKind kind) =>
        fixture.Store.Insert(new CrawlCandidate(Path.Combine(fixture.Root, relativePath), 42, 1, kind));

    private static void Mark(IndexStore store, long id, string state, string detail) =>
        store.Finish(id, state, detail, new Dictionary<string, string?> { ["attempts"] = "3" });

    private static long AddPage(IndexStore store, long fileId)
    {
        store.AddSegment(fileId, SegmentKind.Page, Modality.Text,
            loc: 1, text: "obsolete page recovery marker");
        long id = 0;
        store.Db.Query("SELECT id FROM segments WHERE file = ? AND kind = 'page'",
            new[] { SqlValue.Int(fileId) }, r => id = r.GetInt64(0));
        return id;
    }

    private static void AssertRequeued(IndexStore store, long id)
    {
        FileSnapshot snapshot = GetFile(store, id);
        Assert.Equal("pending", snapshot.State);
        Assert.Null(snapshot.Detail);
        Assert.Null(snapshot.Info);
        Assert.False(snapshot.HasIndexedAt);
        Assert.Empty(store.InfoOf(id));
    }

    private static void AssertUntouched(IndexStore store, long id, string state, string detail)
    {
        FileSnapshot snapshot = GetFile(store, id);
        Assert.Equal(state, snapshot.State);
        Assert.Equal(detail, snapshot.Detail);
        Assert.True(snapshot.HasIndexedAt);
        Assert.Equal("3", store.InfoOf(id)["attempts"]);
    }

    private static FileSnapshot GetFile(IndexStore store, long id)
    {
        FileSnapshot? result = null;
        store.Db.Query("SELECT state, detail, info, indexed_at FROM files WHERE id = ?",
            new[] { SqlValue.Int(id) }, r =>
            {
                result = new FileSnapshot(r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    !r.IsDBNull(3));
            });
        return result ?? throw new InvalidOperationException($"File {id} was not found.");
    }

    private static int SegmentCount(IndexStore store, long fileId)
    {
        int count = 0;
        store.Db.Query("SELECT COUNT(*) FROM segments WHERE file = ?",
            new[] { SqlValue.Int(fileId) }, r => count = (int)r.GetInt64(0));
        return count;
    }

    private static int FtsRowCount(IndexStore store, long segmentId)
    {
        int count = 0;
        store.Db.Query("SELECT COUNT(*) FROM fts WHERE rowid = ?",
            new[] { SqlValue.Int(segmentId) }, r => count = (int)r.GetInt64(0));
        return count;
    }

    private sealed record FileSnapshot(string State, string? Detail, string? Info, bool HasIndexedAt);

    private sealed class IndexFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "digup_pdf_recovery_" + Guid.NewGuid().ToString("N"));
        public IndexStore Store { get; }

        public IndexFixture() => Store = new IndexStore(Path.Combine(Root, "index"));

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
