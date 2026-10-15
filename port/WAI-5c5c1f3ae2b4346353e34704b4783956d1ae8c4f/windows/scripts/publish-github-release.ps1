<#
.SYNOPSIS
    Публикация дистрибутивов WAI в GitHub Releases (v1.0.4).

.DESCRIPTION
    Скрипт проверяет наличие готовых бинарных файлов в dist/releases/,
    рассчитывает контрольные суммы SHA256 и создает GitHub Release с прикрепленными
    установочным пакетом (Setup.exe), переносимым архивом (.zip) и метаданными Velopack.

.EXAMPLE
    .\publish-github-release.ps1 -Version "1.0.4"
#>

[CmdletBinding()]
param(
    [string]$Version = "1.2.0",
    [string]$Tag = "v$Version",
    [string]$Repo = "ex-ru/win_search",
    [switch]$Draft,
    [switch]$Prerelease
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Resolve-Path "$ScriptDir\..\..\..\.."
$ReleasesDir = Join-Path $ProjectRoot "dist\releases"

Write-Host "=== Публикация релиза WAI Windows ($Tag) ===" -ForegroundColor Cyan
Write-Host "Корневой каталог: $ProjectRoot"
Write-Host "Каталог релизов:  $ReleasesDir"

if (-not (Test-Path $ReleasesDir)) {
    throw "Каталог $ReleasesDir не найден. Сначала соберите пакеты через build-package.ps1."
}

$SetupExe = Join-Path $ReleasesDir "WinSearch.WAI-win-Setup.exe"
$PortableZip = Join-Path $ReleasesDir "WinSearch.WAI-win-Portable.zip"
foreach ($requiredAsset in @($SetupExe, $PortableZip)) {
    if (-not (Test-Path -LiteralPath $requiredAsset -PathType Leaf)) {
        throw "Required WAI release asset is missing: $requiredAsset"
    }
}
$ReleasesJson = Join-Path $ReleasesDir "releases.win.json"
$ShaFile = Join-Path $ReleasesDir "SHA256SUMS.txt"

$AssetsToUpload = @()
if (Test-Path $SetupExe) { $AssetsToUpload += $SetupExe }
if (Test-Path $PortableZip) { $AssetsToUpload += $PortableZip }
if (Test-Path $ReleasesJson) { $AssetsToUpload += $ReleasesJson }
if (Test-Path $ShaFile) { $AssetsToUpload += $ShaFile }

# Also attach the current package's full update payload.
$FullNupkg = Get-ChildItem -Path $ReleasesDir -Filter "WinSearch.WAI-$Version-full.nupkg" | Select-Object -First 1
if ($FullNupkg) { $AssetsToUpload += $FullNupkg.FullName }

Write-Host "`nФайлы для публикации в релизе:" -ForegroundColor Yellow
foreach ($asset in $AssetsToUpload) {
    $sizeMb = [math]::Round((Get-Item $asset).Length / 1MB, 2)
    Write-Host "  - $(Split-Path -Leaf $asset) ($sizeMb MB)"
}

$ReleaseNotes = @"
## WAI for Windows v$Version

### Изменения
- Установщик и обновления используют идентификатор пакета `WinSearch.WAI`.
- Полный пакет со встроенным FFmpeg теперь является вариантом по умолчанию.
- Данные WAI в `%LocalAppData%\WAI` сохраняются при обновлении приложения. Переход с установок, использующих другой идентификатор пакета, автоматически не гарантируется.

Установщик: `WinSearch.WAI-win-Setup.exe`

Портативная версия: `WinSearch.WAI-win-Portable.zip`
"@

# Проверяем наличие GitHub CLI (gh)
$gh = Get-Command "gh" -ErrorAction SilentlyContinue
if ($gh) {
    Write-Host "`nСоздание GitHub Release через gh CLI..." -ForegroundColor Green
    $notesPath = Join-Path ([System.IO.Path]::GetTempPath()) "wai-release-notes-$PID-$([guid]::NewGuid().ToString('N')).md"
    try {
        [System.IO.File]::WriteAllText($notesPath, $ReleaseNotes, (New-Object System.Text.UTF8Encoding($false)))
        $ghArgs = @("release", "create", $Tag, "--repo", $Repo, "--title", "WAI Windows $Tag", "--notes-file", $notesPath)
        if ($Draft) { $ghArgs += "--draft" }
        if ($Prerelease) { $ghArgs += "--prerelease" }
        foreach ($a in $AssetsToUpload) {
            $ghArgs += $a
        }
        & gh @ghArgs
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub CLI failed to publish release $Tag (exit code $LASTEXITCODE)."
        }
        Write-Host "Релиз $Tag успешно опубликован в $Repo!" -ForegroundColor Green
    } finally {
        if (Test-Path -LiteralPath $notesPath) { Remove-Item -LiteralPath $notesPath -Force }
    }
} else {
    Write-Host "`nGitHub CLI ('gh') не установлен в системе." -ForegroundColor Yellow
    Write-Host "Вы можете загрузить релиз вручную на странице: https://github.com/$Repo/releases/new?tag=$Tag"
    Write-Host "И прикрепить файлы из $ReleasesDir"
}
