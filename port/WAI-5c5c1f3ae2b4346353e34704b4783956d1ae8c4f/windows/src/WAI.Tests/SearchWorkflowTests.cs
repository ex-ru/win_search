using System.IO;
using WAI.App;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class SearchWorkflowTests
{
    [Fact]
    public void QueryFastPathCpu_DefaultIsTrue()
    {
        var settings = new Settings();
        Assert.True(settings.QueryFastPathCpu);
    }

    [Fact]
    public void Loc_SearchingMeaning_IsProperlyLocalized()
    {
        string prev = Loc.CurrentLanguage;
        try
        {
            Loc.CurrentLanguage = "ru";
            Assert.Equal("Ищем по смыслу…", Loc.SearchingMeaning);
            Assert.Contains("совпадений (текст)", Loc.LexicalHitsSuffix(3));
            Assert.Contains("Fast-Path", Loc.HwQueryFastPath);

            Loc.CurrentLanguage = "en";
            Assert.Equal("Searching by meaning…", Loc.SearchingMeaning);
            Assert.Contains("keyword matches", Loc.LexicalHitsSuffix(3));
            Assert.Contains("Fast-Path", Loc.HwQueryFastPath);
        }
        finally
        {
            Loc.CurrentLanguage = prev;
        }
    }

    [Fact]
    public void SearchLexical_FindsTextWithoutVectors()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wai_search_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "doc1.txt");
            File.WriteAllText(filePath, "Договор поставки оборудования и комплектующих для склада");

            using var store = new IndexStore(tempDir);
            long fileId = store.Insert(new CrawlCandidate(filePath, 100, 1, FileKind.Doc));
            store.AddSegment(fileId, SegmentKind.Chunk, Modality.Text,
                excerpt: "Договор поставки оборудования и комплектующих для склада",
                text: "Договор поставки оборудования и комплектующих для склада");
            store.Finish(fileId, "indexed");

            using var searcher = new Searcher(store, loadVectors: false);
            var results = searcher.Search("договор", queryVector: null, limit: 10);

            Assert.NotEmpty(results);
            Assert.Equal(Path.GetFullPath(filePath), Path.GetFullPath(results[0].Path), StringComparer.OrdinalIgnoreCase);
            Assert.Contains("договор", results[0].Excerpt?.ToLowerInvariant());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TwoPhaseSearch_LexicalAndVectorReRanking()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wai_twophase_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string file1 = Path.Combine(tempDir, "contract.txt");
            File.WriteAllText(file1, "Финансовый годовой отчет компании за 2025 год");

            string file2 = Path.Combine(tempDir, "readme.txt");
            File.WriteAllText(file2, "Инструкция пользователя по установке программного обеспечения");

            using var store = new IndexStore(tempDir);
            long fileId1 = store.Insert(new CrawlCandidate(file1, 100, 1, FileKind.Doc));
            store.AddSegment(fileId1, SegmentKind.Chunk, Modality.Text,
                excerpt: "Финансовый годовой отчет компании за 2025 год",
                text: "Финансовый годовой отчет компании за 2025 год");
            store.Finish(fileId1, "indexed");

            long fileId2 = store.Insert(new CrawlCandidate(file2, 100, 1, FileKind.Doc));
            store.AddSegment(fileId2, SegmentKind.Chunk, Modality.Text,
                excerpt: "Инструкция пользователя по установке программного обеспечения",
                text: "Инструкция пользователя по установке программного обеспечения");
            store.Finish(fileId2, "indexed");

            using var searcher = new Searcher(store, loadVectors: false);

            // Phase 1: Direct lexical search (< 5 ms)
            var phase1 = searcher.Search("отчет", queryVector: null, limit: 10);
            Assert.Single(phase1);
            Assert.Equal(Path.GetFullPath(file1), Path.GetFullPath(phase1[0].Path), StringComparer.OrdinalIgnoreCase);

            // Phase 2: Vector search with mock embedding (should succeed and retain/boost results)
            float[] mockVec = new float[Vectors.Dimension];
            mockVec[0] = 1.0f;
            var phase2 = searcher.Search("отчет", queryVector: mockVec, limit: 10);
            Assert.NotEmpty(phase2);
            Assert.Equal(Path.GetFullPath(file1), Path.GetFullPath(phase2[0].Path), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ExactFilenameMatch_RanksFirstEvenWithSemanticMatches()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wai_filename_boost_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string targetFile = Path.Combine(tempDir, "обои.jpg");
            File.WriteAllText(targetFile, "dummy image content");

            string semanticMatchFile = Path.Combine(tempDir, "calendar_2027.tif");
            File.WriteAllText(semanticMatchFile, "dummy image content");

            using var store = new IndexStore(tempDir);

            // File 1: target image "обои.jpg"
            long fileId1 = store.Insert(new CrawlCandidate(targetFile, 100, 1, FileKind.Doc));
            store.AddSegment(fileId1, SegmentKind.Name, Modality.Text,
                excerpt: "обои.jpg",
                text: "обои jpg");
            store.Finish(fileId1, "done");

            // File 2: other file where text mentions "обои"
            long fileId2 = store.Insert(new CrawlCandidate(semanticMatchFile, 100, 1, FileKind.Doc));
            store.AddSegment(fileId2, SegmentKind.Chunk, Modality.Text,
                excerpt: "Смени обстановку, хотя бы рабочие обои на ноутбуке",
                text: "Смени обстановку хотя бы рабочие обои на ноутбуке");
            store.Finish(fileId2, "done");

            using var searcher = new Searcher(store, loadVectors: false);

            var hits = searcher.Search("обои.jpg", queryVector: null, limit: 10);
            Assert.NotEmpty(hits);
            // Target file should rank first with substantial score boost (> 10.0)
            Assert.Equal(Path.GetFullPath(targetFile), Path.GetFullPath(hits[0].Path), StringComparer.OrdinalIgnoreCase);
            Assert.True(hits[0].Score > 10.0, $"Expected score > 10.0 for exact filename match, got {hits[0].Score}");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

