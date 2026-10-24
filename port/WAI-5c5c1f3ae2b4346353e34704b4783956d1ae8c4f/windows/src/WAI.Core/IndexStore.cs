using System.Text.Json;

namespace WAI.Core;

// ---------- Model helpers (enums live in Pieces.cs) ----------

/// <summary>FileKind parsing with a fallback for unknown stored values
/// (FileKindExtensions.ParseFileKind throws; the store must tolerate them).</summary>
public static class FileKindStrings
{
    public static FileKind Parse(string? raw) => raw switch
    {
        "screenshot" => FileKind.Screenshot,
        "image" => FileKind.Image,
        "pdf" => FileKind.Pdf,
        "doc" => FileKind.Doc,
        "audio" => FileKind.Audio,
        "video" => FileKind.Video,
        "code" => FileKind.Code,
        _ => FileKind.Doc,
    };
}

/// <summary>SegmentKind parsing with a fallback for unknown stored values.</summary>
public static class SegmentKindStrings
{
    public static SegmentKind Parse(string? raw) => raw switch
    {
        "name" => SegmentKind.Name,
        "image" => SegmentKind.Image,
        "ocr" => SegmentKind.Ocr,
        "page" => SegmentKind.Page,
        "page_image" => SegmentKind.PageImage,
        "chunk" => SegmentKind.Chunk,
        "audio" => SegmentKind.Audio,
        "frame" => SegmentKind.Frame,
        "lines" => SegmentKind.Lines,
        "cells" => SegmentKind.Cells,
        _ => SegmentKind.Chunk,
    };
}

/// <summary>Modality storage strings (Modality itself lives in Pieces.cs).</summary>
public static class ModalityStrings
{
    public static string Raw(Modality modality) => modality switch
    {
        Modality.None => "none",
        Modality.Text => "text",
        Modality.Image => "image",
        Modality.Audio => "audio",
        _ => "none",
    };

    public static Modality Parse(string? raw) => raw switch
    {
        "none" => Modality.None,
        "text" => Modality.Text,
        "image" => Modality.Image,
        "audio" => Modality.Audio,
        _ => Modality.None,
    };

    public static readonly Modality[] All = { Modality.None, Modality.Text, Modality.Image, Modality.Audio };
}

/// <summary>A file as the index knows it. Canonical definition (also used by the
/// extractor pipeline via IExtractor.cs); Device/Inode are Windows-stable file IDs.</summary>
public sealed record FileRecord(long Id, string Path, FileKind Kind, long Size, double Mtime,
    long Device = 0, long Inode = 0, string State = "pending");

/// <summary>One stored segment row (the <c>segments</c> table).</summary>
public sealed record Segment(long Id, long FileId, SegmentKind Kind, Modality Modality,
    double? Loc, double? LocEnd, string? Excerpt, bool HasVector);

/// <summary>One indexable piece of a file: the unit stored as a <c>segments</c> row
/// plus an FTS5 row (keyword text) and/or a vector.</summary>
public sealed record Piece(long FileId, SegmentKind Kind, Modality Modality,
    double? Loc = null, double? LocEnd = null, string? Excerpt = null,
    string? Text = null, float[]? Vector = null);

/// <summary>Which vectors an index holds. Mirrors LlamaEmbedder.vectorVersion: it changes
/// only when the vectors do, so a runtime update that leaves the vectors alone keeps the index.</summary>
public static class ModelVectors
{
    public const int VectorVersion = 1;
    public const string VectorRuntime = "llama.cpp-vectors1";

    /// <summary>The full index fingerprint: the embedding source plus the index options.</summary>
    public static string Fingerprint(string vectorSource, IndexOptions options) =>
        $"{vectorSource} | {options.Fingerprint}";
}

/// <summary>"code: retry with backoff": a search of the code index, and nothing else.</summary>
public static class CodeQuery
{
    public const string Prefix = "code:";

    /// <summary>What's typed after "code:" ("" for "code:" alone), or null for a search that isn't one of code.</summary>
    public static string? TextOf(string query)
    {
        string trimmed = query.TrimStart();
        if (trimmed.Length >= Prefix.Length &&
            trimmed.Substring(0, Prefix.Length).Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            return trimmed.Substring(Prefix.Length).Trim();
        return null;
    }
}

/// <summary>Index statistics.</summary>
public sealed class Counts
{
    public Dictionary<string, Dictionary<string, int>> ByKindAndState { get; } = new();
    public int Vectors { get; set; }
    public int Segments { get; set; }
    /// <summary>Long files still being read, and their pages and chunks left.</summary>
    public int Unfinished { get; set; }
    public int Left { get; set; }
}

public sealed record FolderVectorStats(
    string Folder,
    int FileCount,
    int SegmentCount,
    int VectorCount,
    long VectorBytesTotal
)
{
    public int Files => FileCount;
    public int Segments => SegmentCount;
    public int Vectors => VectorCount;
    public long Bytes => VectorBytesTotal;
    public double VectorMegabytes => Math.Round(VectorBytesTotal / (1024.0 * 1024.0), 2);
}

/// <summary>The on-disk index: one SQLite file with files, their segments (with fp16
/// vectors), and an FTS5 table. Ports IndexStore.swift with the same schema.
/// Implements IIndexerStore (declared in Indexer.cs) so the indexer can use it.</summary>
public sealed class IndexStore : IDisposable, IIndexerStore
{
    public const long SchemaVersion = 5;

    /// <summary>Keyword search's tokenizer. Marks (M*) count as letters, so Bengali vowel
    /// signs and Arabic harakat stay inside their words.</summary>
    public const string Tokenizer = "porter unicode61 remove_diacritics 2 categories 'L* N* Co M*'";
    /// <summary>Keyword search's table: words are matched in <c>body</c>, normalized
    /// (<see cref="SearchText"/>); <c>original</c> keeps the text as written when
    /// normalizing changed it, for showing it.</summary>
    public const string FtsColumns = "name, body, original UNINDEXED, tokenize = \"" + Tokenizer + "\"";

    /// <summary>The main index's file in the index folder.</summary>
    public const string MainFile = "index.sqlite";
    /// <summary>The code index is a file of its own beside it.</summary>
    public const string CodeFile = "code.sqlite";

    private const string FileColumns = "id, path, kind, size, mtime, inode, device, state";

    private readonly Database _db;
    private bool _disposed;

    public string Directory { get; }
    public string FilePath { get; }
    public Database Db => _db;

    private PathScope _scopeFilter = new(null);

    /// <summary>Null includes all files; an empty list includes none. Values are copied.</summary>
    public List<string>? Scope
    {
        get => Volatile.Read(ref _scopeFilter).Roots?.ToList();
        set => Volatile.Write(ref _scopeFilter, new PathScope(value));
    }

    internal PathScope ScopeSnapshot => Volatile.Read(ref _scopeFilter);
    public bool IsCodeIndex { get; }

    /// <summary>Dimension belongs to this index, never to a concurrently loaded model.</summary>
    public int VectorDimension => StoredVectorDimension() ?? 768;

    private int? StoredVectorDimension()
    {
        if (int.TryParse(Meta("vector_dim"), out int dimension) && dimension > 0)
            return dimension;
        int? detected = null;
        _db.Query("SELECT length(vec) FROM segments WHERE vec IS NOT NULL LIMIT 1", r =>
        {
            int bytes = r.GetInt32(0);
            if (bytes > 0 && bytes % 2 == 0) detected = bytes / 2;
        });
        return detected;
    }

    public IndexStore(string directory, string fileName = MainFile, bool? isCodeIndex = null)
    {
        System.IO.Directory.CreateDirectory(directory);
        Directory = directory;
        FilePath = System.IO.Path.Combine(directory, fileName);
        _db = new Database(FilePath);
        Migrate();
        IsCodeIndex = isCodeIndex ?? (System.IO.Path.GetFileNameWithoutExtension(fileName).Equals("code", StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetFileNameWithoutExtension(fileName).StartsWith("code_", StringComparison.OrdinalIgnoreCase));
        _db.Execute("CREATE INDEX IF NOT EXISTS files_path_windows ON files(path COLLATE WAI_PATH)");
    }

    /// <summary>Bytes on disk: the database and its write-ahead log.</summary>
    public static long SizeOf(string file)
    {
        long total = 0;
        foreach (var path in new[] { file, file + "-wal" })
        {
            try
            {
                if (System.IO.File.Exists(path))
                    total += new System.IO.FileInfo(path).Length;
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    /// <summary>Paths are stored with '/' separators so prefix-range queries behave
    /// the same on Windows ('\') as on macOS.</summary>
    public static string NormalizePath(string path) => path.Replace('\\', '/');

    public static bool IsInside(string path, string folder)
    {
        string p = NormalizePath(path);
        string f = NormalizePath(folder);
        if (!f.EndsWith('/'))
            f += "/";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes indexed files, segments, and embedding vector disk sizes for a given folder.
    /// </summary>
    public FolderVectorStats GetFolderStats(string folder)
    {
        string prefix = NormalizePath(folder);
        if (!prefix.EndsWith('/'))
            prefix += "/";

        int files = 0;
        int segments = 0;
        int vectors = 0;
        long bytes = 0;

        try
        {
            _db.Query(
                "SELECT COUNT(DISTINCT f.id), COUNT(s.id), COUNT(s.vec), COALESCE(SUM(LENGTH(s.vec)), 0) " +
                "FROM files f " +
                "LEFT JOIN segments s ON s.file = f.id " +
                "WHERE f.path COLLATE WAI_PATH >= ? AND f.path COLLATE WAI_PATH < ?",
                [SqlValue.Text(prefix), SqlValue.Text(prefix[..^1] + "0")],
                r =>
                {
                    files = (int)r.GetInt64(0);
                    segments = (int)r.GetInt64(1);
                    vectors = (int)r.GetInt64(2);
                    bytes = r.GetInt64(3);
                });
        }
        catch { }

        return new FolderVectorStats(folder, files, segments, vectors, bytes);
    }

    /// <summary>
    /// Computes overall indexed files, segments, and embedding vector disk sizes across all folders.
    /// </summary>
    public FolderVectorStats GetTotalStats()
    {
        int files = 0;
        int segments = 0;
        int vectors = 0;
        long bytes = 0;

        try
        {
            _db.Query(
                "SELECT COUNT(DISTINCT f.id), COUNT(s.id), COUNT(s.vec), COALESCE(SUM(LENGTH(s.vec)), 0) " +
                "FROM files f " +
                "LEFT JOIN segments s ON s.file = f.id",
                [],
                r =>
                {
                    files = (int)r.GetInt64(0);
                    segments = (int)r.GetInt64(1);
                    vectors = (int)r.GetInt64(2);
                    bytes = r.GetInt64(3);
                });
        }
        catch { }

        return new FolderVectorStats("Все папки", files, segments, vectors, bytes);
    }

    /// <summary>
    /// Computes indexed files, segments, and embedding vector disk sizes for files whose path
    /// is NOT inside any of the currently active roots.
    /// </summary>
    public FolderVectorStats GetOrphanedStats(IReadOnlyList<string> activeRoots)
    {
        var prefixes = new List<string>();
        foreach (var root in activeRoots)
        {
            string p = NormalizePath(root);
            prefixes.Add(p.EndsWith('/') ? p : p + "/");
        }

        int files = 0;
        int segments = 0;
        int vectors = 0;
        long bytes = 0;

        try
        {
            _db.Query(
                "SELECT f.id, f.path " +
                "FROM files f",
                [],
                r =>
                {
                    string path = r.NullableText(1) ?? "";
                    bool inside = false;
                    foreach (var p in prefixes)
                    {
                        if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (!inside)
                    {
                        long fileId = r.GetInt64(0);
                        files++;
                        _db.Query(
                            "SELECT COUNT(s.id), COUNT(s.vec), COALESCE(SUM(LENGTH(s.vec)), 0) " +
                            "FROM segments s WHERE s.file = ?",
                            [SqlValue.Int(fileId)],
                            sr =>
                            {
                                segments += (int)sr.GetInt64(0);
                                vectors += (int)sr.GetInt64(1);
                                bytes += sr.GetInt64(2);
                            });
                    }
                });
        }
        catch { }

        return new FolderVectorStats("Неактивные / удаленные папки", files, segments, vectors, bytes);
    }

    /// <summary>
    /// Deletes all files and vectors whose path is NOT under any of activeRoots,
    /// returning the number of files purged.
    /// </summary>
    public int PurgeOrphaned(IReadOnlyList<string> activeRoots)
    {
        int count = Prune(activeRoots);
        try { _db.Execute("VACUUM;"); } catch { }
        return count;
    }

    /// <summary>
    /// Returns the exact count of files in the current scope: Done/Partial, Total, and Pending.
    /// </summary>
    public (int DoneFiles, int TotalFiles, int PendingFiles, int Vectors) GetFileCounts()
    {
        var scope = ScopeSnapshot;
        int done = 0;
        int total = 0;
        int pending = 0;

        try
        {
            _db.Query("SELECT state, COUNT(*) FROM files WHERE 1" + scope.Sql() + " GROUP BY state", scope.Values, r =>
            {
                string state = r.NullableText(0) ?? "";
                int count = (int)r.GetInt64(1);
                total += count;
                if (state is "done" or "partial") done += count;
                else if (state == "pending") pending += count;
            });
        }
        catch { }

        return (done, total, pending, 0);
    }

    // ---------- Schema ----------

    private void Migrate()
    {
        long version = 0;
        _db.Query("PRAGMA user_version", r => { version = r.GetInt64(0); });
        if (version > SchemaVersion)
            throw new InvalidOperationException($"this index was made by a newer WAI (schema {version})");
        if (version == SchemaVersion)
            return;
        // One migrates while the other waits (up to a minute, not the usual 5 s).
        _db.Execute("PRAGMA busy_timeout = 60000");
        try
        {
            _db.Transaction(() =>
            {
                long v = 0;
                _db.Query("PRAGMA user_version", r => { v = r.GetInt64(0); });
                if (v == 0)
                    Create();
                if (v == 1)
                    MigrateKeywordSearch();
                if (v >= 1 && v <= 2)
                    MigrateScreenshotText();
                if (v >= 1 && v <= 3)
                    MigrateVectorNames();
                if (v >= 1 && v <= 4)
                    MigrateLongFiles();
            });
        }
        finally
        {
            _db.Execute("PRAGMA busy_timeout = 5000");
        }
    }

    private void Create()
    {
        _db.Execute(
            "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);" +
            "CREATE TABLE files(" +
            "id INTEGER PRIMARY KEY," +
            "path TEXT NOT NULL UNIQUE," +
            "kind TEXT NOT NULL," +
            "size INTEGER NOT NULL," +
            "mtime REAL NOT NULL," +
            "inode INTEGER NOT NULL," +
            "device INTEGER NOT NULL," +
            "state TEXT NOT NULL DEFAULT 'pending'," +
            "detail TEXT," +
            "info TEXT," +
            "indexed_at REAL" +
            ");" +
            "CREATE INDEX files_inode ON files(device, inode);" +
            "CREATE INDEX files_state ON files(state, kind);" +
            "CREATE TABLE segments(" +
            "id INTEGER PRIMARY KEY," +
            "file INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE," +
            "kind TEXT NOT NULL," +
            "modality TEXT NOT NULL," +
            "loc REAL," +
            "loc_end REAL," +
            "excerpt TEXT," +
            "vec BLOB" +
            ");" +
            "CREATE INDEX segments_file ON segments(file);" +
            "CREATE VIRTUAL TABLE fts USING fts5(" + FtsColumns + ");" +
            "PRAGMA user_version = " + SchemaVersion + ";");
    }

    /// <summary>Schema 1 → 2: keyword search with whole Bengali and Arabic words over
    /// normalized text. Rebuilt from what's stored; garbled PDFs are queued again.</summary>
    private void MigrateKeywordSearch()
    {
        var rows = new List<(long Id, string Name, string Body)>();
        _db.Query("SELECT rowid, name, body FROM fts", r =>
            rows.Add((r.GetInt64(0), r.NullableText(1) ?? "", r.NullableText(2) ?? "")));
        var garbled = new HashSet<long>();
        _db.Query("SELECT s.file, fts.body FROM fts JOIN segments s ON s.id = fts.rowid " +
                  "JOIN files f ON f.id = s.file " +
                  "WHERE f.kind = 'pdf' AND s.kind IN ('page', 'page_image')", r =>
        {
            if (PageTextProblemCheck.Check(r.NullableText(1) ?? "") != null)
                garbled.Add(r.GetInt64(0));
        });
        _db.Execute("CREATE VIRTUAL TABLE fts_new USING fts5(" + FtsColumns + ")");
        foreach (var row in rows)
        {
            var (body, original) = KeywordText(row.Body);
            _db.Run("INSERT INTO fts_new(rowid, name, body, original) VALUES(?, ?, ?, ?)",
                new[] { SqlValue.Int(row.Id), SqlValue.Text(SearchText.Normalized(row.Name)),
                        SqlValue.Text(body), SqlValue.Of(original) });
        }
        _db.Execute("DROP TABLE fts; ALTER TABLE fts_new RENAME TO fts; PRAGMA user_version = 2;");
        foreach (long id in garbled)
            Requeue(id);
    }

    /// <summary>Schema 2 → 3: a screenshot's OCR text is for keyword search only;
    /// its text vectors go, nothing is read again.</summary>
    private void MigrateScreenshotText()
    {
        _db.Execute("UPDATE segments SET vec = NULL, modality = 'none' WHERE kind = 'ocr'; " +
                    "PRAGMA user_version = 3;");
    }

    /// <summary>Schema 3 → 4: indexes name their vectors by a vector version, not by the
    /// runtime build that made them. Every index so far comes from vectors version 1.</summary>
    private void MigrateVectorNames()
    {
        _db.Execute("UPDATE meta SET value = replace(value, ' llama.cpp-b11461-metal ', ' llama.cpp-vectors1 ') " +
                    "WHERE key IN ('fingerprint', 'vector_source'); " +
                    "PRAGMA user_version = 4;");
    }

    /// <summary>Schema 4 → 5: long files finish after the first pass from where they
    /// stopped; text that's mostly digits loses its vectors. Nothing is read or embedded here.</summary>
    private void MigrateLongFiles()
    {
        var digits = new List<long>();
        _db.Query("SELECT s.id, coalesce(fts.original, fts.body) FROM segments s JOIN fts ON fts.rowid = s.id " +
                  "WHERE s.kind IN ('page', 'chunk') AND s.vec IS NOT NULL", r =>
        {
            if (!SearchText.HasWords(r.NullableText(1) ?? ""))
                digits.Add(r.GetInt64(0));
        });
        foreach (long id in digits)
            _db.Run("UPDATE segments SET vec = NULL, modality = 'none' WHERE id = ?",
                new[] { SqlValue.Int(id) });
        var limits = new IndexOptions();
        _db.Execute(
            "UPDATE files SET detail = 'read to page 30', info = json_set(info, '$.read_to', '30', '$.read_end', " +
            "CAST(MIN(CAST(json_extract(info, '$.pages') AS INTEGER), " + limits.MaxPages + ") AS TEXT)) " +
            "WHERE kind = 'pdf' AND state = 'partial' AND info IS NOT NULL;" +
            "UPDATE files SET state = 'partial', detail = 'read to chunk 40', info = json_set(info, '$.read_to', '40', " +
            "'$.read_end', CAST(MIN(MAX(41, (CAST(json_extract(info, '$.chars') AS INTEGER) + 1599) / 1600), " +
            limits.MaxChunks + ") AS TEXT)) " +
            "WHERE kind = 'doc' AND state = 'done' AND json_extract(info, '$.chunks') = '40';" +
            "PRAGMA user_version = 5;");
    }

    // ---------- Meta ----------

    public string? Meta(string key)
    {
        string? value = null;
        _db.Query("SELECT value FROM meta WHERE key = ?",
            new[] { SqlValue.Text(key) }, r => { value = r.NullableText(0); });
        return value;
    }

    public string? GetMeta(string key) => Meta(key);

    public void SetMeta(string key, string? value)
    {
        if (value is null)
            _db.Run("DELETE FROM meta WHERE key = ?", new[] { SqlValue.Text(key) });
        else
            _db.Run("INSERT INTO meta(key, value) VALUES(?, ?) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new[] { SqlValue.Text(key), SqlValue.Text(value) });
    }

    public void Transact(Action action) => _db.Transaction(action);

    // ---------- Files ----------

    private static FileRecord Record(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.NullableText(1) ?? "", FileKindStrings.Parse(r.NullableText(2)),
            r.GetInt64(3), r.GetDouble(4), r.GetInt64(6), r.GetInt64(5), r.NullableText(7) ?? "pending");

    /// <summary>Every file under one of <paramref name="roots"/>.</summary>
    public IReadOnlyList<FileRecord> FilesUnder(IReadOnlyList<string> roots)
    {
        var output = new List<FileRecord>();
        foreach (var root in roots)
        {
            bool covered = false;
            foreach (var other in roots)
            {
                if (!ReferenceEquals(root, other) && root != other && IsInside(root, other))
                { covered = true; break; }
            }
            if (covered)
                continue;
            string prefix = NormalizePath(root);
            if (!prefix.EndsWith('/'))
                prefix += "/";
            _db.Query("SELECT " + FileColumns + " FROM files WHERE path COLLATE WAI_PATH >= ? AND path COLLATE WAI_PATH < ?",
                new[] { SqlValue.Text(prefix), SqlValue.Text(prefix[..^1] + "0") },
                r => output.Add(Record(r)));
        }
        return output;
    }

    /// <summary>Adds a file (pending) and returns its id.</summary>
    public long Insert(CrawlCandidate file)
    {
        return _db.Transaction(() =>
        {
            _db.Run("INSERT INTO files(path, kind, size, mtime, inode, device) VALUES(?, ?, ?, ?, ?, ?)",
                new[] { SqlValue.Text(NormalizePath(file.Path)), SqlValue.Text(file.Kind.ToStorageString()),
                        SqlValue.Int(file.Size), SqlValue.Real(file.Mtime),
                        SqlValue.Int(file.Inode), SqlValue.Int(file.Device) });
            long id = _db.LastInsertId;
            AddNameRow(id, file.Path);
            return id;
        });
    }

    /// <summary>The file changed on disk: drop what was extracted from it and queue it again.</summary>
    public void Requeue(long id, CrawlCandidate file)
    {
        _db.Transaction(() =>
        {
            DeleteSegments(id, keepingName: true);
            _db.Run("UPDATE files SET kind = ?, size = ?, mtime = ?, inode = ?, device = ?, " +
                    "state = 'pending', detail = NULL, info = NULL, indexed_at = NULL WHERE id = ?",
                new[] { SqlValue.Text(file.Kind.ToStorageString()), SqlValue.Int(file.Size),
                        SqlValue.Real(file.Mtime),
                        SqlValue.Int(file.Inode), SqlValue.Int(file.Device),
                        SqlValue.Int(id) });
        });
    }

    /// <summary>Extract and embed the file again, as it is.</summary>
    public void Requeue(long id)
    {
        _db.Transaction(() =>
        {
            DeleteSegments(id, keepingName: true);
            _db.Run("UPDATE files SET state = 'pending', detail = NULL, info = NULL, indexed_at = NULL WHERE id = ?",
                new[] { SqlValue.Int(id) });
        });
    }

    /// <summary>Requeues failed PDFs that could not be read because PDFium was missing.</summary>
    public int RetryMissingPdfiumFailures()
    {
        var scope = ScopeSnapshot;
        const string detailPrefix = "unreadable: pdfium.dll is missing";
        return _db.Transaction(() =>
        {
            var ids = new List<long>();
            var values = new List<SqlValue>
            {
                SqlValue.Int(detailPrefix.Length),
                SqlValue.Text(detailPrefix),
            };
            values.AddRange(scope.Values);
            _db.Query("SELECT id FROM files WHERE kind = 'pdf' AND state = 'failed' " +
                      "AND substr(detail, 1, ?) = ?" + scope.Sql(),
                values, r => ids.Add(r.GetInt64(0)));

            foreach (long id in ids)
                Requeue(id);
            return ids.Count;
        });
    }

    /// <summary>Same file at a new path (a move or rename): only the path changes.</summary>
    public void Move(long id, string toPath)
    {
        _db.Transaction(() =>
        {
            _db.Run("UPDATE files SET path = ? WHERE id = ?",
                new[] { SqlValue.Text(NormalizePath(toPath)), SqlValue.Int(id) });
            _db.Run("UPDATE fts SET name = ?, body = ? " +
                    "WHERE rowid IN (SELECT id FROM segments WHERE file = ? AND kind = 'name')",
                new[] { SqlValue.Text(SearchText.Normalized(NameText(toPath))),
                        SqlValue.Text(SearchText.Normalized(FolderText(toPath))), SqlValue.Int(id) });
        });
    }

    public void Delete(long id)
    {
        _db.Transaction(() =>
        {
            _db.Run("DELETE FROM fts WHERE rowid IN (SELECT id FROM segments WHERE file = ?)",
                new[] { SqlValue.Int(id) });
            _db.Run("DELETE FROM files WHERE id = ?", new[] { SqlValue.Int(id) });
        });
    }

    /// <summary>Removes every file that isn't under one of <paramref name="roots"/>.
    /// A root that doesn't exist right now (ejected drive, offline share)
    /// keeps its files: gone hardware must not read as deleted folders.
    /// Returns how many went.</summary>
    public int Prune(IReadOnlyList<string> roots)
    {
        var prefixes = new List<string>();
        foreach (var root in roots)
        {
            string p = NormalizePath(root);
            prefixes.Add(p.EndsWith('/') ? p : p + "/");
        }
        var gone = new List<long>();
        _db.Query("SELECT id, path FROM files", r =>
        {
            string path = r.NullableText(1) ?? "";
            bool keep = false;
            foreach (var p in prefixes)
            {
                // Windows paths ignore case (the rest of sync compares
                // case-insensitively too): never prune over a capital.
                if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { keep = true; break; }
            }
            if (!keep)
                gone.Add(r.GetInt64(0));
        });
        if (gone.Count == 0)
            return 0;
        _db.Transaction(() =>
        {
            foreach (long id in gone)
                Delete(id);
        });
        return gone.Count;
    }

    /// <summary>Queues every file again and forgets the fingerprint.</summary>
    public void ResetAll()
    {
        _db.Transaction(() =>
        {
            _db.Run("DELETE FROM fts WHERE rowid IN (SELECT id FROM segments WHERE kind != 'name')");
            _db.Run("DELETE FROM segments WHERE kind != 'name'");
            _db.Run("UPDATE files SET state = 'pending', detail = NULL, info = NULL, indexed_at = NULL");
            SetMeta("fingerprint", null);
        });
    }

    /// <summary>Deletes all vectors and segments for files under the specified folders (or all folders if null),
    /// resetting those files to 'pending' for fresh re-embedding.</summary>
    public void ClearVectors(IReadOnlyList<string>? roots = null)
    {
        _db.Transaction(() =>
        {
            if (roots is null || roots.Count == 0)
            {
                _db.Run("DELETE FROM fts WHERE rowid IN (SELECT id FROM segments WHERE kind != 'name')");
                _db.Run("DELETE FROM segments WHERE kind != 'name'");
                _db.Run("UPDATE files SET state = 'pending', detail = NULL, info = NULL, indexed_at = NULL");
                SetMeta("fingerprint", null);
            }
            else
            {
                foreach (string root in roots)
                {
                    string prefix = NormalizePath(root);
                    if (!prefix.EndsWith('/')) prefix += "/";
                    _db.Run("DELETE FROM fts WHERE rowid IN (SELECT s.id FROM segments s JOIN files f ON s.file = f.id WHERE (f.path COLLATE WAI_PATH = ? OR (f.path COLLATE WAI_PATH >= ? AND f.path COLLATE WAI_PATH < ?)) AND s.kind != 'name')",
                        new[] { SqlValue.Text(NormalizePath(root)), SqlValue.Text(prefix), SqlValue.Text(prefix[..^1] + "0") });
                    _db.Run("DELETE FROM segments WHERE file IN (SELECT id FROM files WHERE path COLLATE WAI_PATH = ? OR (path COLLATE WAI_PATH >= ? AND path COLLATE WAI_PATH < ?)) AND kind != 'name'",
                        new[] { SqlValue.Text(NormalizePath(root)), SqlValue.Text(prefix), SqlValue.Text(prefix[..^1] + "0") });
                    _db.Run("UPDATE files SET state = 'pending', detail = NULL, info = NULL, indexed_at = NULL WHERE path COLLATE WAI_PATH = ? OR (path COLLATE WAI_PATH >= ? AND path COLLATE WAI_PATH < ?)",
                        new[] { SqlValue.Text(NormalizePath(root)), SqlValue.Text(prefix), SqlValue.Text(prefix[..^1] + "0") });
                }
                SetMeta("fingerprint", null);
            }
        });
        try { _db.Execute("VACUUM;"); } catch { }
    }

    /// <summary>Files waiting to be embedded, in indexing order; <paramref name="only"/>
    /// narrows it to some files (just-added ones).</summary>
    public IReadOnlyList<FileRecord> Pending(int? limit, ISet<long>? only = null)
    {
        var scope = ScopeSnapshot;
        var output = new List<FileRecord>();
        if (only is not null && only.Count == 0)
            return output;
        string filter = only is null ? "" : " AND id IN (" + string.Join(",", only) + ")";
        var values = new List<SqlValue>(scope.Values) { SqlValue.Int(limit ?? -1) };
        _db.Query("SELECT " + FileColumns + " FROM files WHERE state = 'pending'" + filter + scope.Sql() +
                  " ORDER BY " + FileKindOrder.IndexingOrderSql + ", mtime DESC LIMIT ?",
            values, r => output.Add(Record(r)));
        return output;
    }

    public int PendingCount()
    {
        var scope = ScopeSnapshot;
        int count = 0;
        _db.Query("SELECT COUNT(*) FROM files WHERE state = 'pending'" + scope.Sql(), scope.Values,
            r => { count = (int)r.GetInt64(0); });
        return count;
    }

    /// <summary>Long files with pages or chunks still to read after their first step.</summary>
    public IReadOnlyList<FileRecord> Unfinished()
    {
        var scope = ScopeSnapshot;
        var output = new List<FileRecord>();
        _db.Query("SELECT " + FileColumns + " FROM files WHERE state = 'partial'" + scope.Sql() +
                  " ORDER BY " + FileKindOrder.IndexingOrderSql + ", mtime DESC",
            scope.Values, r => output.Add(Record(r)));
        return output;
    }

    /// <summary>How many long files are unfinished, and how many of their pages and chunks are left.</summary>
    public (int Files, int Left) UnfinishedCount()
    {
        var scope = ScopeSnapshot;
        int files = 0, left = 0;
        _db.Query("SELECT COUNT(*), SUM(MAX(0, CAST(json_extract(info, '$.read_end') AS INTEGER) " +
                  "- CAST(json_extract(info, '$.read_to') AS INTEGER))) " +
                  "FROM files WHERE state = 'partial'" + scope.Sql(), scope.Values,
            r =>
            {
                files = (int)r.GetInt64(0);
                left = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1);
            });
        return (files, left);
    }

    public int UnfinishedFiles() => UnfinishedCount().Files;

    /// <summary>What indexing learned about a file (<see cref="Finish"/>'s info).</summary>
    public Dictionary<string, string?> InfoOf(long fileId)
    {
        var info = new Dictionary<string, string?>();
        _db.Query("SELECT info FROM files WHERE id = ?", new[] { SqlValue.Int(fileId) }, r =>
        {
            string? json = r.NullableText(0);
            if (json is null)
                return;
            try
            {
                info = JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ??
                       new Dictionary<string, string?>();
            }
            catch (JsonException) { }
        });
        return info;
    }

    /// <summary>The last page or chunk stored for a file.</summary>
    public int? LastPlace(long fileId)
    {
        int? last = null;
        _db.Query("SELECT MAX(loc) FROM segments WHERE file = ? AND kind IN ('page', 'page_image', 'chunk')",
            new[] { SqlValue.Int(fileId) },
            r => { last = r.IsDBNull(0) ? null : (int)r.GetDouble(0); });
        return last;
    }

    /// <summary>One more failed try at a file, kept in its info without
    /// touching its state. The indexer quarantines a file after a few
    /// crashes so one bad file can't wedge every run; a change on disk
    /// (Requeue) clears the count. Returns the new total.</summary>
    public int BumpAttempts(long fileId)
    {
        var info = InfoOf(fileId);
        int n = info.TryGetValue("attempts", out string? raw) && int.TryParse(raw, out int v) ? v + 1 : 1;
        info["attempts"] = n.ToString();
        var clean = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in info)
        {
            if (kv.Value is not null)
                clean[kv.Key] = kv.Value;
        }
        _db.Run("UPDATE files SET info = ? WHERE id = ?",
            new[] { SqlValue.Text(JsonSerializer.Serialize(clean)), SqlValue.Int(fileId) });
        return n;
    }

    public void Finish(long id, string state, string? detail = null,
        Dictionary<string, string?>? info = null)
    {
        string? json = null;
        if (info is not null && info.Count > 0)
        {
            var clean = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in info)
            {
                if (kv.Value is not null)
                    clean[kv.Key] = kv.Value;
            }
            if (clean.Count > 0)
                json = JsonSerializer.Serialize(clean);
        }
        _db.Run("UPDATE files SET state = ?, detail = ?, info = ?, indexed_at = ? WHERE id = ?",
            new[] { SqlValue.Text(state), SqlValue.Of(detail), SqlValue.Of(json),
                    SqlValue.Real(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), SqlValue.Int(id) });
    }

    // ---------- Segments ----------

    /// <summary>Adds a segment; <paramref name="text"/> goes into keyword search
    /// (normalized), <paramref name="vector"/> into meaning search.</summary>
    public void AddSegment(long fileId, SegmentKind kind, Modality modality,
        double? loc = null, double? locEnd = null, string? excerpt = null,
        string? text = null, float[]? vector = null)
    {
        _db.Transaction(() =>
        {
            // Clean once on the way in: no lone surrogates ever reach FTS or
            // the snippet path, whatever the extractor handed us.
            excerpt = excerpt is null ? null : TextSanitize.Clean(excerpt);
            text = text is null ? null : TextSanitize.Clean(text);
            byte[]? blob = null;
            if (vector is not null)
            {
                int dimension = StoredVectorDimension() ?? vector.Length;
                blob = Vectors.ToHalfBlob(vector, dimension);
                if (Meta("vector_dim") is null) SetMeta("vector_dim", dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            _db.Run("INSERT INTO segments(file, kind, modality, loc, loc_end, excerpt, vec) " +
                    "VALUES(?, ?, ?, ?, ?, ?, ?)",
                new[] { SqlValue.Int(fileId), SqlValue.Text(kind.ToStorageString()),
                        SqlValue.Text(vector is null ? ModalityStrings.Raw(Modality.None)
                                                    : ModalityStrings.Raw(modality)),
                        SqlValue.Of(loc), SqlValue.Of(locEnd), SqlValue.Of(excerpt),
                        blob is null ? SqlValue.Null : SqlValue.Blob(blob) });
            if (!string.IsNullOrEmpty(text))
            {
                var (body, original) = KeywordText(text);
                _db.Run("INSERT INTO fts(rowid, name, body, original) VALUES(?, '', ?, ?)",
                    new[] { SqlValue.Int(_db.LastInsertId), SqlValue.Text(body), SqlValue.Of(original) });
            }
        });
    }

    /// <summary>Adds a <see cref="Piece"/> as one segment row (+FTS5 row / vector).</summary>
    public void AddSegment(Piece piece) =>
        AddSegment(piece.FileId, piece.Kind, piece.Modality, piece.Loc, piece.LocEnd,
            piece.Excerpt, piece.Text, piece.Vector);

    /// <summary><c>body</c> and <c>original</c> for keyword search: the normalized text,
    /// and the text as written if that differs.</summary>
    public static (string Body, string? Original) KeywordText(string text)
    {
        string normalized = SearchText.Normalized(text);
        return (normalized, normalized == text ? null : text);
    }

    private void AddNameRow(long file, string path)
    {
        _db.Run("INSERT INTO segments(file, kind, modality) VALUES(?, 'name', 'none')",
            new[] { SqlValue.Int(file) });
        _db.Run("INSERT INTO fts(rowid, name, body) VALUES(?, ?, ?)",
            new[] { SqlValue.Int(_db.LastInsertId),
                    SqlValue.Text(SearchText.Normalized(NameText(path))),
                    SqlValue.Text(SearchText.Normalized(FolderText(path))) });
    }

    private void DeleteSegments(long file, bool keepingName)
    {
        string filter = keepingName ? "AND kind != 'name'" : "";
        _db.Run("DELETE FROM fts WHERE rowid IN (SELECT id FROM segments WHERE file = ? " + filter + ")",
            new[] { SqlValue.Int(file) });
        _db.Run("DELETE FROM segments WHERE file = ? " + filter, new[] { SqlValue.Int(file) });
    }

    /// <summary>"Screenshot 2026-10-01 at 9.41.07 PM.png" → searchable words.</summary>
    public static string NameText(string path)
    {
        string p = NormalizePath(path);
        int slash = p.LastIndexOf('/');
        return slash < 0 ? p : p.Substring(slash + 1);
    }

    /// <summary>The folders a file sits in, relative to the home folder
    /// ("Downloads Invoices"), as low-weight keywords.</summary>
    public static string FolderText(string path)
    {
        string home = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string folder = NormalizePath(path);
        int slash = folder.LastIndexOf('/');
        folder = slash < 0 ? "" : folder.Substring(0, slash);
        if (folder.StartsWith(home, StringComparison.Ordinal))
            folder = folder.Substring(home.Length);
        return string.Join(" ", folder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------- Stats ----------

    public Counts Counts()
    {
        var scope = ScopeSnapshot;
        var counts = new Counts();
        _db.Query("SELECT kind, state, COUNT(*) FROM files WHERE 1" + scope.Sql() +
                  " GROUP BY kind, state", scope.Values,
            r => {
                string kind = r.NullableText(0) ?? "?";
                string state = r.NullableText(1) ?? "?";
                if (!counts.ByKindAndState.TryGetValue(kind, out var inner))
                {
                    inner = new Dictionary<string, int>();
                    counts.ByKindAndState[kind] = inner;
                }
                inner[state] = (int)r.GetInt64(2);
            });
        (counts.Unfinished, counts.Left) = UnfinishedCount();
        string inScope = scope.Roots is null ? "" : " AND file IN (SELECT id FROM files WHERE 1" + scope.Sql() + ")";
        _db.Query("SELECT COUNT(*), COUNT(vec) FROM segments WHERE kind != 'name'" + inScope, scope.Values,
            r =>
            {
                counts.Segments = (int)r.GetInt64(0);
                counts.Vectors = (int)r.GetInt64(1);
            });
        return counts;
    }

    /// <summary>
    /// Attempts to deduce configured search folders and verify existing indexed files from an existing SQLite database.
    /// Useful for self-healing settings when settings.json was deleted, damaged, or lost during upgrade.
    /// </summary>
    public static bool TryRecoverFolders(string indexDir, out List<string> recoveredFolders)
    {
        recoveredFolders = [];
        try
        {
            string dbPath = System.IO.Path.Combine(indexDir, MainFile);
            if (!System.IO.File.Exists(dbPath) || new System.IO.FileInfo(dbPath).Length < 32768)
                return false;

            using var db = new Database(dbPath, readOnly: true);
            long fileCount = 0;
            db.Query("SELECT COUNT(*) FROM files", r => fileCount = r.GetInt64(0));
            if (fileCount == 0) return false;

            var samplePaths = new List<string>();
            db.Query("SELECT path FROM files LIMIT 2000", r =>
            {
                if (r.NullableText(0) is { Length: > 0 } p)
                    samplePaths.Add(p);
            });

            recoveredFolders = DeduceSearchRoots(samplePaths);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<string> DeduceSearchRoots(IEnumerable<string> samplePaths)
    {
        var knownSpecialFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } up ? System.IO.Path.Combine(up, "Downloads") : null,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        }.Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.Directory.Exists(p)).Select(System.IO.Path.GetFullPath).ToList();

        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in samplePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string normPath = rawPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
            try { normPath = System.IO.Path.GetFullPath(normPath); } catch { continue; }

            bool matchedSpecial = false;
            foreach (var special in knownSpecialFolders)
            {
                if (normPath.StartsWith(special + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normPath, special, StringComparison.OrdinalIgnoreCase))
                {
                    discovered.Add(special);
                    matchedSpecial = true;
                    break;
                }
            }

            if (!matchedSpecial)
            {
                var parts = normPath.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string root = parts[0] + System.IO.Path.DirectorySeparatorChar;
                    string candidate = root;
                    for (int depth = 1; depth < Math.Min(parts.Length, 3); depth++)
                    {
                        candidate = System.IO.Path.Combine(candidate, parts[depth]);
                        if (System.IO.Directory.Exists(candidate))
                        {
                            discovered.Add(candidate);
                            break;
                        }
                    }
                }
            }
        }

        return discovered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _db.Dispose();
    }
}

/// <summary>Immutable scope captured once per operation, including its bound SQL values.</summary>
internal sealed class PathScope
{
    public IReadOnlyList<string>? Roots { get; }
    public IReadOnlyList<SqlValue> Values { get; }
    public PathScope(IEnumerable<string>? roots)
    {
        Roots = roots?.Select(p => IndexStore.NormalizePath(p).TrimEnd('/') + "/").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Values = Roots?.SelectMany(p => new[] { SqlValue.Text(p), SqlValue.Text(p[..^1] + "0") }).ToArray()
            ?? Array.Empty<SqlValue>();
    }
    public string Sql(string column = "path") => Roots is null ? "" : Roots.Count == 0 ? " AND 0"
        : " AND (" + string.Join(" OR ", Roots.Select(_ => "(" + column + " COLLATE WAI_PATH >= ? AND " + column + " COLLATE WAI_PATH < ?)")) + ")";
}
