param(
    [string]$Repo = "ex-ru/win_search",
    [string]$Tag = "latest",
    [string]$OutputDir = "",
    [switch]$Portable = $false
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist/downloads"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$targetName = if ($Portable) { "WinSearch.WAI-win-Portable.zip" } else { "WinSearch.WAI-win-Setup.exe" }
$apiUrl = if ($Tag -eq "latest") {
    "https://api.github.com/repos/$Repo/releases/latest"
} else {
    "https://api.github.com/repos/$Repo/releases/tags/$Tag"
}
$headers = @{ "User-Agent" = "WAI-Downloader/1.2.4"; "Accept" = "application/vnd.github.v3+json" }
try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get
} catch {
    if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -in @(401,404)) {
        throw "Release is not accessible anonymously. For a private repository, open https://github.com/$Repo/releases in a signed-in browser with repository access. This script is for public release channels."
    }
    throw
}
$targetAsset = @($release.assets | Where-Object { $_.name -eq $targetName } | Select-Object -First 1)
if ($targetAsset.Count -eq 0) {
    throw "Release $($release.tag_name) does not contain the expected WAI asset $targetName. Review the release assets before downloading."
}
$targetAsset = $targetAsset[0]
$checksumAsset = @($release.assets | Where-Object { $_.name -eq "SHA256SUMS.txt" } | Select-Object -First 1)
if ($checksumAsset.Count -eq 0) {
    throw "Release $($release.tag_name) does not provide SHA256SUMS.txt; the package cannot be verified."
}
$checksumAsset = $checksumAsset[0]

Write-Host "WAI Release Downloader 1.2.4" -ForegroundColor Cyan
Write-Host "Release: $($release.tag_name)" -ForegroundColor Cyan
Write-Host "Package: $(if ($Portable) { 'Full portable package' } else { 'Full setup with FFmpeg bundled' })" -ForegroundColor Cyan

$destFile = Join-Path $OutputDir $targetAsset.name
$sumsFile = Join-Path $OutputDir "SHA256SUMS.txt"
$wc = New-Object System.Net.WebClient
try {
    $wc.Headers.Add("User-Agent", "WAI-Downloader/1.2.4")
    $wc.DownloadFile($targetAsset.browser_download_url, $destFile)
    $wc.DownloadFile($checksumAsset.browser_download_url, $sumsFile)
    foreach ($metaName in @("releases.win.json", "RELEASES")) {
        $metaAsset = $release.assets | Where-Object { $_.name -eq $metaName } | Select-Object -First 1
        if ($metaAsset) {
            try { $wc.DownloadFile($metaAsset.browser_download_url, (Join-Path $OutputDir $metaName)) } catch { }
        }
    }
} finally {
    $wc.Dispose()
}

$expectedHash = ""
foreach ($line in Get-Content -LiteralPath $sumsFile) {
    $parts = $line -split '\s+'
    if ($parts.Length -ge 2) {
        $checksumName = $parts[1].Trim()
        if ($checksumName.StartsWith('*')) { $checksumName = $checksumName.Substring(1) }
        if ($checksumName -eq $targetAsset.name) {
            $expectedHash = $parts[0].Trim()
            break
        }
    }
}
if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$') {
    throw "SHA256SUMS.txt does not contain a valid checksum for $($targetAsset.name)."
}
$actualHash = (Get-FileHash -LiteralPath $destFile -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) {
    throw "SHA-256 verification failed for $($targetAsset.name). Expected $expectedHash, got $actualHash."
}

Write-Host "SHA-256 verification passed: $actualHash" -ForegroundColor Green
Write-Host "Download complete and verified: $destFile" -ForegroundColor Green
Write-Host "Review the release source and version before running the installer." -ForegroundColor Cyan
