#Requires -Version 7.0
# Actual portable updater gate. All mutable paths are created beneath a new temporary root.
param(
    [Parameter(Mandatory=$true)][string]$PackageDir,
    [Parameter(Mandatory=$true)][string]$TargetReleaseDir,
    [Parameter(Mandatory=$true)][string]$ModelsDir,
    [string]$BaselineVersion='1.1.99',
    [string]$TargetVersion='1.2.2',
    [string]$BaselineReleaseDir='',
    [string]$Model='bge-m3',
    [string]$ReportDir=''
)
$ErrorActionPreference='Stop'
$RepoRoot=Split-Path -Parent $PSScriptRoot
$PackageDir=[IO.Path]::GetFullPath($PackageDir)
$TargetReleaseDir=[IO.Path]::GetFullPath($TargetReleaseDir)
$ModelsDir=[IO.Path]::GetFullPath($ModelsDir)
if (!$ReportDir) { $ReportDir=Join-Path $RepoRoot ('results\update-apply-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$ReportDir=[IO.Path]::GetFullPath($ReportDir)
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$Workspace=Join-Path ([IO.Path]::GetTempPath()) ('WAI-update-apply-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Workspace | Out-Null
$Started=[DateTimeOffset]::UtcNow; $Checks=New-Object 'System.Collections.Generic.List[object]'
$Applied=$false; $ExitCode=0; $Saved=@{}; $TargetPackageSha256=$null; $VerifiedPayloadSha256=@{}
foreach($Name in @('WAI_HOME','WAI_INDEX','WAI_MODELS','WAI_MODEL','WAI_BACKEND','WAI_FFMPEG','DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES')) { $Saved[$Name]=[Environment]::GetEnvironmentVariable($Name,'Process') }
function Assert-Apply([bool]$Condition,[string]$Reason) { if(!$Condition){throw $Reason} }
function Run-Process([string]$Name,[string]$Exe,[string[]]$Arguments,[int]$TimeoutSeconds=300,[switch]$AllowFailure) {
    $Start=[Diagnostics.ProcessStartInfo]::new($Exe)
    $Start.UseShellExecute=$false; $Start.CreateNoWindow=$true
    $Start.RedirectStandardOutput=$true; $Start.RedirectStandardError=$true
    foreach($Argument in $Arguments){$Start.ArgumentList.Add($Argument)}
    $Process=[Diagnostics.Process]::Start($Start)
    $Out=$Process.StandardOutput.ReadToEndAsync(); $Err=$Process.StandardError.ReadToEndAsync(); $Timer=[Diagnostics.Stopwatch]::StartNew()
    try {
        while(!$Process.WaitForExit(1000)) { if($Timer.Elapsed.TotalSeconds -gt $TimeoutSeconds){$Process.Kill($true);throw "$Name timed out"} }
        $Text=$Out.GetAwaiter().GetResult()+"`n"+$Err.GetAwaiter().GetResult()
        $Text | Out-File -LiteralPath (Join-Path $ReportDir "$Name.log") -Encoding utf8
        $Code=$Process.ExitCode
        if(!$AllowFailure){Assert-Apply ($Code -eq 0) "$Name failed (exit $Code); see $Name.log"}
        return [pscustomobject]@{exitCode=$Code;text=$Text}
    } finally {$Process.Dispose()}
}
function Record([string]$Name,[scriptblock]$Action) {
    $Timer=[Diagnostics.Stopwatch]::StartNew()
    try { $Detail=& $Action; $Checks.Add([ordered]@{name=$Name;status='passed';seconds=$Timer.Elapsed.TotalSeconds;detail=@($Detail)});Write-Host "passed: $Name" }
    catch {$Checks.Add([ordered]@{name=$Name;status='failed';seconds=$Timer.Elapsed.TotalSeconds;detail=$_.ToString()});throw}
}
function Installed-Version([string]$Root) {
    $Path=Join-Path $Root 'current\sq.version'
    Assert-Apply (Test-Path -LiteralPath $Path -PathType Leaf) 'Missing installed sq.version manifest.'
    [xml]$Xml=Get-Content -LiteralPath $Path -Raw
    $Metadata=$Xml.SelectSingleNode("//*[local-name()='metadata']")
    Assert-Apply ($null -ne $Metadata) 'Installed manifest has no metadata.'
    Assert-Apply ($Metadata.SelectSingleNode("*[local-name()='id']").InnerText -eq 'WinSearch.WAI') 'Wrong installed package identity.'
    return $Metadata.SelectSingleNode("*[local-name()='version']").InnerText
}
try {
    $env:DOTNET_ROOT=Join-Path $RepoRoot 'tools\dotnet'; $env:DOTNET_CLI_HOME=Join-Path $RepoRoot 'tools\dotnet-home'; $env:NUGET_PACKAGES=Join-Path $RepoRoot 'tools\nuget'
    $env:WAI_HOME=Join-Path $Workspace 'home'; $env:WAI_INDEX=Join-Path $Workspace 'index'
    $env:WAI_MODELS=$ModelsDir; $env:WAI_MODEL=$Model; $env:WAI_BACKEND='cpu'
    foreach($Directory in @($env:WAI_HOME,$env:WAI_INDEX)){New-Item -ItemType Directory -Path $Directory | Out-Null}
    $Vpk=Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'; $LocalVpk=Join-Path $env:DOTNET_CLI_HOME '.dotnet\tools\vpk.exe'
    if(Test-Path -LiteralPath $LocalVpk){$Vpk=$LocalVpk}
    Assert-Apply (Test-Path -LiteralPath $Vpk -PathType Leaf) 'Local vpk.exe required; no automatic installation.'
    Assert-Apply ([version]$BaselineVersion -lt [version]$TargetVersion) 'Target must be newer than baseline.'
    $TargetPackage=Join-Path $TargetReleaseDir "WinSearch.WAI-$TargetVersion-full.nupkg"
    Record 'target-checksum' {
        Assert-Apply (Test-Path -LiteralPath $TargetPackage -PathType Leaf) 'Missing target full nupkg.'
        $Feed=Get-Content -LiteralPath (Join-Path $TargetReleaseDir 'releases.win.json') -Raw | ConvertFrom-Json
        $Asset=@($Feed.Assets | Where-Object {$_.PackageId -eq 'WinSearch.WAI' -and $_.Version -eq $TargetVersion -and $_.Type -eq 'Full'})
        Assert-Apply ($Asset.Count -eq 1) 'Target full package must occur once in feed.'
        $script:TargetPackageSha256=(Get-FileHash -LiteralPath $TargetPackage -Algorithm SHA256).Hash
        Assert-Apply ($TargetPackageSha256 -eq $Asset[0].SHA256) 'Target feed checksum mismatch.'
        "Verified target $TargetVersion package SHA256."
    }
    $BaselineSource=Join-Path $Workspace 'baseline-source'; New-Item -ItemType Directory -Path $BaselineSource | Out-Null
    $BaselineFeed=Join-Path $Workspace 'baseline-feed'
    if ($BaselineReleaseDir) {
        $BaselineReleaseDir=[IO.Path]::GetFullPath($BaselineReleaseDir)
        New-Item -ItemType Directory -Path $BaselineFeed | Out-Null
        Record 'verify-historical-baseline' {
            $Portable=Join-Path $BaselineReleaseDir 'WinSearch.WAI-win-Portable.zip'
            $Expected=$null
            foreach($Line in Get-Content -LiteralPath (Join-Path $BaselineReleaseDir 'SHA256SUMS.txt')) {
                if($Line -match '^([a-fA-F0-9]{64})\s+\*?WinSearch\.WAI-win-Portable\.zip$'){$Expected=$Matches[1]}
            }
            Assert-Apply ($null -ne $Expected) 'Historical baseline has no portable checksum.'
            Assert-Apply ((Get-FileHash -LiteralPath $Portable -Algorithm SHA256).Hash -eq $Expected) 'Historical baseline checksum mismatch.'
            Copy-Item -LiteralPath $Portable -Destination $BaselineFeed
            "Actual historical portable baseline SHA256 $Expected"
        }
    } else {
    Record 'build-disposable-baseline' {
        Assert-Apply (Test-Path -LiteralPath (Join-Path $PackageDir 'WAIApp.exe')) 'Missing staging WAIApp.exe.'
        foreach($File in Get-ChildItem -LiteralPath $PackageDir -Recurse -File){
            $Destination=Join-Path $BaselineSource ([IO.Path]::GetRelativePath($PackageDir,$File.FullName))
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
            # Packaging can rewrite/sign input binaries. A hardlink here would alter target staging.
            Copy-Item -LiteralPath $File.FullName -Destination $Destination
        }
        [IO.File]::WriteAllText((Join-Path $BaselineSource 'baseline-only-release-gate.txt'),'Must disappear when target replaces baseline.')
        Run-Process 'baseline-pack' $Vpk @('pack','--packId','WinSearch.WAI','--packVersion',$BaselineVersion,'--packDir',$BaselineSource,'--mainExe','WAIApp.exe','--packTitle','WAI','--runtime','win-x64','--outputDir',$BaselineFeed,'--shortcuts','None','--delta','None','--yes') 600 | Out-Null
        'Baseline fixture uses current binaries with lower package version and a baseline-only marker; no historic-binary migration claim.'
    }
    }
    $Install=Join-Path $Workspace 'portable'
    Record 'extract-and-index-baseline' {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $BaselineFeed 'WinSearch.WAI-win-Portable.zip'),$Install)
        Assert-Apply (Test-Path -LiteralPath (Join-Path $Install '.portable') -PathType Leaf) 'Not a disposable portable install; refusing apply.'
        Assert-Apply ((Installed-Version $Install) -eq $BaselineVersion) 'Incorrect baseline version.'
        if ($BaselineReleaseDir) { [IO.File]::WriteAllText((Join-Path $Install 'current\baseline-only-release-gate.txt'),'Added test marker must disappear when target replaces baseline.') }
        Assert-Apply (Test-Path -LiteralPath (Join-Path $Install 'current\baseline-only-release-gate.txt')) 'Missing baseline marker.'
        $Documents=Join-Path $Workspace 'documents';New-Item -ItemType Directory -Path $Documents | Out-Null
        [IO.File]::WriteAllText((Join-Path $Documents 'persistent.txt'),'UPDATEPERSIST483716. Резервная копия рабочих документов хранится на внешнем носителе.')
        $Settings=Join-Path $env:WAI_HOME 'settings.json';[IO.File]::WriteAllText($Settings,'{"fixture":"update-persistence","AutoStart":false,"ModelId":"bge-m3"}')
        $script:SettingsHash=(Get-FileHash -LiteralPath $Settings -Algorithm SHA256).Hash
        $env:WAI_FFMPEG=Join-Path $Install 'current'
        Run-Process 'baseline-index' (Join-Path $Install 'current\wai.exe') @('index',$Documents,'--index',$env:WAI_INDEX,'--workers','1') | Out-Null
        $Found=Run-Process 'baseline-search' (Join-Path $Install 'current\wai.exe') @('search','UPDATEPERSIST483716','--index',$env:WAI_INDEX,'--json')
        Assert-Apply ($Found.text -match 'persistent\.txt') 'Baseline fixture was not searchable.'
        'Actual baseline CLI indexing and search passed; persistent settings artifact created.'
    }
    $Updater=Join-Path $Install 'Update.exe'; $Cache=Join-Path $Install 'packages';New-Item -ItemType Directory -Force -Path $Cache | Out-Null
    Record 'reject-corrupt-package' {
        $Corrupt=Join-Path $Workspace 'corrupt-full.nupkg';[IO.File]::WriteAllText($Corrupt,'Deliberately invalid ZIP payload')
        $Result=Run-Process 'corrupt-apply' $Updater @('--rootDir',$Install,'--packageDir',$Cache,'--silent','--log',(Join-Path $ReportDir 'updater-corrupt.log'),'apply','--norestart','--package',$Corrupt) 120 -AllowFailure
        Assert-Apply ($Result.exitCode -ne 0) 'Updater accepted corrupt package.'
        Assert-Apply ((Installed-Version $Install) -eq $BaselineVersion) 'Corrupt apply changed baseline version.'
        $Found=Run-Process 'baseline-after-rejection' (Join-Path $Install 'current\wai.exe') @('search','UPDATEPERSIST483716','--index',$env:WAI_INDEX,'--json')
        Assert-Apply ($Found.text -match 'persistent\.txt') 'Baseline unusable after rejected apply.'
        'Actual updater rejected invalid ZIP; baseline still launches and searches.'
    }
    Record 'actual-velopack-apply' {
        $Staged=Join-Path $Cache ([IO.Path]::GetFileName($TargetPackage));Copy-Item -LiteralPath $TargetPackage -Destination $Staged
        Run-Process 'apply' $Updater @('--rootDir',$Install,'--packageDir',$Cache,'--silent','--log',(Join-Path $ReportDir 'updater-apply.log'),'apply','--norestart','--package',$Staged) 300 | Out-Null
        Assert-Apply ((Installed-Version $Install) -eq $TargetVersion) 'Updater exited without installing target version.'
        Assert-Apply (!(Test-Path -LiteralPath (Join-Path $Install 'current\baseline-only-release-gate.txt'))) 'Baseline-only marker survived replacement.'
        foreach($File in @('WAIApp.exe','wai.exe','wai-embed.exe','WAI.Core.dll','System.Private.CoreLib.dll')){
            $ActualHash=(Get-FileHash -LiteralPath (Join-Path $Install "current\$File") -Algorithm SHA256).Hash
            Assert-Apply ($ActualHash -eq (Get-FileHash -LiteralPath (Join-Path $PackageDir $File) -Algorithm SHA256).Hash) "Applied target mismatch: $File"
            $VerifiedPayloadSha256[$File]=$ActualHash
        }
        $script:Applied=$true
        'Real Update.exe applied target: installed version advanced, baseline marker removed, target executable/runtime hashes match.'
    }
    Record 'launch-target-and-preserve-state' {
        Assert-Apply ((Get-FileHash -LiteralPath (Join-Path $env:WAI_HOME 'settings.json') -Algorithm SHA256).Hash -eq $SettingsHash) 'Settings artifact changed during apply.'
        $Cli=Join-Path $Install 'current\wai.exe'
        $Found=Run-Process 'target-search' $Cli @('search','UPDATEPERSIST483716','--index',$env:WAI_INDEX,'--json')
        Assert-Apply ($Found.text -match 'persistent\.txt') 'Target cannot search existing index.'
        $Document=Join-Path $Workspace 'documents\persistent.txt';[IO.File]::WriteAllText($Document,'UPDATEAFTER99173. Документ обновлен после применения новой версии.')
        [IO.File]::SetLastWriteTimeUtc($Document,[DateTime]::UtcNow.AddSeconds(5))
        Run-Process 'target-index' $Cli @('index',(Join-Path $Workspace 'documents'),'--index',$env:WAI_INDEX,'--workers','1') | Out-Null
        $Found=Run-Process 'target-relaunch-search' $Cli @('search','UPDATEAFTER99173','--index',$env:WAI_INDEX,'--json')
        Assert-Apply ($Found.text -match 'persistent\.txt') 'Target mutation/relaunch search failed.'
        $script:Applied=$true
        'Target CLI launched in separate processes; existing index/settings preserved; mutation and relaunch search passed. GUI not opened.'
    }
} catch {$ExitCode=1;Write-Warning $_.ToString()}
finally {
    foreach($Name in $Saved.Keys){[Environment]::SetEnvironmentVariable($Name,$Saved[$Name],'Process')}
    [ordered]@{status=$(if($ExitCode -eq 0 -and $Applied){'passed'}else{'failed'});applied=$Applied;baselineVersion=$BaselineVersion;targetVersion=$TargetVersion;targetPackageSha256=$TargetPackageSha256;verifiedPayloadSha256=$VerifiedPayloadSha256;startedAtUtc=$Started;finishedAtUtc=[DateTimeOffset]::UtcNow;isolatedWorkspace=$Workspace;checks=$Checks;
        historicalBinaryBaseline=[bool]$BaselineReleaseDir;limitations=@('Disposable portable installation only.',$(if($BaselineReleaseDir){'Verified historical portable binaries used.'}else{'Baseline uses current binaries with lower package metadata, not historic binaries.'}),'Actual updater hooks and CLI relaunch exercised; interactive GUI restart not tested.','Interrupted network transfer is separate from local apply.');updaterReference='https://docs.velopack.io/reference/cli/content/update-windows'} | ConvertTo-Json -Depth 10 | Out-File -LiteralPath (Join-Path $ReportDir 'update-apply.json') -Encoding utf8
}
exit $ExitCode
