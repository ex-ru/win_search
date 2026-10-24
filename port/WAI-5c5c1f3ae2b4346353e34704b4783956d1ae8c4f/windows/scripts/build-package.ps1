# Build and package WAI (Windows AI Search) for Windows (Velopack Setup.exe + Portable zip)
# PowerShell 5.1 & Core safe.
param(
    [string]$Version = "1.2.6",
    [string]$OutputDir = "",
    [switch]$Slim = $false,
    [switch]$Full = $false
)

if ($Slim -and $Full) {
    throw "Choose either -Slim or -Full, not both."
}

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
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $WindowsDir)
}
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist\$Version"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

$separatorChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$outputWithoutTrailingSeparators = $OutputDir.TrimEnd($separatorChars)
$rootWithoutTrailingSeparators = [System.IO.Path]::GetPathRoot($OutputDir).TrimEnd($separatorChars)
if ($outputWithoutTrailingSeparators.Equals($rootWithoutTrailingSeparators, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir cannot be a filesystem root: $OutputDir"
}
$outputPrefix = $outputWithoutTrailingSeparators + [System.IO.Path]::DirectorySeparatorChar
$StagingDir = [System.IO.Path]::GetFullPath((Join-Path $OutputDir "staging"))
if (!$StagingDir.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use staging outside OutputDir: $StagingDir"
}
$ReleasesDir = Join-Path $OutputDir "releases"

$LocalDotnetHome = Join-Path $RepoRoot "tools\dotnet-home"
$LocalNugetPackages = Join-Path $RepoRoot "tools\nuget"
New-Item -ItemType Directory -Force -Path $LocalDotnetHome | Out-Null
New-Item -ItemType Directory -Force -Path $LocalNugetPackages | Out-Null
$env:DOTNET_CLI_HOME = $LocalDotnetHome
$env:NUGET_PACKAGES = $LocalNugetPackages

$NativeRoot = Join-Path $RepoRoot "tools\runtime-win-x64"
& (Join-Path $RepoRoot 'scripts\prepare-runtime.ps1') -OutputDir $NativeRoot
$NativeBin = Join-Path $NativeRoot 'bin'
$NativeLicenses = Join-Path $NativeRoot 'licenses'
$NativeManifestPath = Join-Path $NativeRoot 'native-provenance.json'
$NativeManifest = Get-Content -LiteralPath $NativeManifestPath -Raw | ConvertFrom-Json
foreach ($File in $NativeManifest.files) {
    $Path = Join-Path $NativeBin $File.name
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $File.sha256) { throw "Prepared native file changed: $($File.name)" }
}
$RequiredNativeDlls = @("llama.dll", "mtmd.dll", "ggml.dll", "ggml-base.dll", "ggml-vulkan.dll", "ggml-cpu-x64.dll", "libomp.dll", "pdfium.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")
$RequiredFfmpegTools = @("ffmpeg.exe", "ffprobe.exe")
$RepoLicenseSource = Join-Path $RepoRoot 'LICENSE'
if ($Slim) { throw 'Slim packages are no longer supported. The standard package includes verified media tools and their notices.' }

$NuGetConfig = Join-Path $RepoRoot "NuGet.Config"
if (!(Test-Path -LiteralPath $NuGetConfig -PathType Leaf)) {
    $NuGetConfig = $null
}

$VersionSource = Get-Content -LiteralPath (Join-Path $WindowsDir 'src\WAI.App\AppConstants.cs') -Raw
if ($VersionSource -notmatch ('Version\s*=\s*"' + [regex]::Escape($Version) + '"')) { throw 'Package version must match AppConstants.Version.' }
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building WAI (Windows AI Search) Windows Distribution v$Version" -ForegroundColor Cyan
Write-Host " Output: $OutputDir" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Locate the SDK pinned in global.json.
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
    $stagingItem = Get-Item -LiteralPath $StagingDir -Force
    if (($stagingItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to remove staging reparse point: $StagingDir"
    }
    Remove-Item -LiteralPath $StagingDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
New-Item -ItemType Directory -Force -Path $ReleasesDir | Out-Null

# 3. Publish Self-Contained Applications
function Publish-Project($ProjectRelPath, $Name) {
    $Proj = Join-Path $WindowsDir $ProjectRelPath
    Write-Host "Publishing $Name ($Proj)..." -ForegroundColor Yellow
    $PublishArgs = @("publish", $Proj, "-c", "Release", "-r", "win-x64", "--self-contained",
        "-o", $StagingDir, "--nologo", "-v", "quiet", "-p:RestoreLockedMode=true", "-warnaserror:NU1903,NU1904")
    if ($NuGetConfig) {
        $PublishArgs += "-p:RestoreConfigFile=$NuGetConfig"
    }
    & $DotnetExe @PublishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $Name"
    }
}

Publish-Project "src\WAI.App\WAI.App.csproj" "WAIApp (WPF GUI)"
Publish-Project "src\wai\wai.csproj" "wai (CLI)"
Publish-Project "src\WAI.Embed\WAI.Embed.csproj" "wai-embed (Worker)"

# 4. Copy only the verified native runtime and its complete upstream notices.
foreach ($File in $NativeManifest.files) {
    Copy-Item -LiteralPath (Join-Path $NativeBin $File.name) -Destination (Join-Path $StagingDir $File.name) -Force
}
$LicenseTargetDir = Join-Path $StagingDir 'licenses'
New-Item -ItemType Directory -Force -Path $LicenseTargetDir | Out-Null
Copy-Item -Path (Join-Path $NativeLicenses '*') -Destination $LicenseTargetDir -Recurse -Force
Copy-Item -LiteralPath $RepoLicenseSource -Destination (Join-Path $LicenseTargetDir 'LICENSE-WAI.txt') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'THIRD_PARTY_NOTICES.md') -Destination $LicenseTargetDir -Force
Copy-Item -LiteralPath $NativeManifestPath -Destination (Join-Path $StagingDir 'native-provenance.json') -Force
$ManagedNoticeSource = Join-Path $OutputDir 'managed-notices'
& (Join-Path $RepoRoot 'scripts\collect-managed-notices.ps1') -PublishDir $StagingDir -NuGetPackages $LocalNugetPackages -OutputDir $ManagedNoticeSource
$ManagedNoticeTarget = Join-Path $LicenseTargetDir 'managed'
New-Item -ItemType Directory -Force -Path $ManagedNoticeTarget | Out-Null
Copy-Item -Path (Join-Path $ManagedNoticeSource '*') -Destination $ManagedNoticeTarget -Recurse -Force
$UpdaterNoticeSource = Join-Path $RepoRoot 'licenses\velopack-runtime'
$UpdaterNoticeIndex = Get-Content -LiteralPath (Join-Path $UpdaterNoticeSource 'index.json') -Raw | ConvertFrom-Json
if ($UpdaterNoticeIndex.unresolved_count -ne 0 -or $UpdaterNoticeIndex.git_dependency_count -ne 0) { throw 'Release blocked: unresolved updater notices.' }
$UpdaterNoticeTarget = Join-Path $LicenseTargetDir 'velopack-runtime'
New-Item -ItemType Directory -Force -Path $UpdaterNoticeTarget | Out-Null
Copy-Item -Path (Join-Path $UpdaterNoticeSource '*') -Destination $UpdaterNoticeTarget -Recurse -Force
$ManagedNotices = Get-Content -LiteralPath (Join-Path $LicenseTargetDir 'managed\index.json') -Raw | ConvertFrom-Json
if ($ManagedNotices.unresolved_count -ne 0) { throw 'Release blocked: unresolved managed dependency notices.' }
if (@($ManagedNotices.runtime_notice_check.missing_from_runtime_pack_cache).Count -gt 0) { throw 'Release blocked: missing .NET runtime notices.' }

# 5. Require the exact payload before packaging.
$RequiredFiles = @('WAIApp.exe', 'wai.exe', 'wai-embed.exe', 'WAI.Core.dll', 'e_sqlite3.dll',
    'licenses\LICENSE-WAI.txt', 'licenses\llama\LICENSE', 'licenses\llama\LICENSE-LLVM-OpenMP',
    'licenses\pdfium\pdfium.txt', 'licenses\ffmpeg\COPYING.LGPLv2.1', 'licenses\managed\index.json',
    'licenses\vcruntime\index.json', 'licenses\vcruntime\LICENSE-Microsoft-VC-Runtime.docx', 'native-provenance.json') + $RequiredNativeDlls + $RequiredFfmpegTools
foreach ($File in $RequiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $StagingDir $File) -PathType Leaf)) { throw "Missing required package file: $File" }
}
Write-Host 'Verified native and managed payload/notices.' -ForegroundColor Green

# 7. Create Velopack Setup Package & Portable Distribution
$VpkDir = Join-Path $RepoRoot 'tools\vpk'
$VpkExe = Join-Path $VpkDir 'vpk.exe'
if (!(Test-Path -LiteralPath $VpkExe)) {
    & $DotnetExe tool install vpk --version 1.2.161 --tool-path $VpkDir --configfile (Join-Path $RepoRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install the pinned Velopack tool.' }
}
$PackageId = 'WinSearch.WAI'

$VpkArgs = @(
    "pack",
    "--packId", $PackageId,
    "--packVersion", $Version,
    "--runtime", "win-x64",
    "--packTitle", "WAI (Windows AI Search)",
    "--packAuthors", "WAI Contributors",
    "--packDir", $StagingDir,
    "--mainExe", "WAIApp.exe",
    "--outputDir", $ReleasesDir,
    "--shortcuts", "Desktop,StartMenuRoot",
    "--yes"
)
Write-Host "Building Velopack Setup.exe installer and Portable bundle via $VpkExe..." -ForegroundColor Yellow
& $VpkExe @VpkArgs
if ($LASTEXITCODE -ne 0) {
    throw "Velopack pack failed with exit code $LASTEXITCODE."
}
Write-Host "Velopack package generated successfully in $ReleasesDir" -ForegroundColor Green

# 8. Ship the exact media bundle and matching source beside application artifacts.
Copy-Item -LiteralPath (Join-Path $RepoRoot 'tools\native-cache\ffmpeg-tools-win-x64.zip') -Destination $ReleasesDir -Force
Copy-Item -LiteralPath (Join-Path $NativeRoot 'sources\ffmpeg-corresponding-source.zip') -Destination $ReleasesDir -Force
Copy-Item -LiteralPath $NativeManifestPath -Destination $ReleasesDir -Force

# 9. Compute SHA256 Checksums
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
