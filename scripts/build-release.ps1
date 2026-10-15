param([string]$OutputDir = '')
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Release = Get-Content -LiteralPath (Join-Path $RepoRoot 'release-version.json') -Raw | ConvertFrom-Json
$Builder = Join-Path $RepoRoot 'port\WAI-5c5c1f3ae2b4346353e34704b4783956d1ae8c4f\windows\scripts\build-package.ps1'
if ($OutputDir) { & $Builder -Version $Release.version -OutputDir $OutputDir }
else { & $Builder -Version $Release.version }
