using System.Buffers;
using System.Text.RegularExpressions;

namespace WAI.Core;

public sealed record SearchHit(
    string Path,
    FileKind Kind,
    double Score,
    double? Z,
    double? Cosine,
    int? KeywordRank,
    SegmentKind Segment,
    long SegmentId,
    double? Loc,
    double? LocEnd,
    string? Excerpt,
    double Modified,
    long Size,
    Dictionary<string, string> Info)
{
    /// <summary>Code: the line to open at, in the stretch (loc…loc_end). Null: the
    /// stretch's first line.</summary>
    public double? Focus { get; set; }
}

/// <summary>Hybrid search: meaning (the model's vectors) + words (FTS5).
/// Ports Searcher.swift.</summary>
public sealed class Searcher : IDisposable
{
    private readonly IndexStore _store;
    private readonly Database _db;
    private VectorStore? _vectors;
    private bool _disposed;
    // Keystroke searches share one searcher across threads while Reload
    // rebuilds these lists and Dispose frees them: every touch is gated,
    // so an in-flight search never sees a half-built or freed state.
    private readonly object _gate = new();
    private readonly List<long> _rowFile = new();
    private readonly List<long> _rowSegment = new();
    private readonly List<Modality> _rowModality = new();
    /// <summary>False for the vectors the later pass adds: scored, but they don't set the scale.</summary>
    private readonly List<bool> _rowSetsScale = new();
    private int _dimension = 768;
    private readonly Dictionary<long, FileVersion> _fileVersions = new();
    private readonly record struct FileVersion(string Path, FileKind Kind, long Size, double Modified,
        long Device, long Inode, string State, double? IndexedAt);
    private const string VersionColumns = "id, kind, path, size, mtime, device, inode, state, indexed_at";

    private static FileVersion ReadFileVersion(Microsoft.Data.Sqlite.SqliteDataReader row) =>
        new(row.GetString(2), FileKindStrings.Parse(row.NullableText(1)), row.GetInt64(3), row.GetDouble(4),
            row.GetInt64(5), row.GetInt64(6), row.GetString(7), row.IsDBNull(8) ? null : row.GetDouble(8));

    public string? VectorSource { get; private set; }
    /// <summary>False after <see cref="ReleaseVectors"/>: keyword search still works,
    /// and the next search with a query vector loads them again.</summary>
    public bool VectorsLoaded { get; private set; }

    public int VectorCount { get { lock (_gate) return _rowFile.Count; } }

    /// <summary>This is the code index's searcher: names are looked up as names.</summary>
    public bool IsCode => _store.IsCodeIndex;

    public const double WordWeight = 2.0;
    public const double CodeWordWeight = 1.0;

    /// <summary>With <paramref name="loadVectors"/> false only keyword search is ready;
    /// vectors load on the first meaning search.</summary>
    public Searcher(IndexStore store, bool loadVectors = true)
    {
        _store = store;
        _db = store.Db.OpenReader();
        try
        {
            if (loadVectors) Reload();
            else _db.Transaction(ReloadFiles);
        }
        catch { _db.Dispose(); throw; }
    }

    /// <summary>Reads the files and all vectors again (after indexing added some).</summary>
    public void Reload()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _db.Transaction(() => ReloadLocked());
        }
    }

    private void ReloadLocked(CancellationToken cancellationToken = default)
    {
        ReleaseVectors();
        ReloadFiles();
        int rows = 0;
        _db.Query("SELECT COUNT(*) FROM segments WHERE vec IS NOT NULL",
            r => { rows = (int)r.GetInt64(0); });
        if (rows <= 0)
        {
            VectorsLoaded = true; // nothing to load
            return;
        }
        var matrix = VectorStore.Create(rows, _dimension);
        if (matrix is null)
        {
            VectorsLoaded = true;
            return;
        }
        var steps = new IndexOptions();
        try
        {
            _db.Query("SELECT s.id, s.file, s.modality, s.vec, s.kind, s.loc, f.kind, " +
                            "CAST(json_extract(f.info, '$.pages') AS INTEGER) " +
                            "FROM segments s JOIN files f ON f.id = s.file " +
                            "WHERE s.vec IS NOT NULL ORDER BY s.id", r =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? blob = r.NullableBlob(3);
                if (blob is null || blob.Length != matrix.BytesPerRow || _rowFile.Count >= rows)
                    return;
                matrix.SetRow(_rowFile.Count, blob);
                _rowSegment.Add(r.GetInt64(0));
                _rowFile.Add(r.GetInt64(1));
                _rowModality.Add(ModalityStrings.Parse(r.NullableText(2)));
                // Past the first step of a long PDF (its pages) or document (its chunks).
                string kind = r.NullableText(4) ?? "";
                int place = r.IsDBNull(5) ? 0 : (int)r.GetDouble(5);
                string fileKind = r.NullableText(6) ?? "";
                int pages = r.IsDBNull(7) ? 0 : (int)r.GetInt64(7);
                bool later = fileKind == "pdf"
                    ? kind != SegmentKind.Chunk.ToStorageString() && pages > steps.BigPdfPages && place > steps.PdfStep
                    : kind == SegmentKind.Chunk.ToStorageString() && place > steps.DocStep;
                _rowSetsScale.Add(!later);
            });
        }
        catch
        {
            matrix.Dispose();
            throw;
        }
        _vectors = matrix;
        VectorsLoaded = true;
    }

    /// <summary>Frees the vectors' memory; keyword search keeps working.</summary>
    public void ReleaseVectors()
    {
        lock (_gate)
        {
            _vectors?.Dispose();
            _vectors = null;
            _rowFile.Clear();
            _rowSegment.Clear();
            _rowModality.Clear();
            _rowSetsScale.Clear();
            VectorsLoaded = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _db.Dispose();
            _vectors?.Dispose();
            _vectors = null;
            _rowFile.Clear();
            _rowSegment.Clear();
            _rowModality.Clear();
            _rowSetsScale.Clear();
            VectorsLoaded = false;
        }
    }

    private void ReloadFiles()
    {
        _fileVersions.Clear();
        _db.Query("SELECT " + VersionColumns + " FROM files", r => _fileVersions[r.GetInt64(0)] = ReadFileVersion(r));
        VectorSource = null;
        _dimension = 768;
        _db.Query("SELECT key, value FROM meta WHERE key IN ('vector_source','vector_dim')", r =>
        {
            if (r.GetString(0) == "vector_source") VectorSource = r.GetString(1);
            else if (int.TryParse(r.GetString(1), out int stored) && stored > 0) _dimension = stored;
        });
        _db.Query("SELECT length(vec) FROM segments WHERE vec IS NOT NULL LIMIT 1", r =>
        {
            int bytes = r.GetInt32(0);
            if (bytes > 0 && bytes % 2 == 0) _dimension = bytes / 2;
        });
    }

    public List<SearchHit> Search(
        string text,
        IReadOnlyList<float>? queryVector,
        HashSet<FileKind>? kinds = null,
        int limit = 20,
        double zGate = 2.0,
        double? wordWeight = null,
        bool prefixLast = false,
        CancellationToken cancellationToken = default)
    {
        // One search at a time per searcher: Reload/Dispose wait instead of
        // pulling the vectors out from under a running search.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return _db.Transaction(() => SearchLocked(text, queryVector, kinds, limit, zGate, wordWeight, prefixLast, cancellationToken));
        }
    }

    private List<SearchHit> SearchLocked(
        string text,
        IReadOnlyList<float>? queryVector,
        HashSet<FileKind>? kinds,
        int limit,
        double zGate,
        double? wordWeight,
        bool prefixLast,
        CancellationToken cancellationToken)
    {
        if (limit <= 0 || kinds is { Count: 0 }) return new();
        text = TextSanitize.Clean(text); // typed or pasted text can hold lone surrogates
        double ww = wordWeight ?? (IsCode ? CodeWordWeight : WordWeight);
        if (queryVector is not null && !VectorsLoaded)
            ReloadLocked(cancellationToken);
        // Capture one scope and one committed SQLite snapshot for the entire request.
        var scope = _store.ScopeSnapshot;
        var fileKind = new Dictionary<long, FileKind>();
        if (scope.Roots is { Count: 0 }) return new();
        // Keyword-only searches do not need an O(all files) metadata scan on every keystroke.
        if (queryVector is not null && _rowFile.Count > 0)
            _db.Query("SELECT " + VersionColumns + " FROM files WHERE 1" + scope.Sql(), scope.Values, r =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id = r.GetInt64(0);
                var current = ReadFileVersion(r);
                // Requeue/deletion can replace segment IDs while this vector snapshot is loaded.
                // Until Reload, changed files still participate through their current keyword rows.
                if (_fileVersions.TryGetValue(id, out var loaded) && loaded == current &&
                    (kinds is null || kinds.Contains(current.Kind))) fileKind[id] = current.Kind;
            });
        bool Allowed(long file) => fileKind.ContainsKey(file);

        // Meaning: score every vector on this query's scale, keep each file's best segment.
        var best = new Dictionary<long, (double Z, double Cosine, int Row)>();
        double semanticGate = zGate;
        bool smallSampleCalibration = false;
        int searchDim = _vectors?.Dimension ?? _dimension;
        if (queryVector is not null && queryVector.Count == searchDim && _rowFile.Count > 0)
        {
            float[] scores = Score(queryVector, cancellationToken);
            IReadOnlyList<bool> scale = _rowSetsScale;
            if (scope.Roots is not null || kinds is not null || fileKind.Count < _fileVersions.Count)
                scale = _rowSetsScale.Select((setsScale, row) => setsScale && Allowed(_rowFile[row])).ToArray();
            // With n centered samples, z cannot exceed sqrt(n - 1). A fixed gate of 2
            // therefore suppresses every semantic result in collections with 2-5 vectors.
            // Keep a conservative 90% of that attainable bound; large collections are unchanged.
            int samples = 0;
            for (int row = 0; row < scale.Count && samples < 6; row++)
                if (scale[row]) samples++;
            if (samples is >= 2 and < 6)
            {
                semanticGate = Math.Min(zGate, 0.9 * Math.Sqrt(samples - 1));
                smallSampleCalibration = semanticGate < zGate;
            }
            double[] meaning = VectorMath.Meaning(scores, _rowModality, scale);
            for (int row = 0; row < scores.Length; row++)
            {
                if ((row & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!Allowed(_rowFile[row]))
                    continue;
                double z = meaning[row];
                if (!best.TryGetValue(_rowFile[row], out var prev) || z > prev.Z)
                    best[_rowFile[row]] = (z, scores[row], row);
            }
        }

        // Words: FTS5 with Porter stemming over file names, folder names, OCR, page
        // and document text. A name from code is matched as one phrase, as written.
        List<string> terms = Terms(text);
        List<string>? identifier = IsCode ? CodeLookup.Identifier(text) : null;
        var words = new List<(long File, long Segment, string? Snippet)>();
        var coverage = new Dictionary<long, double>();
        List<string> patterns = identifier is not null
            ? new List<string> { CodeLookup.Phrase(identifier) }
            : Patterns(terms, prefixLast);
        if (patterns.Count > 0)
        {
            var seen = new HashSet<long>();
            var values = new List<SqlValue> { SqlValue.Text(string.Join(" OR ", patterns)) };
            values.AddRange(scope.Values);
            string kindFilter = "";
            if (kinds is not null)
            {
                kindFilter = " AND f.kind IN (" + string.Join(",", kinds.Select(_ => "?")) + ")";
                values.AddRange(kinds.Select(k => SqlValue.Text(k.ToStorageString())));
            }
            _db.Query("SELECT s.file, s.id, snippet(fts, 1, '«', '»', '…', 12), fts.original, f.kind FROM fts " +
                            "JOIN segments s ON s.id = fts.rowid JOIN files f ON f.id = s.file " +
                            "WHERE fts MATCH ?" + scope.Sql("f.path") + kindFilter +
                            " ORDER BY bm25(fts, 3.0, 1.0) LIMIT 1000", values, r =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long file = r.GetInt64(0);
                    if (words.Count >= 100 || !seen.Add(file))
                        return;
                    fileKind[file] = FileKindStrings.Parse(r.NullableText(4));
                    string? original = r.NullableText(3);
                    string? fallback = r.NullableText(2);
                    string? snippet = fallback;
                    if (original is not null)
                    {
                        // One poisoned row must not kill the search: fall back
                        // to SQLite's snippet when ours can't handle the text.
                        try { snippet = SearchText.Snippet(original, terms) ?? fallback; }
                        catch { snippet = fallback; }
                    }
                    words.Add((file, r.GetInt64(1), snippet));
                });
            if (words.Count > 0)
            {
                string files = string.Join(",", words.Select(w => w.File));
                foreach (string pattern in patterns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _db.Query("SELECT DISTINCT s.file FROM fts JOIN segments s ON s.id = fts.rowid " +
                                    "WHERE fts MATCH ? AND s.file IN (" + files + ")",
                        new[] { SqlValue.Text(pattern) }, r =>
                        {
                            coverage[r.GetInt64(0)] =
                                coverage.GetValueOrDefault(r.GetInt64(0)) + 1.0 / patterns.Count;
                        });
                }
            }
        }

        // A file qualifies by standing out in meaning, or by containing at least half
        // of the query's words, or appearing in top keyword search results.
        bool lookup = IsLookup(terms) || identifier is not null;
        double meaningWeight = lookup ? 0.5 : 1;
        double wordsWeight = lookup ? ww * 3 : ww * 2;
        var hinted = KindHints(text);
        var candidates = new HashSet<long>();
        bool QualifiesMeaning((double Z, double Cosine, int Row) value) =>
            value.Z >= semanticGate && (!smallSampleCalibration || value.Cosine > 0);
        foreach (var kv in best)
        {
            if (QualifiesMeaning(kv.Value))
                candidates.Add(kv.Key);
        }
        foreach (var kv in coverage)
        {
            if (kv.Value >= 0.5)
                candidates.Add(kv.Key);
        }
        foreach (var w in words)
        {
            candidates.Add(w.File);
        }

        var wordRank = new Dictionary<long, (int Rank, (long Segment, string? Snippet) Match)>();
        for (int i = 0; i < words.Count; i++)
        {
            if (!wordRank.ContainsKey(words[i].File))
                wordRank[words[i].File] = (i + 1, (words[i].Segment, words[i].Snippet));
        }

        string queryTrim = text.Trim();
        string queryStem = Path.HasExtension(queryTrim)
            ? Path.GetFileNameWithoutExtension(queryTrim)
            : queryTrim;
        string queryExt = Path.GetExtension(queryTrim);
        if (queryExt.StartsWith('.')) queryExt = queryExt[1..];

        var scored = new Dictionary<long, double>();
        foreach (long file in candidates)
        {
            double cov = coverage.GetValueOrDefault(file);
            double s = meaningWeight * Math.Max(best.TryGetValue(file, out var b) ? b.Z : 0, 0) +
                       wordsWeight * Math.Pow(cov, 1.5);
            if (fileKind.TryGetValue(file, out var k) && hinted.Contains(k))
                s += 1;

            // BM25 keyword rank boost for top results
            if (wordRank.TryGetValue(file, out var wr) && wr.Rank <= 10)
            {
                s += Math.Max(0.5, 3.0 - 0.25 * (wr.Rank - 1));
            }

            // Filename and path boosting
            if (_fileVersions.TryGetValue(file, out var ver))
            {
                string fName = Path.GetFileName(ver.Path);
                string fStem = Path.GetFileNameWithoutExtension(ver.Path);
                string fExt = Path.GetExtension(ver.Path);
                if (fExt.StartsWith('.')) fExt = fExt[1..];

                bool exactFile = string.Equals(fName, queryTrim, StringComparison.OrdinalIgnoreCase);
                bool exactStem = !string.IsNullOrEmpty(queryStem) && string.Equals(fStem, queryStem, StringComparison.OrdinalIgnoreCase);
                bool extMatch = !string.IsNullOrEmpty(queryExt) && string.Equals(fExt, queryExt, StringComparison.OrdinalIgnoreCase);

                if (exactFile)
                {
                    s += 12.0;
                }
                else if (exactStem)
                {
                    s += 8.0;
                    if (extMatch) s += 4.0;
                }
                else if (!string.IsNullOrEmpty(queryStem) && queryStem.Length >= 2 && fStem.StartsWith(queryStem, StringComparison.OrdinalIgnoreCase))
                {
                    s += 5.0;
                    if (extMatch) s += 2.0;
                }
                else if (!string.IsNullOrEmpty(queryStem) && queryStem.Length >= 2 && fName.Contains(queryStem, StringComparison.OrdinalIgnoreCase))
                {
                    s += 3.5;
                }
                else if (terms.Count > 0 && terms.All(t => fName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    s += 3.0;
                }
            }

            scored[file] = s;
        }

        var top = scored.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(limit).ToArray();
        var places = new Dictionary<long, (long Segment, double? Focus)>();
        var wantedSegments = new HashSet<long>();
        foreach (var kv in top)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasSemantic = best.TryGetValue(kv.Key, out var semantic);
            bool hasKeyword = wordRank.TryGetValue(kv.Key, out var keyword);
            bool useMeaning = hasSemantic && (QualifiesMeaning(semantic) || !hasKeyword);
            long segmentId;
            if (useMeaning) segmentId = _rowSegment[semantic.Row];
            else if (hasKeyword) segmentId = keyword.Match.Segment;
            else
            {
                long firstSeg = 0;
                _db.Query("SELECT id FROM segments WHERE file = ? ORDER BY id LIMIT 1", new[] { SqlValue.Int(kv.Key) }, r => firstSeg = r.GetInt64(0));
                if (firstSeg == 0) continue;
                segmentId = firstSeg;
            }
            (long Segment, double? Focus) place = (segmentId, null);
            if (IsCode)
                place = CodePlace(kv.Key, segmentId, places.Count < 10 ? identifier : null, text, terms,
                    hasKeyword && keyword.Match.Segment == segmentId || identifier is not null);
            places[kv.Key] = place;
            wantedSegments.Add(place.Segment);
            if (hasKeyword) wantedSegments.Add(keyword.Match.Segment);
        }
        var fileMetadata = new Dictionary<long, (string Path, FileKind Kind, double Modified, long Size, string? Json)>();
        var segments = new Dictionary<long, (SegmentKind Kind, double? Loc, double? End, string? Excerpt)>();
        foreach (var batch in top.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string ids = string.Join(",", batch.Select(kv => kv.Key));
            _db.Query("SELECT id, path, kind, mtime, size, info FROM files WHERE id IN (" + ids + ")", r =>
                fileMetadata[r.GetInt64(0)] = (r.GetString(1), FileKindStrings.Parse(r.NullableText(2)), r.GetDouble(3), r.GetInt64(4), r.NullableText(5)));
        }
        foreach (var batch in wantedSegments.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _db.Query("SELECT id, kind, loc, loc_end, excerpt FROM segments WHERE id IN (" + string.Join(",", batch) + ")", r =>
                segments[r.GetInt64(0)] = (SegmentKindStrings.Parse(r.NullableText(1)), r.IsDBNull(2) ? null : r.GetDouble(2),
                    r.IsDBNull(3) ? null : r.GetDouble(3), EncodingHelper.FixMojibake(r.NullableText(4))));
        }
        var hits = new List<SearchHit>();
        foreach (var kv in top)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long file = kv.Key;
            double score = kv.Value;
            bool hasSemantic = best.TryGetValue(file, out var semantic);
            bool hasKeyword = wordRank.TryGetValue(file, out var keyword);
            if (!places.TryGetValue(file, out var place)) continue;
            long segmentId = place.Segment;
            double? focus = place.Focus;
            if (!fileMetadata.TryGetValue(file, out var metadata) || !segments.TryGetValue(segmentId, out var segmentMetadata)) continue;
            string path = metadata.Path;
            FileKind kind = metadata.Kind;
            double modified = metadata.Modified;
            long size = metadata.Size;
            var info = new Dictionary<string, string>();
            if (metadata.Json is not null)
            {
                try { info = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadata.Json) ?? new(); }
                catch (System.Text.Json.JsonException) { }
            }
            SegmentKind segment = segmentMetadata.Kind;
            double? loc = segmentMetadata.Loc, locEnd = segmentMetadata.End;
            string? storedExcerpt = segmentMetadata.Excerpt;
            // Quote the matched words only when they come from the segment we point at.
            // A segment with no place and no text of its own (a screenshot's image
            // vector) can borrow them from its file's OCR text.
            string? snippet = null;
            if (hasKeyword && keyword.Match.Segment == segmentId && keyword.Match.Snippet is not null &&
                keyword.Match.Snippet.Contains('«'))
                snippet = EncodingHelper.FixMojibake(Excerpt(keyword.Match.Snippet, 400));
            if (snippet is null && storedExcerpt is null && loc is null && hasKeyword &&
                keyword.Match.Snippet is not null && keyword.Match.Snippet.Contains('«'))
            {
                if (segments.TryGetValue(keyword.Match.Segment, out var matched) && matched.Kind != SegmentKind.Name)
                    snippet = EncodingHelper.FixMojibake(Excerpt(keyword.Match.Snippet, 400));
            }
            var hit = new SearchHit(path, kind, score,
                hasSemantic ? semantic.Z : null, hasSemantic ? semantic.Cosine : null,
                hasKeyword ? keyword.Rank : null, segment, segmentId, loc, locEnd,
                snippet ?? storedExcerpt, modified, size, info)
            {
                Focus = focus,
            };
            hits.Add(hit);
        }
        return hits;
    }

    /// <summary>Code: the stretch to point at, and the line in it to open at.</summary>
    private (long Segment, double? Focus) CodePlace(
        long file, long segment, List<string>? identifier, string query,
        List<string> terms, bool wordsHere)
    {
        string name = query.Trim().Replace("()", "", StringComparison.Ordinal);
        if (identifier is not null)
        {
            (long Segment, int Line, int Strength)? foundBest = null;
            _db.Query("SELECT s.id, s.loc, coalesce(fts.original, fts.body) FROM fts " +
                            "JOIN segments s ON s.id = fts.rowid " +
                            "WHERE fts MATCH ? AND s.file = ? ORDER BY s.loc LIMIT 300",
                new[] { SqlValue.Text(CodeLookup.Phrase(identifier)), SqlValue.Int(file) }, r =>
                {
                    if ((foundBest?.Strength ?? 0) >= 2)
                        return;
                    string? text = r.NullableText(2);
                    if (text is null)
                        return;
                    // Name rows have no place (loc is NULL): definitions live in line stretches.
                    if (r.IsDBNull(1))
                        return;
                    var found = CodeLookup.DefinitionOf(name, text, (int)r.GetDouble(1));
                    if (found is not null && found.Value.Strength > (foundBest?.Strength ?? 0))
                        foundBest = (r.GetInt64(0), found.Value.Line, found.Value.Strength);
                });
            if (foundBest is not null)
                return (foundBest.Value.Segment, foundBest.Value.Line);
        }
        if ((!wordsHere && identifier is null) || TextOfSegment(segment) is not string text)
            return (segment, null);
        int first = 1; // name rows have no place: point at the file's first line
        _db.Query("SELECT loc FROM segments WHERE id = ?",
            new[] { SqlValue.Int(segment) }, r => { if (!r.IsDBNull(0)) first = (int)r.GetDouble(0); });
        var words = identifier is null ? terms : new List<string> { name.ToLowerInvariant() };
        int? line = CodeLookup.FirstLineWith(words, text, first);
        return (segment, line is null ? null : (double?)line.Value);
    }

    /// <summary>Codes and IDs ("K7Q2LM", "TRX50") and long numbers mean nothing to an
    /// embedding but everything to keyword search: a query made of them weighs words
    /// double and meaning half.</summary>
    public static bool IsLookup(List<string> terms) =>
        terms.Count > 0 && terms.All(t =>
            t.Any(char.IsDigit) && (t.Any(char.IsLetter) || t.Length >= 4));

    /// <summary>How many of <paramref name="hits"/> (best first) are strong: scoring at
    /// least <paramref name="ratio"/> × the top hit.</summary>
    public static int StrongCount(List<SearchHit> hits, double ratio = 0.5)
    {
        if (hits.Count == 0 || hits[0].Score <= 0)
            return hits.Count;
        double top = hits[0].Score;
        int n = 0;
        while (n < hits.Count && hits[n].Score >= top * ratio) n++;
        return n;
    }

    /// <summary>Cosine similarity of <paramref name="query"/> with every stored vector
    /// (all are L2-normalized): a matrix-vector product, done a block of rows at a time,
    /// each block turned into float32 first. Accelerated by SIMD via <see cref="Vectors.DotProduct"/>.</summary>
    private float[] Score(IReadOnlyList<float> query, CancellationToken cancellationToken)
    {
        int rows = _rowFile.Count;
        var scores = new float[rows];
        if (_vectors is null || rows == 0)
            return scores;
        int dim = _vectors.Dimension;
        float[] q = query is float[] arr ? arr : query.ToArray();
        ReadOnlySpan<float> qSpan = q.AsSpan(0, Math.Min(q.Length, dim));
        // Half -> float conversion dominates large scans. Schedule partitions once per query,
        // rather than creating a Parallel.For for every 1,024 rows. Leave cores for indexing/UI.
        int workers = Math.Min(8, Environment.ProcessorCount / 2);
        if (workers > 1 && (long)rows * dim >= 16_000_000)
        {
            var vectors = _vectors;
            int blocks = (rows + VectorStore.BlockRows - 1) / VectorStore.BlockRows;
            Parallel.For(0, blocks, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = workers },
                () => (Half: ArrayPool<Half>.Shared.Rent(VectorStore.BlockRows * dim),
                       Float: ArrayPool<float>.Shared.Rent(VectorStore.BlockRows * dim)),
                (blockIndex, _, buffers) =>
                {
                    int first = blockIndex * VectorStore.BlockRows;
                    int count = Math.Min(VectorStore.BlockRows, rows - first);
                    vectors.CopyBlockAsFloat(first, count, buffers.Float, buffers.Half);
                    for (int r = 0; r < count; r++)
                        scores[first + r] = Vectors.DotProduct(buffers.Float.AsSpan(r * dim, dim), q.AsSpan(0, dim));
                    return buffers;
                },
                buffers => { ArrayPool<Half>.Shared.Return(buffers.Half); ArrayPool<float>.Shared.Return(buffers.Float); });
            return scores;
        }
        var block = ArrayPool<float>.Shared.Rent(VectorStore.BlockRows * dim);
        try
        {
            for (int row = 0; row < rows; row += VectorStore.BlockRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(VectorStore.BlockRows, rows - row);
                _vectors.CopyBlockAsFloat(row, count, block);
                for (int r = 0; r < count; r++)
                    scores[row + r] = Vectors.DotProduct(block.AsSpan(r * dim, dim), qSpan);
            }
        }
        finally { ArrayPool<float>.Shared.Return(block); }
        return scores;
    }

    /// <summary>The text of a page, a document's passage or a screenshot's OCR, as it's
    /// written: the preview shows the part of a document that matched.</summary>
    public string? TextOfSegment(long id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? text = null;
            _db.Query("SELECT coalesce(original, body) FROM fts WHERE rowid = ?",
                new[] { SqlValue.Int(id) }, r => { text = r.NullableText(0); });
            return text;
        }
    }

    public static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "in", "on", "at", "to", "for", "with", "and", "or", "my", "me",
        "that", "this", "these", "those", "from", "about", "where", "what", "which", "who",
        "when", "is", "was", "are", "were", "be", "it", "its", "some", "any", "find", "show",
        "file", "files", "photo", "photos", "picture", "image", "images",
        "screenshot", "screenshots", "video", "videos", "pdf", "document", "documents", "recording",
        // Common file extensions treated as kind/format stopwords
        "jpg", "jpeg", "png", "gif", "webp", "tif", "tiff", "bmp", "svg",
        "docx", "doc", "xlsx", "xls", "pptx", "ppt", "txt", "rtf",
        "mp4", "mkv", "mov", "avi", "webm", "mp3", "wav", "flac", "m4a", "ogg", "aac",
        // Bengali and Arabic (normalized, as Terms sees them)
        "এবং", "ও", "এই", "সেই", "একটি", "আমার", "থেকে", "জন্য", "সাথে", "মধ্যে",
        "কোথায়", "কোন", "কী", "কি", "যে", "ছবি", "স্ক্রিনশট", "ভিডিও", "ফাইল", "পিডিএফ",
        "في", "من", "على", "الى", "عن", "مع", "هذا", "هذه", "ذلك", "التي", "الذي", "اين", "ما",
        "صورة", "صور", "لقطة", "شاشة", "فيديو", "ملف", "مستند",
        // Russian stopwords and kind words
        "и", "в", "во", "не", "что", "он", "на", "я", "с", "со", "как", "а", "то", "все", "она",
        "так", "его", "но", "да", "ты", "к", "у", "же", "вы", "за", "бы", "по", "только", "ее",
        "мне", "было", "вот", "от", "меня", "еще", "нет", "о", "об", "из", "ему", "теперь",
        "когда", "даже", "ну", "ли", "если", "уже", "или", "ни", "быть", "был", "него", "до",
        "вас", "опять", "вам", "ведь", "там", "потом", "себя", "ей", "может", "они", "тут",
        "где", "есть", "надо", "ней", "для", "мы", "тебя", "их", "чем", "была", "сам", "чтоб",
        "без", "тоже", "себе", "под", "будет", "тогда", "кто", "этот", "того", "потому", "этого",
        "какой", "совсем", "здесь", "этом", "один", "почти", "мой", "тем", "чтобы", "нее", "сейчас",
        "были", "куда", "зачем", "всех", "никогда", "можно", "при", "наконец", "другой", "после",
        "над", "больше", "тот", "через", "эти", "нас", "про", "всего", "них", "какая", "много",
        "эту", "моя", "хорошо", "свою", "этой", "перед", "лучше", "том", "нельзя", "такой", "им",
        "более", "всегда", "всю", "между", "найти", "покажи", "показать", "файл", "файлы", "фото",
        "фотография", "фотографии", "картинка", "картинки", "скриншот", "скриншоты", "видео",
        "документ", "документы", "пдф", "запись"
    };

    /// <summary>The content words of a query, normalized as keyword search keeps its text.
    /// Kind words and stopwords are dropped. Words of two letters or more count.</summary>
    public static List<string> Terms(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var token in SearchText.Normalized(text).ToLowerInvariant()
                     .Split(Array.Empty<char>()))
        {
            // Split on anything that isn't a letter or digit (mirrors Swift's
            // components(separatedBy: alphanumerics.inverted)).
            foreach (var piece in SplitWords(token))
            {
                if (piece.EnumerateRunes().Count() >= 2 &&
                    !Stopwords.Contains(piece) && seen.Add(piece))
                    output.Add(piece);
            }
        }
        // Fallback: if all words were filtered as stopwords/extensions (e.g. searching directly for "jpg" or "video"),
        // keep the original pieces so the user can still search by them.
        if (output.Count == 0)
        {
            foreach (var token in SearchText.Normalized(text).ToLowerInvariant().Split(Array.Empty<char>()))
            {
                foreach (var piece in SplitWords(token))
                {
                    if (piece.EnumerateRunes().Count() >= 2 && seen.Add(piece))
                        output.Add(piece);
                }
            }
        }
        return output;
    }

    private static IEnumerable<string> SplitWords(string token)
    {
        int start = -1;
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsLetterOrDigit(token[i]))
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                yield return token.Substring(start, i - start);
                start = -1;
            }
        }
        if (start >= 0)
            yield return token.Substring(start);
    }

    /// <summary>FTS5 patterns for the query's words: quoted, and as prefixes for the last
    /// word while it's being typed and for words of languages that attach endings.</summary>
    public static List<string> Patterns(List<string> terms, bool prefixLast)
    {
        var output = new List<string>(terms.Count);
        for (int i = 0; i < terms.Count; i++)
        {
            bool prefix = (prefixLast && i == terms.Count - 1) || SearchText.Inflects(terms[i]);
            var forms = SearchText.RussianForms(terms[i]);
            output.Add(forms.Count > 1 && !prefix
                ? "(" + string.Join(" OR ", forms.Select(f => "\"" + f + "\"")) + ")"
                : "\"" + terms[i] + "\"" + (prefix ? "*" : ""));
        }
        return output;
    }

    /// <summary>The query's words as an FTS5 OR-query.</summary>
    public static string? FtsQuery(string text, bool prefixLast = false)
    {
        var words = Terms(text);
        return words.Count == 0 ? null : string.Join(" OR ", Patterns(words, prefixLast));
    }

    public static readonly Dictionary<string, FileKind> KindWords = new(StringComparer.Ordinal)
    {
        ["screenshot"] = FileKind.Screenshot,
        ["screenshots"] = FileKind.Screenshot,
        ["photo"] = FileKind.Image,
        ["photos"] = FileKind.Image,
        ["picture"] = FileKind.Image,
        ["pictures"] = FileKind.Image,
        ["image"] = FileKind.Image,
        ["images"] = FileKind.Image,
        ["pdf"] = FileKind.Pdf,
        ["pdfs"] = FileKind.Pdf,
        ["paper"] = FileKind.Pdf,
        ["papers"] = FileKind.Pdf,
        ["document"] = FileKind.Doc,
        ["documents"] = FileKind.Doc,
        ["note"] = FileKind.Doc,
        ["notes"] = FileKind.Doc,
        ["video"] = FileKind.Video,
        ["videos"] = FileKind.Video,
        ["clip"] = FileKind.Video,
        ["movie"] = FileKind.Video,
        ["audio"] = FileKind.Audio,
        ["song"] = FileKind.Audio,
        ["podcast"] = FileKind.Audio,
        ["voice"] = FileKind.Audio,
        ["sound"] = FileKind.Audio,
        // Common file extensions
        ["jpg"] = FileKind.Image,
        ["jpeg"] = FileKind.Image,
        ["png"] = FileKind.Image,
        ["gif"] = FileKind.Image,
        ["webp"] = FileKind.Image,
        ["tif"] = FileKind.Image,
        ["tiff"] = FileKind.Image,
        ["bmp"] = FileKind.Image,
        ["svg"] = FileKind.Image,
        ["docx"] = FileKind.Doc,
        ["doc"] = FileKind.Doc,
        ["xlsx"] = FileKind.Doc,
        ["xls"] = FileKind.Doc,
        ["pptx"] = FileKind.Doc,
        ["ppt"] = FileKind.Doc,
        ["txt"] = FileKind.Doc,
        ["rtf"] = FileKind.Doc,
        ["mp4"] = FileKind.Video,
        ["mkv"] = FileKind.Video,
        ["mov"] = FileKind.Video,
        ["avi"] = FileKind.Video,
        ["webm"] = FileKind.Video,
        ["mp3"] = FileKind.Audio,
        ["wav"] = FileKind.Audio,
        ["flac"] = FileKind.Audio,
        ["m4a"] = FileKind.Audio,
        ["ogg"] = FileKind.Audio,
        ["aac"] = FileKind.Audio,
        // Russian kind words
        ["картинка"] = FileKind.Image,
        ["картинки"] = FileKind.Image,
        ["фото"] = FileKind.Image,
        ["фотография"] = FileKind.Image,
        ["фотографии"] = FileKind.Image,
        ["скриншот"] = FileKind.Screenshot,
        ["скриншоты"] = FileKind.Screenshot,
        ["скрин"] = FileKind.Screenshot,
        ["скрины"] = FileKind.Screenshot,
        ["видео"] = FileKind.Video,
        ["клип"] = FileKind.Video,
        ["фильм"] = FileKind.Video,
        ["аудио"] = FileKind.Audio,
        ["песня"] = FileKind.Audio,
        ["музыка"] = FileKind.Audio,
        ["трек"] = FileKind.Audio,
        ["документ"] = FileKind.Doc,
        ["документы"] = FileKind.Doc,
        ["текст"] = FileKind.Doc,
        ["пдф"] = FileKind.Pdf,
        // Bengali and Arabic (normalized, as Terms sees them)
        ["স্ক্রিনশট"] = FileKind.Screenshot,
        ["ছবি"] = FileKind.Image,
        ["পিডিএফ"] = FileKind.Pdf,
        ["নোট"] = FileKind.Doc,
        ["ভিডিও"] = FileKind.Video,
        ["অডিও"] = FileKind.Audio,
        ["গান"] = FileKind.Audio,
        ["রেকর্ডিং"] = FileKind.Audio,
        ["لقطة"] = FileKind.Screenshot,
        ["صورة"] = FileKind.Image,
        ["صور"] = FileKind.Image,
        ["مستند"] = FileKind.Doc,
        ["ملاحظات"] = FileKind.Doc,
        ["فيديو"] = FileKind.Video,
        ["مقطع"] = FileKind.Video,
        ["صوت"] = FileKind.Audio,
        ["تسجيل"] = FileKind.Audio,
        ["اغنية"] = FileKind.Audio,
    };

    /// <summary>Kinds the query asks for ("…in a video", "screenshot of…"): a soft boost,
    /// not a filter.</summary>
    public static HashSet<FileKind> KindHints(string text)
    {
        var hints = new HashSet<FileKind>();
        foreach (var word in SearchText.Normalized(text).ToLowerInvariant()
                     .Split(Array.Empty<char>()))
        {
            foreach (var piece in SplitWords(word))
            {
                if (KindWords.TryGetValue(piece, out var kind))
                {
                    hints.Add(kind);
                    continue;
                }
                foreach (var kv in KindWords)
                {
                    if (SearchText.Inflects(kv.Key) &&
                        kv.Key.EnumerateRunes().Count() >= 3 &&
                        piece.StartsWith(kv.Key, StringComparison.Ordinal))
                    {
                        hints.Add(kv.Value);
                        break;
                    }
                }
            }
        }
        return hints;
    }

    /// <summary>One line, at most <paramref name="limit"/> characters: for result lists.</summary>
    internal static string Excerpt(string text, int limit = 240)
    {
        string line = Regex.Replace(TextSanitize.Clean(text), @"\s+", " ").Trim();
        return line.EnumerateRunes().Count() > limit
            ? string.Concat(line.EnumerateRunes().Take(limit)) + "…"
            : line;
    }
}

/// <summary>Names in code: a query that's a name finds it exactly, and the place that
/// defines it rather than every place that uses it. Ports CodeLookup.</summary>
public static class CodeLookup
{
    private static readonly Regex IdentifierChars =
        new(@"^[A-Za-z_$][A-Za-z0-9_$]*(?:(?:\.|::|->|#)[A-Za-z_$][A-Za-z0-9_$]*)*$",
            RegexOptions.Compiled);

    /// <summary>The words keyword search keeps a name from code as, when the query is
    /// one: snake_case, camelCase, dotted, or with digits. Null for words anyone would
    /// say ("retry", "Renamer").</summary>
    public static List<string>? Identifier(string query)
    {
        string name = query.Trim();
        if (name.EndsWith("()", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - 2);
        if (!IdentifierChars.IsMatch(name))
            return null;
        bool hasLower = name.Any(char.IsLower);
        bool camel = name.Skip(1).Any(char.IsUpper) && hasLower;
        if (!camel && !name.Any(c => "_.:#$".Contains(c) || char.IsDigit(c)))
            return null;
        return name.ToLowerInvariant().Split(Array.Empty<char>())
            .SelectMany(SplitName).Where(w => w.Length > 0).ToList();
    }

    private static IEnumerable<string> SplitName(string token)
    {
        int start = -1;
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsLetterOrDigit(token[i]))
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                yield return token.Substring(start, i - start);
                start = -1;
            }
        }
        if (start >= 0)
            yield return token.Substring(start);
    }

    /// <summary>The keyword search pattern for an identifier's words: one phrase, so they
    /// match only side by side, as written.</summary>
    public static string Phrase(List<string> words) => "\"" + string.Join(" ", words) + "\"";

    /// <summary>Where <paramref name="name"/> is defined in <paramref name="text"/> (a
    /// stretch whose first line is <paramref name="first"/>): the line, and how surely.
    /// Null when the stretch only uses it.</summary>
    public static (int Line, int Strength)? DefinitionOf(string name, string text, int first)
    {
        (int Line, int Strength)? best = null;
        var lines = Lines(text);
        for (int offset = 0; offset < lines.Count; offset++)
        {
            foreach (var range in Occurrences(name, lines[offset]))
            {
                string before = lines[offset].Substring(0, range.Start).Trim();
                string after = lines[offset].Substring(range.Start + range.Length).Trim();
                int strength = DefinitionStrength(before, after, lines[offset]);
                if (strength > (best?.Strength ?? 0))
                    best = (first + offset, strength);
                if (strength == 2)
                    return best;
            }
        }
        return best;
    }

    public static bool DefinesSomething(string word) =>
        Definers.Contains(word) && word != "let" && word != "var" && word != "const" && word != "val";

    private static readonly HashSet<string> Definers = new(StringComparer.Ordinal)
    {
        "def", "func", "fn", "fun", "function", "class", "struct", "enum", "protocol",
        "interface", "trait", "type", "typealias", "actor", "extension", "impl", "module",
        "namespace", "object", "record", "macro", "const", "let", "var", "val", "union",
        "mod", "sub", "@interface", "@implementation", "@protocol", "#define", "function*",
    };

    private static readonly HashSet<string> Users = new(StringComparer.Ordinal)
    {
        "return", "await", "yield", "throw", "new", "else", "case", "print", "echo", "assert",
        "not", "and", "or", "in", "is", "do", "go", "defer", "try", "catch", "if", "while",
        "for", "typeof", "delete", "raise", "import", "from",
    };

    private static int DefinitionStrength(string before, string after, string line)
    {
        string previous = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? "";
        if (Definers.Contains(previous))
            return 2;
        if (before.StartsWith("func (", StringComparison.Ordinal) && before.EndsWith(")", StringComparison.Ordinal))
            return 2; // a Go method
        if (before.StartsWith("- (", StringComparison.Ordinal) || before.StartsWith("+ (", StringComparison.Ordinal))
            return after.StartsWith(":", StringComparison.Ordinal) || after.Length == 0 ||
                   after.StartsWith("{", StringComparison.Ordinal) || after.StartsWith(";", StringComparison.Ordinal)
                ? 2 : 0; // an Objective-C method
        // C-style: "static void name(…) {", not a call or an assignment.
        if (!after.StartsWith("(", StringComparison.Ordinal) || before.Length == 0 ||
            Users.Contains(previous) || before.Any(c => "=(,.!?".Contains(c)) ||
            before.Contains("->", StringComparison.Ordinal))
            return 0;
        char last = before[^1];
        if (!(char.IsLetter(last) || char.IsDigit(last) || "*&>_]".Contains(last)))
            return 0;
        return line.Trim().EndsWith(";", StringComparison.Ordinal) ? 1 : 2;
    }

    /// <summary>The line (from <paramref name="first"/>) to open at for
    /// <paramref name="words"/>. Null when none is there.</summary>
    public static int? FirstLineWith(List<string> words, string text, int first)
    {
        var wanted = words.Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()).ToList();
        if (wanted.Count == 0)
            return null;
        int? firstCode = null, firstAny = null;
        var lines = Lines(text);
        for (int offset = 0; offset < lines.Count; offset++)
        {
            if (!wanted.Any(w => Has(w, lines[offset])))
                continue;
            string trimmed = lines[offset].Trim();
            if (trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Take(4).Any(w => DefinesSomething(w)))
                return first + offset;
            if (firstCode is null && !CommentStarts.Any(trimmed.StartsWith))
                firstCode = offset;
            firstAny ??= offset;
        }
        int? found = firstCode ?? firstAny;
        return found is null ? null : first + found.Value;
    }

    private static readonly string[] CommentStarts =
        { "//", "#", "/*", "*", "--", "\"\"\"", "'''", "<!--", ";" };

    private static bool Has(string word, string line)
    {
        string lower = line.ToLowerInvariant();
        int searchStart = 0;
        while (true)
        {
            int found = lower.IndexOf(word, searchStart, StringComparison.Ordinal);
            if (found < 0)
                return false;
            if (found == 0 ||
                (!char.IsLetter(lower[found - 1]) && !char.IsDigit(lower[found - 1])) ||
                (found < line.Length && char.IsUpper(line[found])))
                return true;
            searchStart = found + word.Length;
        }
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').ToList();

    private static List<(int Start, int Length)> Occurrences(string name, string line)
    {
        var found = new List<(int, int)>();
        int start = 0;
        while (start <= line.Length)
        {
            int at = line.IndexOf(name, start, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                break;
            bool clearBefore = at == 0 || !IsNameCharacter(line[at - 1]);
            bool clearAfter = at + name.Length >= line.Length || !IsNameCharacter(line[at + name.Length]);
            if (clearBefore && clearAfter)
                found.Add((at, name.Length));
            start = at + name.Length;
        }
        return found;
    }

    private static bool IsNameCharacter(char c) =>
        char.IsLetter(c) || char.IsDigit(c) || c == '_' || c == '$';
}
