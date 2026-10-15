# Extract app-local CRT DLLs without installing a machine-wide redistributable.
param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$OutputDir,
    [Parameter(Mandatory=$true)][string]$CacheDir
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Lock = (Get-Content -LiteralPath (Join-Path $RepoRoot 'native-dependencies.json') -Raw -Encoding UTF8 | ConvertFrom-Json).vcruntime
if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $Lock.sha256) { throw 'VC runtime installer hash mismatch.' }
$Signature = Get-AuthenticodeSignature -LiteralPath $Installer
if ($Signature.Status -ne 'Valid' -or $Signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'VC runtime installer signature is not valid Microsoft code.' }
$Work = Join-Path ([IO.Path]::GetFullPath($CacheDir)) ('vcruntime-extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$Expand = Join-Path $env:SystemRoot 'System32\expand.exe'
$Bytes = [IO.File]::ReadAllBytes($Installer)
$Search = [Text.Encoding]::GetEncoding(28591).GetString($Bytes)
$Position = 0; $Containers = @()
while (($Position = $Search.IndexOf('MSCF',$Position,[StringComparison]::Ordinal)) -ge 0) {
    if ($Position + 36 -le $Bytes.Length) {
        $Length = [BitConverter]::ToUInt32($Bytes,$Position + 8)
        if ($Length -gt 36 -and ([long]$Position + $Length) -le $Bytes.Length -and $Bytes[$Position+24] -eq 3 -and $Bytes[$Position+25] -eq 1) {
            $Cabinet = Join-Path $Work ("container-$($Containers.Count).cab")
            $CabBytes = New-Object byte[] $Length
            [Buffer]::BlockCopy($Bytes,$Position,$CabBytes,0,$Length)
            [IO.File]::WriteAllBytes($Cabinet,$CabBytes)
            $Destination = Join-Path $Work ("container-$($Containers.Count)")
            New-Item -ItemType Directory -Path $Destination | Out-Null
            & $Expand '-F:*' $Cabinet $Destination | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'VC runtime embedded cabinet extraction failed.' }
            $Containers += $Destination
        }
    }
    $Position += 4
}
if ($Containers.Count -ne 2) { throw 'Unexpected VC runtime container layout; review the pinned archive.' }
[xml]$Manifest = Get-Content -LiteralPath (Join-Path $Containers[0] '0') -Raw
$Payload = @($Manifest.BurnManifest.Payload | Where-Object { $_.FilePath -like '*vcRuntimeMinimum_amd64\*.cab' })
if ($Payload.Count -ne 1 -or $Payload[0].SourcePath -notmatch '^a[0-9]+$') { throw 'Pinned VC runtime x64 cabinet not found.' }
$RuntimeCab = Join-Path $Work 'runtime.cab'
Copy-Item -LiteralPath (Join-Path $Containers[1] $Payload[0].SourcePath) -Destination $RuntimeCab
$Runtime = Join-Path $Work 'runtime'
New-Item -ItemType Directory -Path $Runtime | Out-Null
& $Expand '-F:*' $RuntimeCab $Runtime | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'VC runtime DLL extraction failed.' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
foreach ($File in $Lock.files) {
    $Source = Join-Path $Runtime $File.archiveName
    if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $File.sha256 -or (Get-Item -LiteralPath $Source).Length -ne $File.size) { throw "VC runtime DLL changed: $($File.name)" }
    if ((Get-Item -LiteralPath $Source).VersionInfo.FileVersion -ne $Lock.version) { throw "VC runtime DLL version changed: $($File.name)" }
    Copy-Item -LiteralPath $Source -Destination (Join-Path $OutputDir $File.name) -Force
}
Write-Host 'Pinned, Microsoft-signed app-local VC runtime prepared without machine installation.'
