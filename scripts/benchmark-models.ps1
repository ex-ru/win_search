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

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " DigUp AI Embedding Models Benchmark Suite (Release 1.1.0)" -ForegroundColor Cyan
Write-Host " Models: EmbeddingGemma 2 | BGE-M3 | Nomic Embed v1.5" -ForegroundColor Cyan
Write-Host " Target Platform: Windows x64 CPU" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$Dotnet = "dotnet"
if (Test-Path (Join-Path $RepoRoot "tools\dotnet\dotnet.exe")) {
    $Dotnet = Join-Path $RepoRoot "tools\dotnet\dotnet.exe"
}

$TestProj = Join-Path $RepoRoot "port\DigUp-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\src\DigUp.Tests\DigUp.Tests.csproj"

& $Dotnet test $TestProj --filter "FullyQualifiedName~ModelBenchmark" --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Benchmark tests failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$ReportPath = Join-Path $OutputDir "benchmark-models-report.md"
if (Test-Path $ReportPath) {
    Write-Host "`n==========================================================" -ForegroundColor Green
    Write-Host " Benchmark Completed Successfully!" -ForegroundColor Green
    Write-Host " Report written to: $ReportPath" -ForegroundColor Cyan
    Write-Host "==========================================================`n" -ForegroundColor Green
    Get-Content $ReportPath | Select-Object -First 20 | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
}
