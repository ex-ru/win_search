param(
    [string]$Repo = "ex-ru/win_search",
    [string]$Tag = "latest",
    [string]$OutputDir = "",
    [switch]$Portable = $false,
    [switch]$Full = $false,
    [switch]$IncludeTools = $false,
    [switch]$IncludeModels = $false
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist\releases"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " WAI Release Downloader (Release 1.1.0)" -ForegroundColor Cyan
Write-Host " Repository: $Repo" -ForegroundColor Cyan
Write-Host " Target:     $OutputDir" -ForegroundColor Cyan
Write-Host " Package:    $(if ($Portable) { 'Portable ZIP' } elseif ($Full) { 'Full Offline Setup (~290 MB)' } else { 'Slim Lightweight Setup (~75 MB)' })" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$apiUrl = if ($Tag -eq "latest") {
    "https://api.github.com/repos/$Repo/releases/latest"
} else {
    "https://api.github.com/repos/$Repo/releases/tags/$Tag"
}

Write-Host "Checking GitHub Releases at $apiUrl..." -ForegroundColor Yellow
$headers = @{
    "User-Agent" = "WAI-Downloader/1.1"
    "Accept" = "application/vnd.github.v3+json"
}

try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get
} catch {
    Write-Warning "Could not fetch release metadata from GitHub API: $_"
    Write-Host "`nПрямые ссылки для ручного скачивания в браузере:" -ForegroundColor Cyan
    Write-Host "👉 https://github.com/$Repo/releases`n" -ForegroundColor Yellow
    exit 1
}

$targetPattern = if ($Portable) {
    "*win-Portable.zip"
} elseif ($Full) {
    "*win-Setup-Full.exe"
} else {
    "*win-Setup.exe"
}

$assets = @($release.assets)
$targetAsset = $assets | Where-Object { $_.name -like $targetPattern -or $_.name -like "*$targetPattern*" } | Select-Object -First 1

if (!$targetAsset) {
    # Fallback to any exe or zip asset
    $targetAsset = $assets | Where-Object { $_.name -like "*.exe" -or $_.name -like "*.zip" } | Select-Object -First 1
}

if (!$targetAsset) {
    Write-Error "No matching release asset found in release $($release.tag_name)."
    exit 1
}

$destFile = Join-Path $OutputDir $targetAsset.name
Write-Host "Found release $($release.tag_name): $($targetAsset.name) ($([Math]::Round($targetAsset.size / 1MB, 2)) MB)" -ForegroundColor Green
Write-Host "Downloading to $destFile..." -ForegroundColor Yellow

$wc = New-Object System.Net.WebClient
$wc.Headers.Add("User-Agent", "WAI-Downloader/1.0")
$wc.DownloadFile($targetAsset.browser_download_url, $destFile)

Write-Host "Download complete: $destFile" -ForegroundColor Green

# Also download checksums and manifests if available
foreach ($metaName in @("SHA256SUMS.txt", "releases.win.json", "RELEASES")) {
    $metaAsset = $assets | Where-Object { $_.name -eq $metaName } | Select-Object -First 1
    if ($metaAsset) {
        $metaPath = Join-Path $OutputDir $metaName
        try {
            $wc.DownloadFile($metaAsset.browser_download_url, $metaPath)
            Write-Host "Downloaded metadata: $metaName" -ForegroundColor Gray
        } catch { }
    }
}

# Verify SHA256 if SHA256SUMS.txt exists
$sumsFile = Join-Path $OutputDir "SHA256SUMS.txt"
if (Test-Path $sumsFile) {
    $expectedHash = ""
    foreach ($line in Get-Content $sumsFile) {
        $parts = $line -split '\s+'
        if ($parts.Length -ge 2 -and $parts[1] -eq $targetAsset.name) {
            $expectedHash = $parts[0].Trim()
            break
        }
    }
    if ($expectedHash) {
        $actualHash = (Get-FileHash -Path $destFile -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -eq $expectedHash.ToLowerInvariant()) {
            Write-Host "SHA256 verification passed: $actualHash" -ForegroundColor Green
        } else {
            Write-Warning "SHA256 verification mismatch! Expected $expectedHash, got $actualHash"
        }
    }
}

if ($IncludeTools) {
    $toolsAsset = $assets | Where-Object { $_.name -like "*ffmpeg-tools*" } | Select-Object -First 1
    if ($toolsAsset) {
        $toolsDest = Join-Path $OutputDir $toolsAsset.name
        Write-Host "Downloading on-demand media tools: $($toolsAsset.name)..." -ForegroundColor Yellow
        $wc.DownloadFile($toolsAsset.browser_download_url, $toolsDest)
        Write-Host "Media tools package saved to: $toolsDest" -ForegroundColor Green
    }
}

Write-Host "`nГотово! Вы можете запустить установщик:" -ForegroundColor Cyan
Write-Host "  $destFile`n" -ForegroundColor Yellow
