using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WAI.Core;

return await Benchmark.Run(args);

internal static class Benchmark
{
    private const int Dimension = 768;
    private const int DatasetVersion = 1;
    private const long MemoryBudget = 3L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Run(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help) { Console.WriteLine(Options.Usage); return 0; }
            Directory.CreateDirectory(options.StateDirectory);
            var results = new List<DatasetResult>();
            foreach (int count in options.Counts)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                string directory = Path.Combine(options.StateDirectory, count.ToString(CultureInfo.InvariantCulture));
                var manifest = Prepare(directory, count);
                Console.WriteLine($"Measuring {count:N0} fragments / {manifest.Files:N0} files");
                results.Add(await Measure(directory, manifest, options));
            }
            var report = new Report(DateTimeOffset.UtcNow, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription, Environment.ProcessorCount, Dimension,
                "Exact SIMD fp16 scan with bounded parallel partitions for large matrices; no ANN. Cold = unloaded Searcher, OS disk cache is not flushed.", results);
            string reportPath = Path.GetFullPath(options.ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine($"Report: {reportPath}");
            return results.All(r => r.Passed) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static DatasetManifest Prepare(string directory, int fragments)
    {
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "dataset.json");
        if (File.Exists(manifestPath))
        {
            var existing = JsonSerializer.Deserialize<DatasetManifest>(File.ReadAllText(manifestPath));
            if (existing is { Version: DatasetVersion, Dimension: Dimension } && existing.Fragments == fragments
                && File.Exists(Path.Combine(directory, IndexStore.MainFile)))
            {
                using var check = new IndexStore(directory);
                long actual = 0;
                check.Db.Query("SELECT COUNT(*) FROM segments WHERE vec IS NOT NULL", r => actual = r.GetInt64(0));
                if (actual == fragments) return existing;
            }
            throw new InvalidOperationException($"Incomplete or incompatible dataset at {directory}. Choose another --state directory or remove that generated dataset manually.");
        }
        string databasePath = Path.Combine(directory, IndexStore.MainFile);
        if (File.Exists(databasePath))
            throw new InvalidOperationException($"Dataset has no completion manifest: {directory}. Choose another --state directory or remove that generated dataset manually.");
        int files = Math.Min(10_000, Math.Max(20, fragments / 10));
        int[] queryRows = { fragments / 4, fragments / 2, fragments * 3 / 4 };
        float[][] queries = queryRows.Select(MakeVector).ToArray();
        var best = queries.Select(_ => Enumerable.Repeat(float.NegativeInfinity, files).ToArray()).ToArray();
        Console.WriteLine($"Generating {fragments:N0} x {Dimension} fp16 vectors ({fragments * Dimension * 2L / 1024 / 1024:N0} MiB), streaming to SQLite");
        var timer = Stopwatch.StartNew();
        using (var store = new IndexStore(directory))
        {
            store.SetMeta("vector_dim", Dimension.ToString(CultureInfo.InvariantCulture));
            store.SetMeta("vector_source", "wai-performance-seeded-v1");
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            connection.CreateCollation("WAI_PATH", (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a, b));
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-8192; PRAGMA wal_autocheckpoint=1000;";
                pragma.ExecuteNonQuery();
            }
            using (var transaction = connection.BeginTransaction())
            using (var file = connection.CreateCommand())
            {
                file.Transaction = transaction;
                file.CommandText = "INSERT INTO files(id,path,kind,size,mtime,inode,device,state) VALUES($id,$path,'doc',100,1,0,0,'done')";
                var id = file.Parameters.Add("$id", SqliteType.Integer);
                var path = file.Parameters.Add("$path", SqliteType.Text);
                file.Prepare();
                for (int i = 1; i <= files; i++)
                {
                    id.Value = i;
                    path.Value = FilePath(i);
                    file.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO segments(id,file,kind,modality,vec) VALUES($id,$file,'chunk','text',$vec)";
            var rowId = insert.Parameters.Add("$id", SqliteType.Integer);
            var fileId = insert.Parameters.Add("$file", SqliteType.Integer);
            var vector = insert.Parameters.Add("$vec", SqliteType.Blob);
            var row = new float[Dimension];
            var blob = new byte[Dimension * 2];
            vector.Value = blob;
            insert.Prepare();
            for (int first = 0; first < fragments; first += 1000)
            {
                GuardMemory();
                using var transaction = connection.BeginTransaction();
                insert.Transaction = transaction;
                int end = Math.Min(first + 1000, fragments);
                for (int i = first; i < end; i++)
                {
                    FillVector(i, row);
                    for (int d = 0; d < Dimension; d++)
                    {
                        Half half = (Half)row[d];
                        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(d * 2), BitConverter.HalfToUInt16Bits(half));
                        row[d] = (float)half;
                    }
                    int targetFile = i % files;
                    for (int q = 0; q < queries.Length; q++)
                        best[q][targetFile] = Math.Max(best[q][targetFile], Vectors.DotProduct(row, queries[q]));
                    rowId.Value = i + 1;
                    fileId.Value = targetFile + 1;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
                if ((first + 1000) % 100_000 == 0) Console.WriteLine($"Generated {end:N0} / {fragments:N0}");
            }
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }
        var references = best.Select(scores => scores.Select((cosine, index) => new RankedFile(index + 1, cosine))
            .OrderByDescending(r => r.Cosine).ThenBy(r => r.FileId).Take(10).ToArray()).ToArray();
        var manifest = new DatasetManifest(DatasetVersion, Dimension, fragments, files, queryRows, references, timer.Elapsed.TotalSeconds);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return manifest;
    }

    private static async Task<DatasetResult> Measure(string directory, DatasetManifest manifest, Options options)
    {
        using var store = new IndexStore(directory);
        using var monitor = new MemoryMonitor();
        var cold = Stopwatch.StartNew();
        using var searcher = new Searcher(store);
        cold.Stop();
        var queries = manifest.QueryRows.Select(MakeVector).ToArray();
        var recalls = new List<RecallResult>();
        for (int q = 0; q < queries.Length; q++)
        {
            var hits = searcher.Search("", queries[q], limit: 10, zGate: double.NegativeInfinity);
            var reference = manifest.References[q];
            var expected = reference.Select(r => FilePath(r.FileId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            double recall10 = hits.Count(h => expected.Contains(h.Path)) / 10.0;
            bool top1 = hits.Count > 0 && hits[0].Path.Equals(FilePath(reference[0].FileId), StringComparison.OrdinalIgnoreCase);
            bool cosineMatches = hits.All(h => reference.Any(r => FilePath(r.FileId).Equals(h.Path, StringComparison.OrdinalIgnoreCase)
                && h.Cosine is double c && Math.Abs(c - r.Cosine) <= 0.00001));
            recalls.Add(new RecallResult(manifest.QueryRows[q] + 1, top1 ? 1 : 0, recall10, cosineMatches));
        }
        // All measured repetitions execute the same known queries; initial verification warms JIT and buffers.
        var sequential = Timings(searcher, queries, options.Repetitions, manifest);
        long writerUpdates = 0;
        using var writerCancellation = new CancellationTokenSource();
        using var writerReady = new ManualResetEventSlim();
        var writer = Task.Run(async () =>
        {
            try
            {
                while (!writerCancellation.IsCancellationRequested)
                {
                    store.Db.Transaction(() =>
                    {
                        store.Db.Run("UPDATE files SET detail = ? WHERE id = ?", new[] { SqlValue.Text("benchmark write " + writerUpdates), SqlValue.Int(1 + writerUpdates % manifest.Files) });
                    });
                    Interlocked.Increment(ref writerUpdates);
                    writerReady.Set();
                    await Task.Delay(25, writerCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (writerCancellation.IsCancellationRequested) { }
        });
        TimingResult concurrent;
        try
        {
            if (!writerReady.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Background writer did not start");
            concurrent = Timings(searcher, queries, options.Repetitions, manifest);
        }
        finally
        {
            writerCancellation.Cancel();
            await writer;
        }
        monitor.Sample();
        bool passed = recalls.All(r => r.Top1Recall == 1 && r.Top10Recall == 1 && r.CosineMatches)
            && sequential.RecallInvariant && concurrent.RecallInvariant
            && (options.MaxP95Milliseconds is null || Math.Max(sequential.P95Milliseconds, concurrent.P95Milliseconds) <= options.MaxP95Milliseconds)
            && (options.MaxPrivateMegabytes is null || monitor.PeakPrivateBytes / 1048576.0 <= options.MaxPrivateMegabytes);
        Console.WriteLine($"Cold load {cold.Elapsed.TotalMilliseconds:F1} ms; warm p50/p95 {sequential.P50Milliseconds:F1}/{sequential.P95Milliseconds:F1} ms; writer p95 {concurrent.P95Milliseconds:F1} ms; recall {(passed ? "PASS" : "FAIL")}");
        return new DatasetResult(manifest.Fragments, manifest.Files, manifest.GenerationSeconds, cold.Elapsed.TotalMilliseconds,
            sequential, concurrent, writerUpdates, monitor.PeakWorkingBytes, monitor.PeakPrivateBytes,
            IndexStore.SizeOf(store.FilePath), recalls, passed);
    }

    private static TimingResult Timings(Searcher searcher, float[][] queries, int repetitions, DatasetManifest manifest)
    {
        var times = new double[repetitions];
        bool recallInvariant = true;
        for (int i = 0; i < repetitions; i++)
        {
            GuardMemory();
            var timer = Stopwatch.StartNew();
            var hits = searcher.Search("", queries[i % queries.Length], limit: 10, zGate: double.NegativeInfinity);
            times[i] = timer.Elapsed.TotalMilliseconds;
            var reference = manifest.References[i % queries.Length];
            recallInvariant &= hits.Count == reference.Length && hits.Select(h => h.Path)
                .SequenceEqual(reference.Select(r => FilePath(r.FileId)), StringComparer.OrdinalIgnoreCase);
            recallInvariant &= hits.Select((h, rank) => rank < reference.Length && h.Cosine is double c
                && Math.Abs(c - reference[rank].Cosine) <= 0.00001).All(matches => matches);
        }
        Array.Sort(times);
        return new TimingResult(repetitions, Percentile(times, 0.5), Percentile(times, 0.95), times[0], times[^1], recallInvariant);
    }

    private static double Percentile(double[] sorted, double percentile) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    private static string FilePath(int id) => $"C:/wai-benchmark/file{id:D6}.txt";
    private static float[] MakeVector(int seed) { var vector = new float[Dimension]; FillVector(seed, vector); return vector; }
    private static void FillVector(int seed, float[] vector)
    {
        uint state = unchecked((uint)(seed + 1) * 747796405u + 2891336453u);
        double squares = 0;
        for (int d = 0; d < vector.Length; d++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            float value = (state >> 8) * (2f / 16777216f) - 1f;
            vector[d] = value;
            squares += (double)value * value;
        }
        float scale = (float)(1 / Math.Sqrt(squares));
        for (int d = 0; d < vector.Length; d++) vector[d] *= scale;
    }
    private static void GuardMemory()
    {
        using var process = Process.GetCurrentProcess();
        if (process.PrivateMemorySize64 > MemoryBudget || process.WorkingSet64 > MemoryBudget)
            throw new InvalidOperationException("Benchmark exceeded its 3 GiB process memory budget");
    }

    private sealed class MemoryMonitor : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private readonly object _gate = new();
        public long PeakWorkingBytes { get; private set; }
        public long PeakPrivateBytes { get; private set; }
        public MemoryMonitor()
        {
            Sample();
            _task = Task.Run(async () =>
            {
                try { while (!_stop.IsCancellationRequested) { Sample(); await Task.Delay(50, _stop.Token); } }
                catch (OperationCanceledException) { }
            });
        }
        public void Sample()
        {
            using var process = Process.GetCurrentProcess();
            lock (_gate)
            {
                PeakWorkingBytes = Math.Max(PeakWorkingBytes, process.WorkingSet64);
                PeakPrivateBytes = Math.Max(PeakPrivateBytes, process.PrivateMemorySize64);
            }
        }
        public void Dispose() { _stop.Cancel(); _task.GetAwaiter().GetResult(); _stop.Dispose(); }
    }

    private sealed record Options(int[] Counts, int Repetitions, string StateDirectory, string ReportPath,
        double? MaxP95Milliseconds, double? MaxPrivateMegabytes, bool Help)
    {
        public const string Usage = "WAI.Performance [--counts 10000,100000] [--million] [--repetitions 12] [--state <dir>] [--report <file>] [--max-p95-ms <n>] [--max-private-mb <n>]";
        public static Options Parse(string[] args)
        {
            int[] counts = { 10_000, 100_000 };
            int repetitions = 12;
            string state = Path.GetFullPath("state/perf");
            string? report = null;
            double? maxP95 = null, maxPrivate = null;
            bool million = false, help = false;
            for (int i = 0; i < args.Length; i++)
            {
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
                switch (args[i])
                {
                    case "--counts": counts = Value().Split(',').Select(int.Parse).ToArray(); break;
                    case "--million": million = true; break;
                    case "--repetitions": repetitions = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--state": state = Path.GetFullPath(Value()); break;
                    case "--report": report = Path.GetFullPath(Value()); break;
                    case "--max-p95-ms": maxP95 = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--max-private-mb": maxPrivate = double.Parse(Value(), CultureInfo.InvariantCulture); break;
                    case "--help": case "-h": help = true; break;
                    default: throw new ArgumentException("Unknown argument: " + args[i]);
                }
            }
            if (million) counts = counts.Append(1_000_000).Distinct().ToArray();
            if (counts.Length == 0 || counts.Any(n => n < 20 || n > 1_000_000)) throw new ArgumentException("Counts must be between 20 and 1000000 fragments");
            if (repetitions < 3) throw new ArgumentException("Use at least 3 repetitions");
            return new(counts, repetitions, state, report ?? Path.Combine(state, "report.json"), maxP95, maxPrivate, help);
        }
    }
    private sealed record RankedFile(int FileId, float Cosine);
    private sealed record DatasetManifest(int Version, int Dimension, int Fragments, int Files, int[] QueryRows, RankedFile[][] References, double GenerationSeconds);
    private sealed record RecallResult(int QuerySegmentId, double Top1Recall, double Top10Recall, bool CosineMatches);
    private sealed record TimingResult(int Repetitions, double P50Milliseconds, double P95Milliseconds, double MinMilliseconds, double MaxMilliseconds, bool RecallInvariant);
    private sealed record DatasetResult(int Fragments, int Files, double GenerationSeconds, double ColdLoadMilliseconds,
        TimingResult Sequential, TimingResult WithBackgroundWriter, long BackgroundWrites, long PeakWorkingBytes,
        long PeakPrivateBytes, long IndexBytes, List<RecallResult> Recall, bool Passed);
    private sealed record Report(DateTimeOffset TimestampUtc, string Runtime, string OperatingSystem, int ProcessorCount,
        int Dimension, string Method, List<DatasetResult> Datasets);
}
