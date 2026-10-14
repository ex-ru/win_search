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
    [string]$Version = "1.1.0",
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
$ReleasesJson = Join-Path $ReleasesDir "releases.win.json"
$ShaFile = Join-Path $ReleasesDir "SHA256SUMS.txt"

$AssetsToUpload = @()
if (Test-Path $SetupExe) { $AssetsToUpload += $SetupExe }
if (Test-Path $PortableZip) { $AssetsToUpload += $PortableZip }
if (Test-Path $ReleasesJson) { $AssetsToUpload += $ReleasesJson }
if (Test-Path $ShaFile) { $AssetsToUpload += $ShaFile }

# Также добавляем full.nupkg для автообновлений Velopack
$FullNupkg = Get-ChildItem -Path $ReleasesDir -Filter "*-$Version-full.nupkg" | Select-Object -First 1
if ($FullNupkg) { $AssetsToUpload += $FullNupkg.FullName }

Write-Host "`nФайлы для публикации в релизе:" -ForegroundColor Yellow
foreach ($asset in $AssetsToUpload) {
    $sizeMb = [math]::Round((Get-Item $asset).Length / 1MB, 2)
    Write-Host "  - $(Split-Path -Leaf $asset) ($sizeMb MB)"
}

$ReleaseNotes = @"
## WAI Windows Port v$Version

### Что нового:
1. **Дистрибутивы и установка**:
   - Автономный установщик: `WinSearch.WAI-win-Setup.exe` (per-user, no-admin, автозапуск).
   - Портативная версия: `WinSearch.WAI-win-Portable.zip` (распакуй и запусти).
   - Поддержка отслеживания больших пакетов через Git LFS.
2. **Окно предпросмотра (Quick Look)**:
   - Устранена полоса в заголовке окна (прозрачный DWM-фрейм).
   - Добавлены стандартные оконные кнопки: Свернуть, Развернуть, Закрыть.
   - Добавлено меню «Файл»: Открыть, В проводнике (с выделением файла), Сохранить как…, Копировать путь.
   - Возможность открывать несколько окон предпросмотра одновременно.
3. **Корректные кодировки текста**:
   - Автодетектор кодировок: Windows-1251 (CP1251), CP866, UTF-8 с/без BOM.
   - Устранены ромбики (\uFFFD) при предпросмотре и индексации кириллических файлов.
4. **Удобство горячих клавиш**:
   - Клавиша \`Esc\` теперь надежно сворачивает приложение из любого состояния (поиск, выдача, скролл).
   - Возврат фокуса после закрытия предпросмотра.
5. **Настройки и трей**:
   - Увеличенная высота окна настроек (780px) и темный дизайн в едином стиле приложения.
   - Кнопка очистки векторного индекса по выбранным папкам.
   - В контекстном меню трея: раздел «О программе (About)» со ссылкой на GitHub.
   - В трее рядом с галкой «Индексация» отображается динамический прогресс: сколько проиндексировано и сколько осталось.
6. **Открытие в Проводнике**:
   - Команда «В проводнике» открывает целевую папку с автоматическим выделением файла (\`explorer.exe /select\`).
"@

# Проверяем наличие GitHub CLI (gh)
$gh = Get-Command "gh" -ErrorAction SilentlyContinue
if ($gh) {
    Write-Host "`nСоздание GitHub Release через gh CLI..." -ForegroundColor Green
    $ghArgs = @("release", "create", $Tag, "--repo", $Repo, "--title", "WAI Windows $Tag", "--notes", $ReleaseNotes)
    if ($Draft) { $ghArgs += "--draft" }
    if ($Prerelease) { $ghArgs += "--prerelease" }
    foreach ($a in $AssetsToUpload) {
        $ghArgs += $a
    }
    & gh @ghArgs
    Write-Host "Релиз $Tag успешно опубликован в $Repo!" -ForegroundColor Green
} else {
    Write-Host "`nGitHub CLI ('gh') не установлен в системе." -ForegroundColor Yellow
    Write-Host "Вы можете загрузить релиз вручную на странице: https://github.com/$Repo/releases/new?tag=$Tag"
    Write-Host "И прикрепить файлы из $ReleasesDir"
}
