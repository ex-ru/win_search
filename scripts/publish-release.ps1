# Publish DigUp Windows distribution packages to GitHub Releases
# PowerShell 5.1 & Core safe.
param(
    [string]$Version = "1.1.0",
    [string]$Repo = "ex-ru/win_search",
    [string]$ReleasesDir = "",
    [string]$Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleasesDir)) {
    $ReleasesDir = Join-Path $RepoRoot "dist\releases"
}
$ReleasesDir = [System.IO.Path]::GetFullPath($ReleasesDir)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " DigUp Release Publisher" -ForegroundColor Cyan
Write-Host " Version:     $Version" -ForegroundColor Cyan
Write-Host " Repository:  $Repo" -ForegroundColor Cyan
Write-Host " Source:      $ReleasesDir" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

if (!(Test-Path $ReleasesDir)) {
    Write-Error "Directory $ReleasesDir does not exist. Run build-package.ps1 first."
    exit 1
}

$filesToUpload = @(Get-ChildItem -Path $ReleasesDir -File | Where-Object {
    $_.Name -like "*.exe" -or $_.Name -like "*.zip" -or $_.Name -like "*.nupkg" -or
    $_.Name -in @("releases.win.json", "RELEASES", "SHA256SUMS.txt")
})

if ($filesToUpload.Count -eq 0) {
    Write-Error "No release artifacts found in $ReleasesDir."
    exit 1
}

Write-Host "Found $($filesToUpload.Count) files to publish:" -ForegroundColor Yellow
$filesToUpload | ForEach-Object {
    $sizeMb = [Math]::Round($_.Length / 1MB, 2)
    Write-Host " - $($_.Name) ($sizeMb MB)" -ForegroundColor Gray
}

# Check for GitHub CLI (gh)
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh) {
    Write-Host "`nUsing GitHub CLI (gh)..." -ForegroundColor Green
    $tag = "v$Version"
    $filePaths = $filesToUpload | ForEach-Object { $_.FullName }
    $title = "DigUp v$Version (Windows)"
    $notes = "DigUp v$Version Windows Release with universal hardware acceleration (CPU, NVIDIA CUDA/Vulkan, AMD Radeon Vulkan, Intel Arc/Iris Xe Vulkan)."

    Write-Host "Creating release $tag..." -ForegroundColor Yellow
    & gh release create $tag @filePaths --repo $Repo --title $title --notes $notes
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Successfully published release $tag to https://github.com/$Repo/releases/tag/$tag" -ForegroundColor Green
        exit 0
    }
}

# REST API upload with GITHUB_TOKEN
if (![string]::IsNullOrWhiteSpace($Token)) {
    Write-Host "`nPublishing via GitHub REST API with GITHUB_TOKEN..." -ForegroundColor Green
    $tag = "v$Version"
    $headers = @{
        "Authorization" = "token $Token"
        "Accept" = "application/vnd.github.v3+json"
        "User-Agent" = "DigUp-Publisher/1.0"
    }

    $releaseBody = @{
        tag_name = $tag
        name = "DigUp v$Version (Windows)"
        body = "DigUp v$Version Windows Release with universal hardware acceleration (CPU, NVIDIA CUDA/Vulkan, AMD Radeon Vulkan, Intel Arc/Iris Xe Vulkan)."
        draft = $false
        prerelease = $false
    } | ConvertTo-Json

    $releaseUrl = "https://api.github.com/repos/$Repo/releases"
    $created = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -Method Post -Body $releaseBody -ContentType "application/json"
    $uploadBase = $created.upload_url -replace '\{\?name,label\}', ''

    foreach ($file in $filesToUpload) {
        $uploadUrl = "$uploadBase?name=$($file.Name)"
        Write-Host "Uploading $($file.Name)..." -ForegroundColor Yellow
        $uploadHeaders = @{
            "Authorization" = "token $Token"
            "Content-Type" = "application/octet-stream"
            "User-Agent" = "DigUp-Publisher/1.0"
        }
        Invoke-RestMethod -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $file.FullName | Out-Null
    }

    Write-Host "Successfully published release $tag via API!" -ForegroundColor Green
    exit 0
}

# Manual Instructions
Write-Host "`n----------------------------------------------------------" -ForegroundColor Yellow
Write-Host "Для загрузки релиза на GitHub выполните одно из действий:" -ForegroundColor Cyan
Write-Host "1. Установите GitHub CLI (winget install GitHub.cli) и запустите:" -ForegroundColor White
Write-Host "   gh auth login" -ForegroundColor Gray
Write-Host "   .\scripts\publish-release.ps1 -Version $Version" -ForegroundColor Gray
Write-Host "`n2. Либо установите переменную окружения с персональным токеном GitHub:" -ForegroundColor White
Write-Host "   `$env:GITHUB_TOKEN = 'ghp_...'" -ForegroundColor Gray
Write-Host "   .\scripts\publish-release.ps1 -Version $Version" -ForegroundColor Gray
Write-Host "`n3. Либо откройте веб-интерфейс создания релиза в браузере:" -ForegroundColor White
Write-Host "   👉 https://github.com/$Repo/releases/new" -ForegroundColor Yellow
Write-Host "   И перетащите файлы из папки:" -ForegroundColor White
Write-Host "   $ReleasesDir" -ForegroundColor Cyan
Write-Host "----------------------------------------------------------`n" -ForegroundColor Yellow
