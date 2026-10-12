param([ValidateSet('cpu', 'vulkan')][string]$Backend = 'vulkan')
$ErrorActionPreference = 'Stop'
$env:DIGUP_HOME = Join-Path $PSScriptRoot 'state\app'
$env:DIGUP_MODELS = Join-Path $PSScriptRoot 'models'
$env:DIGUP_INDEX = Join-Path $PSScriptRoot "state\index-$Backend"
$env:DIGUP_FFMPEG = Join-Path $PSScriptRoot 'tools\ffmpeg'
$runDir = if ($Backend -eq 'vulkan') { Join-Path $PSScriptRoot 'run-vulkan' } else { Join-Path $PSScriptRoot 'run' }
$exe = Join-Path $runDir 'DigUpApp.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Build missing: $exe" }
New-Item -ItemType Directory -Force -Path $env:DIGUP_HOME | Out-Null
$settingsPath = Join-Path $env:DIGUP_HOME 'settings.json'
if (!(Test-Path -LiteralPath $settingsPath)) {
    @{
        Folders = @((Join-Path $PSScriptRoot 'testbed'))
        Skipped = @()
        Hotkey = 'ctrl+alt+space'
        Dim = 0.35
        Blur = $true
        Code = $false
        CheckUpdates = $false
        Editor = 'auto'
        IsNew = $false
        SetupDone = $true
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
}
Start-Process -FilePath $exe -ArgumentList '--show' -WorkingDirectory $runDir -WindowStyle Normal
