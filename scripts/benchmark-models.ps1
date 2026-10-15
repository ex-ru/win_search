# benchmark-models.ps1 - Comparative Inference Speed and Retrieval Accuracy Benchmark
# Evaluates EmbeddingGemma 2, BGE-M3, and Nomic Embed v1.5 on CPU.

param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "results"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " WAI AI Embedding Models Benchmark Suite (Release 1.2.0)" -ForegroundColor Cyan
Write-Host " Models: EmbeddingGemma 2 | BGE-M3 | Nomic Embed v1.5" -ForegroundColor Cyan
Write-Host " Target Platform: Windows x64 CPU" -ForegroundColor Cyan
Write-Host " Results are specific to this benchmark fixture and machine, not product guarantees." -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

$Dotnet = "dotnet"
if (Test-Path (Join-Path $RepoRoot "tools\dotnet\dotnet.exe")) {
    $Dotnet = Join-Path $RepoRoot "tools\dotnet\dotnet.exe"
}

$TestProj = Join-Path $RepoRoot "port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\src\WAI.Tests\WAI.Tests.csproj"

$PreviousOptIn = $env:WAI_RUN_MODEL_BENCHMARKS
$PreviousOutput = $env:WAI_BENCHMARK_OUTPUT_DIR
$TestResults = Join-Path $OutputDir ('test-results-' + [Guid]::NewGuid().ToString('N'))
try {
    $env:WAI_RUN_MODEL_BENCHMARKS = '1'
    $env:WAI_BENCHMARK_OUTPUT_DIR = $OutputDir
    & $Dotnet test $TestProj --filter "FullyQualifiedName~ModelBenchmark" --logger "console;verbosity=normal" --logger "trx;LogFileName=model-benchmarks.trx" --results-directory $TestResults
    $BenchmarkExitCode = $LASTEXITCODE
} finally {
    [Environment]::SetEnvironmentVariable('WAI_RUN_MODEL_BENCHMARKS', $PreviousOptIn, 'Process')
    [Environment]::SetEnvironmentVariable('WAI_BENCHMARK_OUTPUT_DIR', $PreviousOutput, 'Process')
}
if ($BenchmarkExitCode -ne 0) { throw "Benchmark tests failed with exit code $BenchmarkExitCode" }
[xml]$TestReport = Get-Content -LiteralPath (Join-Path $TestResults 'model-benchmarks.trx') -Raw
$Counters = $TestReport.TestRun.ResultSummary.Counters
if ([int]$Counters.executed -lt 1 -or [int]$Counters.passed -ne [int]$Counters.executed -or [int]$Counters.notExecuted -gt 0) {
    throw 'Benchmark did not execute and pass all required integration tests; skipped tests are not success.'
}

$ReportPath = Join-Path $OutputDir "benchmark-models-report.md"
if (!(Test-Path -LiteralPath $ReportPath -PathType Leaf)) { throw "Benchmark report was not created in requested OutputDir: $ReportPath" }
if (Test-Path $ReportPath) {
    Write-Host "`n==========================================================" -ForegroundColor Green
    Write-Host " Benchmark Completed Successfully!" -ForegroundColor Green
    Write-Host " Report written to: $ReportPath" -ForegroundColor Cyan
    Write-Host "==========================================================`n" -ForegroundColor Green
    Get-Content $ReportPath | Select-Object -First 20 | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
}
