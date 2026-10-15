using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WAI.Core;
using Xunit;
using Xunit.Abstractions;

namespace WAI.Tests;

public class ModelBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public ModelBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "testbed-russian-eval.json")))
                return dir;
            string? parent = Path.GetDirectoryName(dir);
            if (parent == dir || string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }
        return AppContext.BaseDirectory;
    }

    private sealed record QueryEvalItem(
        string Query,
        string? ExpectedFilename,
        string Kind,
        string Direction);

    private sealed record ModelBenchResult(
        string ModelName,
        string ModelId,
        int Dimension,
        string Quantization,
        double LoadSeconds,
        double RamMb,
        double AvgLatencyMs,
        double P95LatencyMs,
        double AccuracyPercent,
        int Hits,
        int Total,
        List<(string Query, string Direction, string Expected, string Top1, double Sim, bool Pass, double LatMs)> Details);

    public sealed class ModelIntegrationFactAttribute : FactAttribute
    {
        public ModelIntegrationFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("WAI_RUN_MODEL_BENCHMARKS") != "1")
                Skip = "Explicit integration benchmark: set WAI_RUN_MODEL_BENCHMARKS=1 with local worker and models.";
        }
    }

    [ModelIntegrationFact]
    [Trait("Category", "Integration")]
    public void Benchmark_AllEmbeddingModels_RetrievalAccuracyAndLatency()
    {
        string repoRoot = FindRepoRoot();
        string evalJsonPath = Path.Combine(repoRoot, "testbed-russian-eval.json");
        string testbedDir = Path.Combine(repoRoot, "testbed");
        string modelsDir = Path.Combine(repoRoot, "models");
        string runDir = Environment.GetEnvironmentVariable("WAI_BENCHMARK_RUN_DIR")
            ?? Path.Combine(repoRoot, "dist", "staging");
        string workerExe = Path.Combine(runDir, "wai-embed.exe");
        string resultsDir = Environment.GetEnvironmentVariable("WAI_BENCHMARK_OUTPUT_DIR")
            ?? Path.Combine(repoRoot, "results");
        Directory.CreateDirectory(resultsDir);

        Assert.True(File.Exists(evalJsonPath), $"Missing required evaluation manifest: {evalJsonPath}");
        Assert.True(Directory.Exists(testbedDir), $"Missing required testbed: {testbedDir}");
        Assert.True(File.Exists(workerExe), $"Missing required real worker: {workerExe}");
        Assert.True(Directory.Exists(modelsDir), $"Missing required local models: {modelsDir}");
        // 1. Load evaluation dataset
        using var jsonDoc = JsonDocument.Parse(File.ReadAllText(evalJsonPath));
        var queries = new List<QueryEvalItem>();
        foreach (var q in jsonDoc.RootElement.GetProperty("queries").EnumerateArray())
        {
            string query = q.GetProperty("query").GetString()!;
            string? expected = q.TryGetProperty("expected_filename", out var expProp) && expProp.ValueKind == JsonValueKind.String
                ? expProp.GetString() : null;
            string kind = q.GetProperty("kind").GetString()!;
            string dir = q.GetProperty("direction").GetString()!;
            queries.Add(new QueryEvalItem(query, expected, kind, dir));
        }

        // 2. Load testbed documents and text representation
        var docFiles = Directory.GetFiles(testbedDir).OrderBy(Path.GetFileName).ToList();
        var docTexts = new Dictionary<string, string>();
        foreach (string file in docFiles)
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".txt" or ".md" or ".html")
            {
                docTexts[name] = File.ReadAllText(file);
            }
            else if (name.Contains("Скриншот-счет") || name.Contains("счет"))
            {
                docTexts[name] = "Счет на оплату № 88392 от 12.10.2026. Услуги поискового индексирования файлов. Итого к оплате: 15400 рублей. Оплата услуг поиска.";
            }
            else if (name == "Screenshot-probe.png")
            {
                docTexts[name] = "Screenshot probe diagnostic invoice payment settings.";
            }
            else if (ext is ".png" or ".jpg" or ".jpeg")
            {
                docTexts[name] = $"{Path.GetFileNameWithoutExtension(name)} photo image sample graphic";
            }
            else if (ext is ".wav" or ".mp3")
            {
                docTexts[name] = $"{Path.GetFileNameWithoutExtension(name)} audio recording speech voice track";
            }
            else if (ext is ".mp4" or ".mov")
            {
                docTexts[name] = $"{Path.GetFileNameWithoutExtension(name)} video recording screen demonstration";
            }
            else
            {
                docTexts[name] = name;
            }
        }

        // 3. Models to benchmark
        var models = new[]
        {
            (Id: "gemma2", Name: "EmbeddingGemma 2 (Google, 2.6B)", Dim: 768, Quant: "Q8_0", TextOnly: false),
            (Id: "bge-m3", Name: "BGE-M3 (BAAI, 568M)", Dim: 1024, Quant: "Q4_K_M", TextOnly: true),
            (Id: "nomic", Name: "Nomic Embed v1.5 (Nomic, 137M)", Dim: 768, Quant: "Q8_0", TextOnly: true),
        };

        var benchmarkResults = new List<ModelBenchResult>();

        foreach (var m in models)
        {
            string[] requiredFiles = m.Id switch
            {
                "gemma2" => ["embeddinggemma-2-Q8_0.gguf", "mmproj-embeddinggemma-2-Q8_0.gguf"],
                "bge-m3" => ["bge-m3-Q4_K_M.gguf"],
                "nomic" => ["nomic-embed-text-v1.5.Q8_0.gguf"],
                _ => throw new InvalidOperationException($"Unknown benchmark model: {m.Id}"),
            };
            foreach (string requiredFile in requiredFiles)
                Assert.True(File.Exists(Path.Combine(modelsDir, requiredFile)), $"Required local benchmark model missing: {requiredFile}");
            _output.WriteLine($"\n=== Benchmarking {m.Name} ===");
            var logPath = Path.Combine(resultsDir, $"worker-{m.Id}.log");

            var config = new WorkerConfig(
                Executable: workerExe,
                Arguments: ["--model", m.Id, "--cpu"],
                Environment: new Dictionary<string, string> { ["WAI_MODELS"] = modelsDir },
                LogPath: logPath,
                TextOnly: m.TextOnly);

            var swStart = Stopwatch.StartNew();
            using var client = new WorkerClient(config);
            swStart.Stop();

            double loadSec = client.Info.LoadSeconds ?? Math.Round(swStart.Elapsed.TotalSeconds, 2);
            double ramMb = 0;
            try
            {
                var procs = Process.GetProcessesByName("wai-embed");
                if (procs.Length > 0) ramMb = Math.Round(procs[0].WorkingSet64 / (1024.0 * 1024.0), 1);
            }
            catch { }

            _output.WriteLine($"Loaded in {loadSec:0.00}s | Dim: {client.Info.Dim} | RAM: {ramMb:0.0} MB");

            // Embed testbed docs
            var docNamesList = docTexts.Keys.ToList();
            var docVectors = new List<float[]>();
            foreach (string dName in docNamesList)
            {
                var v = client.EmbedTexts([docTexts[dName]]);
                docVectors.Add(v[0]);
            }

            // Warmup
            client.EmbedTexts(["warmup query"]);

            // Run evaluation queries
            var queryLatencies = new List<double>();
            int hits = 0;
            var details = new List<(string, string, string, string, double, bool, double)>();

            foreach (var q in queries)
            {
                var sw = Stopwatch.StartNew();
                var qVecs = client.EmbedTexts([q.Query]);
                sw.Stop();

                double latMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                queryLatencies.Add(latMs);
                var qVec = qVecs[0];

                // Find top-1 document by cosine similarity (dot product of normalized vectors)
                double bestSim = -1;
                string bestDoc = "";
                for (int i = 0; i < docNamesList.Count; i++)
                {
                    double sim = 0;
                    var dVec = docVectors[i];
                    for (int d = 0; d < client.Info.Dim; d++)
                    {
                        sim += qVec[d] * dVec[d];
                    }
                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        bestDoc = docNamesList[i];
                    }
                }

                bool pass;
                if (q.ExpectedFilename is null)
                {
                    // Absent query should have lower similarity score (< 0.65)
                    pass = bestSim < 0.65;
                }
                else
                {
                    pass = string.Equals(bestDoc, q.ExpectedFilename, StringComparison.OrdinalIgnoreCase);
                }

                if (pass) hits++;

                details.Add((
                    Query: q.Query,
                    Direction: q.Direction,
                    Expected: q.ExpectedFilename ?? "(absent/none)",
                    Top1: bestDoc,
                    Sim: Math.Round(bestSim, 3),
                    Pass: pass,
                    LatMs: latMs));

                _output.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] '{q.Query}' -> {bestDoc} (sim={bestSim:0.000}, {latMs}ms)");
            }

            double avgLat = Math.Round(queryLatencies.Average(), 1);
            var sortedLats = queryLatencies.OrderBy(x => x).ToList();
            double p95Lat = Math.Round(sortedLats[(int)Math.Min(sortedLats.Count - 1, Math.Floor(sortedLats.Count * 0.95))], 1);
            double accPercent = Math.Round(100.0 * hits / queries.Count, 1);

            _output.WriteLine($"Result: Avg Latency = {avgLat}ms, P95 = {p95Lat}ms, Accuracy = {hits}/{queries.Count} ({accPercent}%)");

            benchmarkResults.Add(new ModelBenchResult(
                ModelName: m.Name,
                ModelId: m.Id,
                Dimension: client.Info.Dim,
                Quantization: m.Quant,
                LoadSeconds: loadSec,
                RamMb: ramMb,
                AvgLatencyMs: avgLat,
                P95LatencyMs: p95Lat,
                AccuracyPercent: accPercent,
                Hits: hits,
                Total: queries.Count,
                Details: details));
        }

        // 4. Generate Markdown report
        var sb = new StringBuilder();
        sb.AppendLine("# 🔬 Отчет бенчмарка моделей эмбеддингов (Release 1.1.0)");
        sb.AppendLine($"**Дата тестирования:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine("**Платформа:** Windows x64 CPU, llama.cpp b11461, WAI Native Engine  ");
        sb.AppendLine("**Тестовый стенд:** `testbed/` (реальный текст TXT/MD/HTML; остальные файлы представлены синтетическими описаниями, без OCR и медиа-извлечения)  ");
        sb.AppendLine("**Датасет валидации:** `testbed-russian-eval.json` (русскоязычные и кросс-язычные запросы, OCR счетов, номера, негативные запросы)\n");
        sb.AppendLine("---");
        sb.AppendLine("\n## 📊 Сводная таблица сравнения моделей на CPU\n");
        sb.AppendLine("| Модель | Размерность (Dim) | Квантование | Время загрузки (сек) | Память RAM (МБ) | Латентность (мс/запрос) | Точность Top-1 (%) |");
        sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (var r in benchmarkResults)
        {
            sb.AppendLine($"| **{r.ModelName}** | {r.Dimension} | {r.Quantization} | {r.LoadSeconds:0.0} s | {r.RamMb:0} MB | **{r.AvgLatencyMs:0.0} ms** | **{r.AccuracyPercent:0.0}%** ({r.Hits}/{r.Total}) |");
        }

        sb.AppendLine("\n---\n");
        sb.AppendLine("## 🔍 Детализированный отчет по запросам тестового стенда\n");

        foreach (var r in benchmarkResults)
        {
            sb.AppendLine($"### {r.ModelName} ({r.Dimension} dim)");
            sb.AppendLine("| Запрос | Направление / Тип | Ожидаемый файл | Top-1 результат (сходство) | Статус |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :---: |");
            foreach (var d in r.Details)
            {
                string statusIcon = d.Pass ? "✅ PASS" : "❌ FAIL";
                sb.AppendLine($"| {d.Query} | {d.Direction} | {d.Expected} | {d.Top1} ({d.Sim:0.000}) | {statusIcon} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("This is a synthetic model comparison over the listed text representations, not an extraction or production-quality acceptance test.");
        sb.AppendLine("No model or hardware recommendation is inferred from this small corpus. See WAI.ReleaseSmoke for file-pipeline checks.");
        string reportPath = Path.Combine(resultsDir, "benchmark-models-report.md");
        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);

        _output.WriteLine($"Report written to {reportPath}");

        // Asserts
        foreach (var r in benchmarkResults)
        {
            double minAcc = r.ModelId == "nomic" ? 50.0 : 70.0;
            Assert.True(r.AccuracyPercent >= minAcc, $"{r.ModelName} accuracy too low: {r.AccuracyPercent}% (want >= {minAcc}%)");
        }
    }
}
