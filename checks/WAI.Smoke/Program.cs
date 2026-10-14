using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WAI.Core;

internal static class Program
{
    private const int ExpectedDimension = 768;
    private const double UnitNormTolerance = 0.001;
    private const int SearchLimit = 5;
    private const int WarmRepetitions = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: WAI.Smoke <runDir> <indexDir> <manifestPath> <outputJson>");
            return 2;
        }

        string runDir = Path.GetFullPath(args[0]);
        string indexDir = Path.GetFullPath(args[1]);
        string manifestPath = Path.GetFullPath(args[2]);
        string outputPath = Path.GetFullPath(args[3]);
        var report = new SmokeReport
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            RunDirectory = runDir,
            IndexDirectory = indexDir,
            ManifestPath = manifestPath,
        };

        int exitCode = 0;
        try
        {
            RunProbe(report, runDir, indexDir, manifestPath);
        }
        catch (Exception error)
        {
            report.Error = $"{error.GetType().Name}: {error.Message}";
            exitCode = 2;
        }

        report.FinishedAtUtc = DateTimeOffset.UtcNow;
        report.PositiveMetrics = BuildPositiveMetrics(report.Results);
        report.AbsentFindings = report.Results
            .Where(result => result.Kind == "absent")
            .Select(result => new AbsentFinding(
                result.Query,
                result.Direction,
                result.HybridTop,
                result.HybridTop.Count > 0,
                result.LexicalTop,
                result.LexicalTop.Count > 0))
            .ToList();

        try
        {
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions) + "\n", new UTF8Encoding(false));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not write report: {error.Message}");
            return 2;
        }

        PrintSummary(report, outputPath);
        return exitCode;
    }

    private static void RunProbe(SmokeReport report, string runDir, string indexDir, string manifestPath)
    {
        ManifestQuery[] queries = ReadManifest(manifestPath);
        string executable = Path.Combine(runDir, "wai-embed.exe");

        using var store = new IndexStore(indexDir);
        using var worker = new WorkerClient(new WorkerConfig(
            executable,
            Array.Empty<string>(),
            TextOnly: true,
            LogPath: Path.Combine(indexDir, "smoke-worker.log")));
        using var searcher = new Searcher(store, loadVectors: true);

        report.Worker = new WorkerSummary(
            worker.Info.Model,
            worker.Info.Revision,
            worker.Info.Runtime,
            worker.Info.Dtype,
            worker.Info.Dim,
            worker.Info.TextOnly,
            worker.Info.LoadSeconds,
            worker.Info.VectorSource);
        report.SearcherVectorSource = searcher.VectorSource;
        report.StoredVectorCount = searcher.VectorCount;
        report.VectorSourceMatches = string.Equals(
            searcher.VectorSource,
            worker.Info.VectorSource,
            StringComparison.Ordinal);

        if (!report.VectorSourceMatches.Value)
        {
            throw new InvalidOperationException(
                $"Index vector source '{searcher.VectorSource ?? "<missing>"}' does not match worker '{worker.Info.VectorSource}'.");
        }

        if (worker.Info.Dim != ExpectedDimension)
        {
            throw new InvalidDataException(
                $"Worker reports dimension {worker.Info.Dim}; expected {ExpectedDimension}.");
        }

        foreach (ManifestQuery query in queries)
        {
            Stopwatch embedTimer = Stopwatch.StartNew();
            float[][] vectors = worker.EmbedTexts(new[] { Prompts.Query(query.Query) });
            embedTimer.Stop();

            if (vectors.Length != 1)
                throw new InvalidDataException($"Worker returned {vectors.Length} vectors for one query.");

            float[] vector = vectors[0];
            double norm = ValidateVector(vector, query.Query);

            Stopwatch hybridTimer = Stopwatch.StartNew();
            List<SearchHit> hybridHits = searcher.Search(query.Query, vector, limit: SearchLimit);
            hybridTimer.Stop();

            Stopwatch lexicalTimer = Stopwatch.StartNew();
            List<SearchHit> lexicalHits = searcher.Search(query.Query, null, limit: SearchLimit);
            lexicalTimer.Stop();

            List<string> hybridTop = Names(hybridHits);
            List<string> lexicalTop = Names(lexicalHits);
            report.Results.Add(new QueryResult(
                query.Query,
                query.ExpectedFilename,
                query.Kind,
                query.Direction,
                embedTimer.Elapsed.TotalMilliseconds,
                hybridTimer.Elapsed.TotalMilliseconds,
                vector.Length,
                norm,
                hybridTop,
                RankOf(hybridTop, query.ExpectedFilename),
                lexicalTimer.Elapsed.TotalMilliseconds,
                lexicalTop,
                RankOf(lexicalTop, query.ExpectedFilename)));
        }

        ManifestQuery warmQuery = queries.FirstOrDefault(query =>
                query.Direction?.StartsWith("Russian→", StringComparison.Ordinal) == true)
            ?? throw new InvalidDataException("Manifest has no Russian-language query for warm timing.");

        var warmSamples = new List<double>(WarmRepetitions);
        for (int i = 0; i < WarmRepetitions; i++)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            float[][] vectors = worker.EmbedTexts(new[] { Prompts.Query(warmQuery.Query) });
            if (vectors.Length != 1)
                throw new InvalidDataException($"Worker returned {vectors.Length} vectors for warm query {i + 1}.");

            float[] vector = vectors[0];
            _ = ValidateVector(vector, warmQuery.Query);
            _ = searcher.Search(warmQuery.Query, vector, limit: SearchLimit);
            totalTimer.Stop();
            warmSamples.Add(totalTimer.Elapsed.TotalMilliseconds);
        }

        double[] sortedSamples = warmSamples.Order().ToArray();
        report.WarmTiming = new WarmTiming(
            warmQuery.Query,
            warmSamples.Count,
            warmSamples,
            Median(sortedSamples),
            NearestRankPercentile(sortedSamples, 0.95),
            "nearest-rank (ceil(p*n))");
    }

    private static ManifestQuery[] ReadManifest(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        JsonElement queryArray = document.RootElement.GetProperty("queries");
        var queries = new List<ManifestQuery>();

        foreach (JsonElement element in queryArray.EnumerateArray())
        {
            string query = element.GetProperty("query").GetString()
                ?? throw new InvalidDataException("Manifest query text cannot be null.");
            JsonElement expectedElement = element.GetProperty("expected_filename");
            string? expectedFilename = expectedElement.ValueKind == JsonValueKind.Null
                ? null
                : expectedElement.GetString();
            string kind = element.GetProperty("kind").GetString()?.ToLowerInvariant()
                ?? throw new InvalidDataException($"Manifest kind is missing for query '{query}'.");
            string? direction = element.TryGetProperty("direction", out JsonElement directionElement)
                ? directionElement.GetString()
                : null;

            if (kind is not ("semantic" or "exact" or "absent"))
                throw new InvalidDataException($"Unsupported kind '{kind}' for query '{query}'.");
            if ((kind == "absent") != (expectedFilename is null))
                throw new InvalidDataException($"Expected filename and kind disagree for query '{query}'.");

            queries.Add(new ManifestQuery(query, expectedFilename, kind, direction));
        }

        if (queries.Count == 0)
            throw new InvalidDataException("Manifest contains no queries.");

        return queries.ToArray();
    }

    private static double ValidateVector(float[] vector, string query)
    {
        if (vector.Length != ExpectedDimension)
        {
            throw new InvalidDataException(
                $"Embedding for '{query}' has {vector.Length} values; expected {ExpectedDimension}.");
        }

        double squaredNorm = 0;
        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException($"Embedding for '{query}' contains a non-finite value.");
            squaredNorm += (double)value * value;
        }

        double norm = Math.Sqrt(squaredNorm);
        if (!double.IsFinite(norm) || Math.Abs(norm - 1.0) > UnitNormTolerance)
        {
            throw new InvalidDataException(
                $"Embedding for '{query}' has L2 norm {norm.ToString("G9", CultureInfo.InvariantCulture)}; " +
                $"expected unit norm within {UnitNormTolerance}.");
        }

        return norm;
    }

    private static List<string> Names(List<SearchHit> hits) =>
        hits.Take(SearchLimit).Select(hit => Path.GetFileName(hit.Path)).ToList();

    private static int? RankOf(List<string> names, string? expectedFilename)
    {
        if (expectedFilename is null)
            return null;

        int index = names.FindIndex(name => string.Equals(name, expectedFilename, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index + 1 : null;
    }

    private static Dictionary<string, Dictionary<string, HitMetrics>> BuildPositiveMetrics(
        IReadOnlyList<QueryResult> results)
    {
        var allMetrics = new Dictionary<string, Dictionary<string, HitMetrics>>(StringComparer.Ordinal);
        foreach (string mode in new[] { "hybrid", "lexicalOnly" })
        {
            var byKind = new Dictionary<string, HitMetrics>(StringComparer.Ordinal);
            foreach (string kind in new[] { "exact", "semantic" })
            {
                QueryResult[] positive = results.Where(result => result.Kind == kind).ToArray();
                int top1Hits = positive.Count(result => RankFor(result, mode) == 1);
                int top5Hits = positive.Count(result => RankFor(result, mode) is >= 1 and <= SearchLimit);
                byKind[kind] = new HitMetrics(
                    positive.Length,
                    top1Hits,
                    positive.Length == 0 ? null : (double)top1Hits / positive.Length,
                    top5Hits,
                    positive.Length == 0 ? null : (double)top5Hits / positive.Length);
            }

            allMetrics[mode] = byKind;
        }

        return allMetrics;
    }

    private static int? RankFor(QueryResult result, string mode) => mode switch
    {
        "hybrid" => result.HybridExpectedRank,
        "lexicalOnly" => result.LexicalExpectedRank,
        _ => null,
    };

    private static double Median(double[] sorted)
    {
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private static double NearestRankPercentile(double[] sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static void PrintSummary(SmokeReport report, string outputPath)
    {
        Console.WriteLine($"Report: {outputPath}");
        Console.WriteLine($"Queries recorded: {report.Results.Count}");

        if (report.Error is not null)
        {
            Console.WriteLine($"Probe failed: {report.Error}");
            return;
        }

        Console.WriteLine($"Vector source match: {report.VectorSourceMatches}; stored vectors: {report.StoredVectorCount}");
        foreach (string mode in new[] { "hybrid", "lexicalOnly" })
        {
            foreach (string kind in new[] { "exact", "semantic" })
            {
                HitMetrics metric = report.PositiveMetrics[mode][kind];
                Console.WriteLine(
                    $"{mode} {kind}: top1 {metric.Top1Hits}/{metric.Evaluated}, top5 {metric.Top5Hits}/{metric.Evaluated}");
            }
        }

        if (report.AbsentFindings.Count > 0)
        {
            int withHybridHits = report.AbsentFindings.Count(finding => finding.HybridReturnedAny);
            Console.WriteLine($"Absent queries with hybrid top-5 results: {withHybridHits}/{report.AbsentFindings.Count} (reported, not asserted)");
        }

        if (report.WarmTiming is { } warm)
        {
            Console.WriteLine(
                $"Warm encode+search ({warm.Repetitions}): median {warm.MedianMs.ToString("F3", CultureInfo.InvariantCulture)} ms, " +
                $"p95 {warm.P95Ms.ToString("F3", CultureInfo.InvariantCulture)} ms");
        }
    }

    private sealed record ManifestQuery(string Query, string? ExpectedFilename, string Kind, string? Direction);

    private sealed record WorkerSummary(
        string Model,
        string Revision,
        string Runtime,
        string Dtype,
        int Dimension,
        bool TextOnly,
        double? LoadSeconds,
        string VectorSource);

    private sealed record QueryResult(
        string Query,
        string? ExpectedFilename,
        string Kind,
        string? Direction,
        double EmbeddingMs,
        double HybridSearchMs,
        int EmbeddingDimension,
        double EmbeddingNorm,
        List<string> HybridTop,
        int? HybridExpectedRank,
        double LexicalSearchMs,
        List<string> LexicalTop,
        int? LexicalExpectedRank);

    private sealed record HitMetrics(
        int Evaluated,
        int Top1Hits,
        double? Top1Rate,
        int Top5Hits,
        double? Top5Rate);

    private sealed record AbsentFinding(
        string Query,
        string? Direction,
        List<string> HybridTop,
        bool HybridReturnedAny,
        List<string> LexicalTop,
        bool LexicalReturnedAny);

    private sealed record WarmTiming(
        string Query,
        int Repetitions,
        List<double> SamplesMs,
        double MedianMs,
        double P95Ms,
        string PercentileMethod);

    private sealed class SmokeReport
    {
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public string RunDirectory { get; set; } = string.Empty;
        public string IndexDirectory { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public WorkerSummary? Worker { get; set; }
        public string? SearcherVectorSource { get; set; }
        public int? StoredVectorCount { get; set; }
        public bool? VectorSourceMatches { get; set; }
        public List<QueryResult> Results { get; } = new();
        public Dictionary<string, Dictionary<string, HitMetrics>> PositiveMetrics { get; set; } = new();
        public List<AbsentFinding> AbsentFindings { get; set; } = new();
        public WarmTiming? WarmTiming { get; set; }
        public string? Error { get; set; }
    }
}
