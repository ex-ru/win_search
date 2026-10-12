# Build and package DigUp for Windows (Velopack Setup.exe + Portable zip)
# PowerShell 5.1 & Core safe.
param(
    [string]$Version = "1.0.0",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$WindowsDir = Split-Path -Parent $ScriptDir

$RepoRoot = $WindowsDir
while ($RepoRoot -and !(Test-Path (Join-Path $RepoRoot ".git"))) {
    $parent = Split-Path -Parent $RepoRoot
    if ($parent -eq $RepoRoot) { break }
    $RepoRoot = $parent
}
if (!(Test-Path (Join-Path $RepoRoot ".git"))) {
    $RepoRoot = "D:\_projects\win_search"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist"
}

$StagingDir = Join-Path $OutputDir "staging"
$ReleasesDir = Join-Path $OutputDir "releases"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building DigUp Windows Distribution v$Version" -ForegroundColor Cyan
Write-Host " Output: $OutputDir" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Locate .NET 9 SDK
$DotnetExe = "dotnet"
if (Test-Path (Join-Path $RepoRoot "tools\dotnet\dotnet.exe")) {
    $DotnetRoot = Join-Path $RepoRoot "tools\dotnet"
    $DotnetExe = Join-Path $DotnetRoot "dotnet.exe"
    $env:DOTNET_ROOT = $DotnetRoot
    $env:PATH = "$DotnetRoot;$env:PATH"
    Write-Host "Using bundled .NET SDK: $DotnetExe" -ForegroundColor Green
}

# 2. Clean staging and output directories
if (Test-Path $StagingDir) {
    Remove-Item -Recurse -Force $StagingDir
}
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
New-Item -ItemType Directory -Force -Path $ReleasesDir | Out-Null

# 3. Publish Self-Contained Applications
function Publish-Project($ProjectRelPath, $Name) {
    $Proj = Join-Path $WindowsDir $ProjectRelPath
    Write-Host "Publishing $Name ($Proj)..." -ForegroundColor Yellow
    & $DotnetExe publish $Proj -c Release -r win-x64 --self-contained -o $StagingDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $Name"
    }
}

Publish-Project "src\DigUp.App\DigUp.App.csproj" "DigUpApp (WPF GUI)"
Publish-Project "src\digup\digup.csproj" "digup (CLI)"
Publish-Project "src\DigUp.Embed\DigUp.Embed.csproj" "digup-embed (Worker)"

# 4. Copy native runtime libraries
Write-Host "Copying native runtime binaries..." -ForegroundColor Yellow
$RunVulkan = Join-Path $RepoRoot "run-vulkan"
if (Test-Path $RunVulkan) {
    # Copy GGML, Vulkan, and llama.cpp native DLLs
    Get-ChildItem -Path $RunVulkan -Filter "*.dll" | ForEach-Object {
        $dest = Join-Path $StagingDir $_.Name
        if (!(Test-Path $dest)) {
            Copy-Item $_.FullName $dest -Force
        }
    }
}

# Copy native tools (ffmpeg, ffprobe) if available
$FfmpegTools = Join-Path $RepoRoot "tools\ffmpeg"
if (Test-Path $FfmpegTools) {
    Get-ChildItem -Path $FfmpegTools -Filter "*.exe" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $StagingDir $_.Name) -Force
    }
}

# 5. Verification of packaged payload
$RequiredFiles = @("DigUpApp.exe", "digup.exe", "digup-embed.exe", "DigUp.Core.dll")
foreach ($req in $RequiredFiles) {
    if (!(Test-Path (Join-Path $StagingDir $req))) {
        throw "Missing required packaged file: $req in $StagingDir"
    }
}
Write-Host "Payload validation passed." -ForegroundColor Green

# 6. Create Velopack Setup Package & Portable Distribution
$VpkExe = "vpk"
$UserVpk = "$env:USERPROFILE\.dotnet\tools\vpk.exe"
if (Test-Path $UserVpk) {
    $VpkExe = $UserVpk
}

try {
    Write-Host "Building Velopack Setup.exe installer and Portable bundle via $VpkExe..." -ForegroundColor Yellow
    & $VpkExe pack `
        --packId "DigUp" `
        --packVersion $Version `
        --runtime "win-x64" `
        --packTitle "DigUp" `
        --packAuthors "DigUp Contributors" `
        --packDir $StagingDir `
        --mainExe "DigUpApp.exe" `
        --outputDir $ReleasesDir `
        --shortcuts "Desktop,StartMenuRoot" `
        --skipVeloAppCheck `
        --yes

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Velopack package generated successfully in $ReleasesDir" -ForegroundColor Green
    } else {
        Write-Warning "Velopack pack returned exit code $LASTEXITCODE."
    }
} catch {
    Write-Warning "Velopack pack failed: $_"
}

# 8. Compute SHA256 Checksums
Write-Host "Computing SHA256 hashes..." -ForegroundColor Yellow
$HashFile = Join-Path $ReleasesDir "SHA256SUMS.txt"
$Hashes = @()
Get-ChildItem -Path $ReleasesDir -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | ForEach-Object {
    $h = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $Hashes += "$h  $($_.Name)"
}
$Hashes | Out-File -FilePath $HashFile -Encoding utf8
Write-Host "Release checksums written to $($HashFile):" -ForegroundColor Cyan
$Hashes | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Build & Packaging Complete!" -ForegroundColor Green
Write-Host " Releases directory: $ReleasesDir" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
