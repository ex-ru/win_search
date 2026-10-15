# Prepare the exact redistributable runtime from pinned archives. PowerShell 5.1 / 7.
param(
    [string]$OutputDir = '',
    [string]$CacheDir = '',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDir) { $OutputDir = Join-Path $RepoRoot 'tools\runtime-win-x64' }
if (!$CacheDir) { $CacheDir = Join-Path $RepoRoot 'tools\native-cache' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$CacheDir = [IO.Path]::GetFullPath($CacheDir)
$LockPath = Join-Path $RepoRoot 'native-dependencies.json'
$Lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$Lock.ffmpeg) { throw 'FFmpeg binary archive has not been pinned yet.' }
foreach ($Path in @($OutputDir,$CacheDir)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
$Bin = Join-Path $OutputDir 'bin'
$Licenses = Join-Path $OutputDir 'licenses'
foreach ($Path in @($Bin,$Licenses)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
function Get-Verified($Entry, [string]$Name) {
    $Path = Join-Path $CacheDir $Name
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Entry.sha256) { return $Path }
        throw "Cached archive has the wrong hash: $Path. Move it aside before retrying."
    }
    if ($Offline) { throw "Offline cache is missing $Name" }
    $Partial = "$Path.partial"
    Invoke-WebRequest -Uri $Entry.url -OutFile $Partial -UseBasicParsing -TimeoutSec 300
    if ((Get-FileHash -LiteralPath $Partial -Algorithm SHA256).Hash -ne $Entry.sha256) { throw "SHA-256 mismatch for $Name" }
    if ($Entry.size -and (Get-Item -LiteralPath $Partial).Length -ne $Entry.size) { throw "Size mismatch for $Name" }
    Move-Item -LiteralPath $Partial -Destination $Path
    return $Path
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Expand-VerifiedZip([string]$Path, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $Prefix = [IO.Path]::GetFullPath($Destination).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $Zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($Entry in $Zip.Entries) {
            $Target = [IO.Path]::GetFullPath((Join-Path $Destination $Entry.FullName))
            if (!$Target.StartsWith($Prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive entry escapes extraction directory.' }
            if ($Entry.FullName.EndsWith('/') -or $Entry.FullName.EndsWith('\')) { New-Item -ItemType Directory -Force -Path $Target | Out-Null; continue }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($Entry,$Target,$true)
        }
    } finally { $Zip.Dispose() }
}
$LlamaZip = Get-Verified $Lock.llama 'llama-b11461-win-vulkan-x64.zip'
$PdfTgz = Get-Verified $Lock.pdfium 'pdfium-8100-win-x64.tgz'
$MediaZip = Get-Verified $Lock.ffmpeg 'ffmpeg-tools-win-x64.zip'
$SourceZip = Get-Verified $Lock.ffmpeg.sourceArchive 'ffmpeg-corresponding-source.zip'
$VcInstaller = Get-Verified $Lock.vcruntime 'vc-redist-14.51.36247-x64.exe'
& (Join-Path $PSScriptRoot 'prepare-vcruntime.ps1') -Installer $VcInstaller -OutputDir $Bin -CacheDir $CacheDir
$LlamaDir = Join-Path $CacheDir 'llama-b11461'
$PdfDir = Join-Path $CacheDir 'pdfium-8100'
$MediaDir = Join-Path $CacheDir 'ffmpeg-payload'
Expand-VerifiedZip $LlamaZip $LlamaDir
Expand-VerifiedZip $MediaZip $MediaDir
New-Item -ItemType Directory -Force -Path $PdfDir | Out-Null
# This verified upstream tgz contains only regular files/directories, as checked by the release gate.
& tar -xzf $PdfTgz -C $PdfDir
if ($LASTEXITCODE -ne 0) { throw 'PDFium extraction failed (Windows tar.exe required).' }
$Selected = @(Get-ChildItem -LiteralPath $LlamaDir -File | Where-Object {
    $_.Name -in @('llama.dll','mtmd.dll','ggml.dll','ggml-base.dll','ggml-vulkan.dll','libomp.dll') -or $_.Name -like 'ggml-cpu-*.dll'
})
foreach ($File in $Selected) { Copy-Item -LiteralPath $File.FullName -Destination $Bin -Force }
foreach ($Name in @('ffmpeg.exe','ffprobe.exe')) { Copy-Item -LiteralPath (Join-Path $MediaDir $Name) -Destination $Bin -Force }
Copy-Item -LiteralPath (Join-Path $PdfDir 'bin\pdfium.dll') -Destination $Bin -Force
foreach ($Name in @('llama.dll','mtmd.dll','ggml-vulkan.dll','ggml-cpu-x64.dll','libomp.dll','pdfium.dll','ffmpeg.exe','ffprobe.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $Bin $Name) -PathType Leaf)) { throw "Missing native payload: $Name" }
}
$LlamaNotices = Join-Path $Licenses 'llama'
$PdfNotices = Join-Path $Licenses 'pdfium'
foreach ($Path in @($LlamaNotices,$PdfNotices)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
Copy-Item -LiteralPath (Join-Path $LlamaDir 'LICENSE-LLVM-OpenMP') -Destination $LlamaNotices -Force
Copy-Item -Path (Join-Path $RepoRoot 'licenses\llama\*') -Destination $LlamaNotices -Force
Copy-Item -LiteralPath (Join-Path $PdfDir 'LICENSE') -Destination (Join-Path $PdfNotices 'LICENSE-binary-distribution.txt') -Force
Copy-Item -Path (Join-Path $PdfDir 'licenses\*') -Destination $PdfNotices -Force
Copy-Item -Path (Join-Path $MediaDir 'licenses\*') -Destination $Licenses -Recurse -Force
$VcNotices = Join-Path $Licenses 'vcruntime'
New-Item -ItemType Directory -Force -Path $VcNotices | Out-Null
$VcNoticeSource = Join-Path $RepoRoot 'licenses\vcruntime'
$VcNoticeIndex = Get-Content -LiteralPath (Join-Path $VcNoticeSource 'index.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($File in $VcNoticeIndex.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $VcNoticeSource $File.name) -Algorithm SHA256).Hash -ne $File.sha256) { throw "VC runtime notice changed: $($File.name)" }
}
Copy-Item -Path (Join-Path $RepoRoot 'licenses\vcruntime\*') -Destination $VcNotices -Force
$Sources = Join-Path $OutputDir 'sources'
New-Item -ItemType Directory -Force -Path $Sources | Out-Null
Copy-Item -LiteralPath $SourceZip -Destination $Sources -Force
$Inventory = @()
foreach ($File in Get-ChildItem -LiteralPath $Bin -File | Sort-Object Name) {
    $Inventory += [ordered]@{name=$File.Name;size=$File.Length;sha256=(Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$Manifest = [ordered]@{schemaVersion=1;lockSha256=(Get-FileHash -LiteralPath $LockPath -Algorithm SHA256).Hash.ToLowerInvariant();dependencies=$Lock;files=$Inventory}
[IO.File]::WriteAllText((Join-Path $OutputDir 'native-provenance.json'),($Manifest | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
Write-Host "Verified native runtime prepared: $OutputDir"
