# Build and package DigUp for Windows (Velopack Setup.exe + Portable zip)
# PowerShell 5.1 & Core safe.
param(
    [string]$Version = "1.0.4",
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
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist"
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

$RunVulkan = Join-Path $RepoRoot "run-vulkan"
$FfmpegTools = Join-Path $RepoRoot "tools\ffmpeg"
$LlamaVulkan = Join-Path $RepoRoot "tools\llama-vulkan"
$PdfiumSource = Join-Path $WindowsDir "native\x64\pdfium.dll"
$OpenMpLicenseSource = Join-Path $LlamaVulkan "LICENSE-LLVM-OpenMP"
$RepoLicenseSource = Join-Path $RepoRoot "LICENSE"
$RequiredNativeDlls = @("llama.dll", "mtmd.dll", "ggml.dll", "ggml-base.dll", "ggml-vulkan.dll", "ggml-cpu-x64.dll", "libomp.dll")
$RequiredFfmpegTools = @("ffmpeg.exe", "ffprobe.exe")
foreach ($name in $RequiredNativeDlls) {
    if (!(Test-Path -LiteralPath (Join-Path $RunVulkan $name) -PathType Leaf)) {
        throw "Missing required native runtime file: $(Join-Path $RunVulkan $name)"
    }
}
foreach ($name in $RequiredFfmpegTools) {
    if (!(Test-Path -LiteralPath (Join-Path $FfmpegTools $name) -PathType Leaf)) {
        throw "Missing required media tool: $(Join-Path $FfmpegTools $name)"
    }
}
if (!(Test-Path -LiteralPath $PdfiumSource -PathType Leaf)) {
    throw "Missing required PDFium runtime: $PdfiumSource"
}
foreach ($license in @($OpenMpLicenseSource, $RepoLicenseSource)) {
    if (!(Test-Path -LiteralPath $license -PathType Leaf)) {
        throw "Missing required license file: $license"
    }
}

$NuGetConfig = Join-Path $RepoRoot "tools\NuGet.Config"
if (!(Test-Path -LiteralPath $NuGetConfig -PathType Leaf)) {
    $NuGetConfig = $null
}

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
        "-o", $StagingDir, "--nologo", "-v", "quiet")
    if ($NuGetConfig) {
        $PublishArgs += "-p:RestoreConfigFile=$NuGetConfig"
    }
    & $DotnetExe @PublishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $Name"
    }
}

Publish-Project "src\DigUp.App\DigUp.App.csproj" "DigUpApp (WPF GUI)"
Publish-Project "src\digup\digup.csproj" "digup (CLI)"
Publish-Project "src\DigUp.Embed\DigUp.Embed.csproj" "digup-embed (Worker)"

# 4. Copy native runtime libraries
Write-Host "Copying native runtime binaries..." -ForegroundColor Yellow
Get-ChildItem -LiteralPath $RunVulkan -Filter "*.dll" -File |
    Where-Object { $_.Name -like "ggml*.dll" -or $_.Name -in @("llama.dll", "mtmd.dll", "libomp.dll") } |
    ForEach-Object {
        $dest = Join-Path $StagingDir $_.Name
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    }

foreach ($name in $RequiredFfmpegTools) {
    Copy-Item -LiteralPath (Join-Path $FfmpegTools $name) -Destination (Join-Path $StagingDir $name) -Force
}

$PdfiumTarget = Join-Path $StagingDir "pdfium.dll"
Copy-Item -LiteralPath $PdfiumSource -Destination $PdfiumTarget -Force
New-Item -ItemType Directory -Force -Path (Join-Path $StagingDir "native\x64") | Out-Null
Copy-Item -LiteralPath $PdfiumSource -Destination (Join-Path $StagingDir "native\x64\pdfium.dll") -Force

$LicenseTargetDir = Join-Path $StagingDir "licenses"
New-Item -ItemType Directory -Force -Path $LicenseTargetDir | Out-Null
Copy-Item -LiteralPath $OpenMpLicenseSource -Destination (Join-Path $LicenseTargetDir "LICENSE-LLVM-OpenMP") -Force
Copy-Item -LiteralPath $RepoLicenseSource -Destination (Join-Path $LicenseTargetDir "LICENSE") -Force

# 5. Verification of packaged payload
$RequiredFiles = @("DigUpApp.exe", "digup.exe", "digup-embed.exe", "DigUp.Core.dll", "e_sqlite3.dll") +
    $RequiredNativeDlls + $RequiredFfmpegTools + @("licenses\LICENSE-LLVM-OpenMP", "licenses\LICENSE")
foreach ($req in $RequiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $StagingDir $req) -PathType Leaf)) {
        throw "Missing required packaged file: $req in $StagingDir"
    }
}
if (!(Test-Path -LiteralPath $PdfiumTarget -PathType Leaf)) {
    throw "Missing required packaged file: pdfium.dll in $StagingDir"
}
Write-Host "Payload validation passed." -ForegroundColor Green

# 6. Move legacy PackId DigUp artifacts aside and remove them from active feeds.
$LegacyNupkgPattern = '^DigUp-.+-(?:full|delta)\.nupkg$'
$LegacyArtifactPattern = '^(?:DigUp-.+-(?:full|delta)\.nupkg|DigUp-win-Setup\.exe|DigUp-win-Portable\.zip)$'
$LegacyDir = Join-Path $ReleasesDir "legacy"
$LegacyArtifacts = @()
if (Test-Path -LiteralPath $ReleasesDir -PathType Container) {
    $LegacyArtifacts = @(Get-ChildItem -LiteralPath $ReleasesDir -File | Where-Object {
        $_.Name -cmatch $LegacyArtifactPattern
    })
}
$LegacyNupkgs = @($LegacyArtifacts | Where-Object { $_.Name -cmatch $LegacyNupkgPattern })

$ExistingLegacyNupkgs = @()
if (Test-Path -LiteralPath $LegacyDir) {
    $legacyDirItem = Get-Item -LiteralPath $LegacyDir -Force
    if (($legacyDirItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        !$legacyDirItem.PSIsContainer) {
        throw "Refusing to use legacy path that is not a regular directory: $LegacyDir"
    }
    $ExistingLegacyNupkgs = @(Get-ChildItem -LiteralPath $LegacyDir -File | Where-Object {
        $_.Name -cmatch $LegacyNupkgPattern
    })
}

$MetadataNames = @("RELEASES", "releases.win.json", "assets.win.json")
$MetadataBackups = @()
if ($LegacyNupkgs.Count -gt 0) {
    foreach ($name in $MetadataNames) {
        $source = Join-Path $ReleasesDir $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $MetadataBackups += Get-Item -LiteralPath $source
        }
    }
}

$MetadataReplacements = @{}
$HasLegacyNupkg = $LegacyNupkgs.Count -gt 0 -or $ExistingLegacyNupkgs.Count -gt 0
if ($HasLegacyNupkg) {
    $releasesPath = Join-Path $ReleasesDir "RELEASES"
    if (Test-Path -LiteralPath $releasesPath -PathType Leaf) {
        $keptLines = New-Object 'System.Collections.Generic.List[string]'
        foreach ($line in [System.IO.File]::ReadAllLines($releasesPath)) {
            $columns = $line -split '\s+'
            if ($columns.Length -ge 3 -and $columns[1] -cmatch $LegacyNupkgPattern) {
                continue
            }
            $keptLines.Add($line)
        }
        $MetadataReplacements["RELEASES"] = $keptLines.ToArray()
    }

    $releaseJsonPath = Join-Path $ReleasesDir "releases.win.json"
    if (Test-Path -LiteralPath $releaseJsonPath -PathType Leaf) {
        $releaseManifest = [System.IO.File]::ReadAllText($releaseJsonPath) | ConvertFrom-Json
        if ($null -eq $releaseManifest -or $null -eq $releaseManifest.PSObject.Properties["Assets"]) {
            throw "Invalid Velopack release metadata: $releaseJsonPath"
        }
        $releaseManifest.Assets = @($releaseManifest.Assets | Where-Object {
            $_.PackageId -cne "DigUp" -and $_.FileName -cnotmatch $LegacyArtifactPattern
        })
        $MetadataReplacements["releases.win.json"] = ConvertTo-Json -InputObject $releaseManifest -Depth 100 -Compress
    }

    $assetsJsonPath = Join-Path $ReleasesDir "assets.win.json"
    if (Test-Path -LiteralPath $assetsJsonPath -PathType Leaf) {
        $assetManifest = @([System.IO.File]::ReadAllText($assetsJsonPath) | ConvertFrom-Json)
        $assetManifest = @($assetManifest | Where-Object {
            $_.RelativeFileName -cnotmatch $LegacyArtifactPattern
        })
        $MetadataReplacements["assets.win.json"] = ConvertTo-Json -InputObject $assetManifest -Depth 100 -Compress
    }
}

$legacyTargets = @($LegacyArtifacts) + @($MetadataBackups)
foreach ($source in $legacyTargets) {
    $destination = Join-Path $LegacyDir $source.Name
    if (Test-Path -LiteralPath $destination) {
        throw "Legacy release filename already exists; refusing to overwrite: $destination"
    }
}
if ($legacyTargets.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $LegacyDir | Out-Null
    foreach ($artifact in $LegacyArtifacts) {
        Move-Item -LiteralPath $artifact.FullName -Destination (Join-Path $LegacyDir $artifact.Name)
    }
    foreach ($metadata in $MetadataBackups) {
        Copy-Item -LiteralPath $metadata.FullName -Destination (Join-Path $LegacyDir $metadata.Name)
    }
}

foreach ($name in $MetadataReplacements.Keys) {
    $path = Join-Path $ReleasesDir $name
    if ($name -eq "RELEASES") {
        [System.IO.File]::WriteAllLines($path, [string[]]$MetadataReplacements[$name], [System.Text.Encoding]::ASCII)
    } else {
        [System.IO.File]::WriteAllText($path, [string]$MetadataReplacements[$name], (New-Object System.Text.UTF8Encoding($false)))
    }
}

# 7. Create Velopack Setup Package & Portable Distribution
$VpkExe = "vpk"
$LocalVpk = Join-Path $LocalDotnetHome ".dotnet\tools\vpk.exe"
$UserVpk = "$env:USERPROFILE\.dotnet\tools\vpk.exe"
if (Test-Path -LiteralPath $LocalVpk -PathType Leaf) {
    $VpkExe = $LocalVpk
} elseif (Test-Path -LiteralPath $UserVpk -PathType Leaf) {
    $VpkExe = $UserVpk
}

$VpkArgs = @(
    "pack",
    "--packId", "WinSearch.DigUp",
    "--packVersion", $Version,
    "--runtime", "win-x64",
    "--packTitle", "DigUp",
    "--packAuthors", "DigUp Contributors",
    "--packDir", $StagingDir,
    "--mainExe", "DigUpApp.exe",
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
