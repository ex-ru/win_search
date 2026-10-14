param([ValidateSet('cpu', 'vulkan')][string]$Backend = 'vulkan')
$ErrorActionPreference = 'Stop'
$env:WAI_HOME = Join-Path $PSScriptRoot 'state\app'
$env:WAI_MODELS = Join-Path $PSScriptRoot 'models'
$env:WAI_INDEX = Join-Path $PSScriptRoot "state\index-$Backend"
$env:WAI_FFMPEG = Join-Path $PSScriptRoot 'tools\ffmpeg'
$env:WAI_UPDATE_SOURCE = Join-Path $PSScriptRoot 'dist\releases'
$runDir = if ($Backend -eq 'vulkan') { Join-Path $PSScriptRoot 'run-vulkan' } else { Join-Path $PSScriptRoot 'run' }
$exe = Join-Path $runDir 'WAIApp.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Build missing: $exe" }
New-Item -ItemType Directory -Force -Path $env:WAI_HOME | Out-Null
$settingsPath = Join-Path $env:WAI_HOME 'settings.json'
if (!(Test-Path -LiteralPath $settingsPath)) {
    @{
        Folders = @((Join-Path $PSScriptRoot 'testbed'))
        Skipped = @()
        Hotkey = 'ctrl+alt+space'
        Dim = 0.35
        Blur = $true
        Code = $false
        CheckUpdates = $true
        UpdateSource = $env:WAI_UPDATE_SOURCE
        Editor = 'auto'
        IsNew = $false
        SetupDone = $true
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
}
Start-Process -FilePath $exe -ArgumentList '--show' -WorkingDirectory $runDir -WindowStyle Normal
