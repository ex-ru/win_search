param(
    [Parameter(Mandatory=$true)][string]$PackageDir,
    [string]$ReportDir = ''
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PackageDir = [IO.Path]::GetFullPath($PackageDir)
if (!$ReportDir) { $ReportDir = Join-Path $RepoRoot 'state\native-package-check' }
$ReportDir = [IO.Path]::GetFullPath($ReportDir)
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$Runner = Join-Path $ReportDir 'runner'
$Dotnet = Join-Path $RepoRoot 'tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $Dotnet)) { $Dotnet = 'dotnet' }
& $Dotnet publish (Join-Path $RepoRoot 'checks\WAI.ReleaseSmoke\WAI.ReleaseSmoke.csproj') -c Release -r win-x64 --self-contained true -o $Runner --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Native gate harness publish failed.' }
# Overlay the shipped runtime and Core; the driver must not substitute its project-build Core.
Copy-Item -Path (Join-Path $PackageDir '*') -Destination $Runner -Recurse -Force
$ReportPath = Join-Path $ReportDir 'native-smoke.json'
& (Join-Path $Runner 'WAI.ReleaseSmoke.exe') --package $PackageDir --native --report $ReportPath
if ($LASTEXITCODE -ne 0) { throw 'Packaged native gate failed.' }
$Report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($Report.loadedCoreSha256 -ne (Get-FileHash -LiteralPath (Join-Path $PackageDir 'WAI.Core.dll')).Hash) { throw 'The gate loaded another Core assembly.' }
$Index = Get-Content -LiteralPath (Join-Path $PackageDir 'licenses\managed\index.json') -Raw | ConvertFrom-Json
if ($Index.unresolved_count -ne 0) { throw 'Unresolved managed notices in package.' }
$Manifest = Get-Content -LiteralPath (Join-Path $PackageDir 'native-provenance.json') -Raw | ConvertFrom-Json
foreach ($File in $Manifest.files) {
    if ($File.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $PackageDir $File.name)).Hash) { throw "Native file hash mismatch: $($File.name)" }
}
Write-Host 'Packaged Core/runtime, native PDF/media, rollback and notices passed. Models are checked separately with local fixtures.'
