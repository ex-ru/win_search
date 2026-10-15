# Verify an already built FULL package. Never downloads models or opens user folders.
param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [Parameter(Mandatory = $true)][string]$ModelsDir,
    [string]$ReleasesDir = '',
    [string]$Model = 'bge-m3',
    [string]$ReportDir = '',
    [switch]$Native,
    [switch]$SkipEvaluation
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PackageDir = [IO.Path]::GetFullPath($PackageDir)
$ModelsDir = [IO.Path]::GetFullPath($ModelsDir)
if (!$ReleasesDir) { $ReleasesDir = Join-Path (Split-Path -Parent $PackageDir) 'releases' }
$ReleasesDir = [IO.Path]::GetFullPath($ReleasesDir)
if (!$ReportDir) { $ReportDir = Join-Path $RepoRoot ('results\release-check-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$ReportDir = [IO.Path]::GetFullPath($ReportDir)
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$Checks = New-Object 'System.Collections.Generic.List[object]'
$Started = [DateTimeOffset]::UtcNow
$ExitCode = 0
$Dotnet = Join-Path $RepoRoot 'tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $Dotnet)) { $Dotnet = 'dotnet' }
$SavedEnvironment = @{}
foreach ($Name in @('WAI_HOME','WAI_INDEX','WAI_MODELS','WAI_MODEL','WAI_BACKEND','WAI_FFMPEG','DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES')) {
    $SavedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
}
function Assert-Release([bool]$Condition, [string]$Reason) { if (!$Condition) { throw $Reason } }
function Invoke-Gate([string]$Name, [scriptblock]$Action) {
    $Timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $Detail = & $Action
        $Checks.Add([ordered]@{name=$Name;status='passed';seconds=$Timer.Elapsed.TotalSeconds;detail=@($Detail)})
        Write-Host "passed: $Name"
    } catch {
        $Checks.Add([ordered]@{name=$Name;status='failed';seconds=$Timer.Elapsed.TotalSeconds;detail=$_.ToString()})
        throw
    }
}
try {
    Invoke-Gate 'packaged-payload-net10-full' {
        $Required = @('WAIApp.exe','wai.exe','wai-embed.exe','WAI.Core.dll','e_sqlite3.dll','pdfium.dll',
            'ffmpeg.exe','ffprobe.exe','llama.dll','mtmd.dll','ggml.dll','ggml-base.dll','ggml-vulkan.dll',
            'msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','licenses\vcruntime\index.json',
            'ggml-cpu-x64.dll','libomp.dll','System.Private.CoreLib.dll','licenses\LICENSE-WAI.txt',
            'licenses\llama\LICENSE','licenses\llama\LICENSE-LLVM-OpenMP','licenses\pdfium\pdfium.txt',
            'licenses\ffmpeg\COPYING.LGPLv2.1','licenses\ffmpeg\LICENSE-dav1d.txt','licenses\ffmpeg\LICENSE-zlib.txt',
            'licenses\managed\index.json','native-provenance.json')
        foreach ($File in $Required) {
            $Path = Join-Path $PackageDir $File
            Assert-Release (Test-Path -LiteralPath $Path -PathType Leaf) "Missing required full-package file: $File"
            Assert-Release ((Get-Item -LiteralPath $Path).Length -gt 0) "Empty package file: $File"
        }
        foreach ($App in @('WAIApp','wai','wai-embed')) {
            $Config = Get-Content -LiteralPath (Join-Path $PackageDir "$App.runtimeconfig.json") -Raw | ConvertFrom-Json
            Assert-Release ($Config.runtimeOptions.tfm -eq 'net10.0') "$App does not target .NET 10."
            $Frameworks = @($Config.runtimeOptions.includedFrameworks) + @($Config.runtimeOptions.frameworks) + @($Config.runtimeOptions.framework)
            Assert-Release (@($Frameworks | Where-Object { $_ -and $_.version -match '^10\.' }).Count -gt 0) "$App runtime configuration has no .NET 10 framework."
        }
        $RuntimeVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $PackageDir 'System.Private.CoreLib.dll')).Version
        Assert-Release ($RuntimeVersion.Major -eq 10) 'Packaged runtime binary is not .NET 10.'
        'Full payload and .NET 10 runtime verified.'
    }
    Invoke-Gate 'third-party-notices-and-corresponding-source' {
        $Manifest = Get-Content -LiteralPath (Join-Path $PackageDir 'native-provenance.json') -Raw | ConvertFrom-Json
        foreach ($File in $Manifest.files) {
            Assert-Release ((Get-FileHash -LiteralPath (Join-Path $PackageDir $File.name)).Hash -eq $File.sha256) "Native provenance mismatch: $($File.name)"
        }
        $VcNoticeIndex = Get-Content -LiteralPath (Join-Path $PackageDir 'licenses\vcruntime\index.json') -Raw | ConvertFrom-Json
        foreach ($File in $VcNoticeIndex.files) {
            Assert-Release ((Get-FileHash -LiteralPath (Join-Path $PackageDir "licenses\vcruntime\$($File.name)")).Hash -eq $File.sha256) "VC runtime notice mismatch: $($File.name)"
        }
        $Index = Get-Content -LiteralPath (Join-Path $PackageDir 'licenses\managed\index.json') -Raw | ConvertFrom-Json
        Assert-Release ($Index.unresolved_count -eq 0) 'Managed notices remain unresolved.'
        Assert-Release (@($Index.runtime_notice_check.missing_from_runtime_pack_cache).Count -eq 0) 'Missing .NET runtime notices.'
        $SourceZipPath = Join-Path $ReleasesDir 'ffmpeg-corresponding-source.zip'
        Assert-Release (Test-Path -LiteralPath $SourceZipPath -PathType Leaf) 'Missing matching media source distribution.'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $SourceZip = [IO.Compression.ZipFile]::OpenRead($SourceZipPath)
        try {
            foreach ($Pair in @(@('ffmpegSource','tar.xz'),@('dav1dSource','tar.gz'),@('zlibSource','tar.gz'))) {
                $Source = $Manifest.dependencies.($Pair[0])
                $Name = ($Pair[0] -replace 'Source$','') + '-' + $Source.version + '.' + $Pair[1]
                $Entries = @($SourceZip.Entries | Where-Object { $_.FullName -eq $Name })
                Assert-Release ($Entries.Count -eq 1) "Source archive missing: $Name"
                $Stream = $Entries[0].Open(); $Hasher = [Security.Cryptography.SHA256]::Create()
                try { $Hash = ([BitConverter]::ToString($Hasher.ComputeHash($Stream))).Replace('-','') }
                finally { $Stream.Dispose(); $Hasher.Dispose() }
                Assert-Release ($Hash -eq $Source.sha256) "Corresponding-source hash mismatch: $Name"
            }
            foreach ($Name in @('scripts/build-ffmpeg.sh','native-dependencies.json','README.md')) {
                Assert-Release (@($SourceZip.Entries | Where-Object { $_.FullName -eq $Name }).Count -eq 1) "Source rebuild instruction missing: $Name"
            }
        } finally { $SourceZip.Dispose() }
        'Native hashes, managed/runtime notices and exact FFmpeg/dav1d/zlib source archives verified.'
    }
    Invoke-Gate 'release-sha256-and-portable-payload' {
        $ChecksumPath = Join-Path $ReleasesDir 'SHA256SUMS.txt'
        Assert-Release (Test-Path -LiteralPath $ChecksumPath -PathType Leaf) 'Missing release SHA256SUMS.txt.'
        $Count = 0
        foreach ($Line in Get-Content -LiteralPath $ChecksumPath) {
            if ([string]::IsNullOrWhiteSpace($Line)) { continue }
            Assert-Release ($Line -match '^([a-fA-F0-9]{64})\s+\*?(.+)$') "Malformed checksum entry: $Line"
            $Expected = $Matches[1]; $File = $Matches[2]
            Assert-Release ([IO.Path]::GetFileName($File) -eq $File -and $File -ne '..') 'Checksum entries must be filenames within releases.'
            $Path = Join-Path $ReleasesDir $File
            Assert-Release (Test-Path -LiteralPath $Path -PathType Leaf) "Checksummed artifact missing: $File"
            Assert-Release ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected) "Checksum mismatch: $File"
            $Count++
        }
        Assert-Release ($Count -gt 0) 'Checksum manifest is empty.'
        $Portable = Join-Path $ReleasesDir 'WinSearch.WAI-win-Portable.zip'
        Assert-Release (Test-Path -LiteralPath $Portable -PathType Leaf) 'Missing portable release archive.'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $Zip = [IO.Compression.ZipFile]::OpenRead($Portable)
        try {
            foreach ($File in @('WAIApp.exe','wai.exe','wai-embed.exe','WAI.Core.dll','ffmpeg.exe','ffprobe.exe','pdfium.dll','System.Private.CoreLib.dll')) {
                # The portable root can also contain the main executable's launcher stub.
                # Compare the real current/ payload, not that launcher.
                $Entry = @($Zip.Entries | Where-Object { $_.FullName.Replace('\','/') -eq "current/$File" })
                Assert-Release ($Entry.Count -eq 1) "Portable archive must contain one current/$File payload."
                $Stream = $Entry[0].Open(); $Hasher = [Security.Cryptography.SHA256]::Create()
                try { $Actual = ([BitConverter]::ToString($Hasher.ComputeHash($Stream))).Replace('-','') }
                finally { $Stream.Dispose(); $Hasher.Dispose() }
                Assert-Release ($Actual -eq (Get-FileHash -LiteralPath (Join-Path $PackageDir $File) -Algorithm SHA256).Hash) "Portable payload differs from tested package: $File"
            }
        } finally { $Zip.Dispose() }
        "$Count release checksums and portable executable/native/runtime payload verified."
    }
    $Workspace = Join-Path ([IO.Path]::GetTempPath()) ('WAI-package-gate-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $Workspace | Out-Null
    $env:WAI_HOME = Join-Path $Workspace 'home'
    $env:WAI_INDEX = Join-Path $Workspace 'index'
    $env:WAI_MODELS = Join-Path $Workspace 'models'
    $env:WAI_FFMPEG = $PackageDir
    $env:WAI_MODEL = $Model
    $env:WAI_BACKEND = 'cpu'
    foreach ($Dir in @($env:WAI_HOME,$env:WAI_INDEX,$env:WAI_MODELS)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
    $env:DOTNET_ROOT = Split-Path -Parent $Dotnet
    $env:DOTNET_CLI_HOME = Join-Path $RepoRoot 'tools\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $RepoRoot 'tools\nuget'
    Invoke-Gate 'isolate-local-models' {
        # Hardlinks avoid duplicating model data on the same volume. Cross-volume runs copy immutable fixtures.
        $Files = @()
        $Selected = switch ($Model) { 'bge-m3' { 'bge-m3-Q4_K_M.gguf' }; 'nomic' { 'nomic-embed-text-v1.5.Q8_0.gguf' }; 'gemma2' { 'embeddinggemma-2-Q8_0.gguf' }; default { throw "Unknown model: $Model" } }
        $Files = @($Selected)
        if ($Model -eq 'gemma2') { $Files += 'mmproj-embeddinggemma-2-Q8_0.gguf' }
        foreach ($File in $Files) {
            $Source = Join-Path $ModelsDir $File; $Target = Join-Path $env:WAI_MODELS $File
            Assert-Release (Test-Path -LiteralPath $Source -PathType Leaf) "Required local model is missing: $File"
            try { New-Item -ItemType HardLink -Path $Target -Target $Source -ErrorAction Stop | Out-Null }
            catch { Copy-Item -LiteralPath $Source -Destination $Target }
        }
        "Isolated model path: $env:WAI_MODELS. No downloads."
    }
    Invoke-Gate 'packaged-cli' {
        $Documents = Join-Path $Workspace 'documents'; New-Item -ItemType Directory -Force -Path $Documents | Out-Null
        [IO.File]::WriteAllText((Join-Path $Documents 'invoice.txt'), 'Release package invoice GATE483716. Delivery of laboratory equipment.', [Text.UTF8Encoding]::new($false))
        $Cli = Join-Path $PackageDir 'wai.exe'
        $Estimate = & $Cli estimate $Documents --json 2>&1
        Assert-Release ($LASTEXITCODE -eq 0) "Packaged CLI estimate failed: $Estimate"
        Assert-Release (($Estimate | Out-String | ConvertFrom-Json).files -eq 1) 'CLI crawler did not discover the isolated document.'
        $Indexed = & $Cli index $Documents --index $env:WAI_INDEX --workers 1 2>&1
        Assert-Release ($LASTEXITCODE -eq 0) "Packaged CLI index failed: $Indexed"
        $Search = & $Cli search GATE483716 --index $env:WAI_INDEX --json 2>&1
        Assert-Release ($LASTEXITCODE -eq 0) "Packaged CLI search failed: $Search"
        Assert-Release (($Search | Out-String) -match 'invoice\.txt') "Packaged CLI lost expected document: $Search"
        $Status = & $Cli status --index $env:WAI_INDEX 2>&1
        Assert-Release ($LASTEXITCODE -eq 0) "Packaged CLI status failed: $Status"
        @($Estimate,$Indexed,$Search,$Status) | Out-File -LiteralPath (Join-Path $ReportDir 'cli-output.txt') -Encoding utf8
        "Real packaged CLI estimate/index/search/status passed with isolated home/index/models: $Workspace"
    }
    Invoke-Gate 'release-smoke' {
        $Project = Join-Path $RepoRoot 'checks\WAI.ReleaseSmoke\WAI.ReleaseSmoke.csproj'
        $HarnessOutput = Join-Path $Workspace 'harness-build'
        # A self-contained runtime needs a self-contained dependency manifest as well.
        # Merely replacing a framework-dependent runtimeconfig leaves CoreCLR unresolved.
        $BuildLog = & $Dotnet publish $Project -c Release -r win-x64 --self-contained true -o $HarnessOutput --nologo -v quiet "-p:RestoreConfigFile=$(Join-Path $RepoRoot 'NuGet.Config')" 2>&1
        $BuildLog | Out-File -LiteralPath (Join-Path $ReportDir 'build.log') -Encoding utf8
        Assert-Release ($LASTEXITCODE -eq 0) 'Release smoke harness build failed; see build.log.'
        $Runner = Join-Path $Workspace 'harness'
        New-Item -ItemType Directory -Path $Runner | Out-Null
        # Exercise exactly the shipped Core, SQLite, runtime and dependencies even if source
        # was edited after packaging. Only the executable test driver comes from this build.
        Get-ChildItem -LiteralPath $PackageDir | Copy-Item -Destination $Runner -Recurse
        foreach ($Name in @('WAI.ReleaseSmoke.exe','WAI.ReleaseSmoke.dll','WAI.ReleaseSmoke.deps.json')) {
            Copy-Item -LiteralPath (Join-Path $HarnessOutput $Name) -Destination (Join-Path $Runner $Name)
        }
        Copy-Item -LiteralPath (Join-Path $PackageDir 'wai.runtimeconfig.json') -Destination (Join-Path $Runner 'WAI.ReleaseSmoke.runtimeconfig.json')
        $Harness = Join-Path $Runner 'WAI.ReleaseSmoke.exe'
        $Arguments = @('--package',$PackageDir,'--models',$env:WAI_MODELS,'--model',$Model,'--report',(Join-Path $ReportDir 'smoke.json'))
        if ($Native) { $Arguments += '--native' }
        if (!$SkipEvaluation) { $Arguments += @('--eval',(Join-Path $RepoRoot 'testbed-production.json')) }
        & $Harness @Arguments
        Assert-Release ($LASTEXITCODE -eq 0) 'Release smoke failed; see smoke.json and its isolated workspace.'
        $Smoke = Get-Content -LiteralPath (Join-Path $ReportDir 'smoke.json') -Raw | ConvertFrom-Json
        Assert-Release ($Smoke.loadedCoreSha256 -eq (Get-FileHash -LiteralPath (Join-Path $PackageDir 'WAI.Core.dll') -Algorithm SHA256).Hash) 'Harness did not load the tested packaged Core binary.'
        'File lifecycle, real CPU worker, crash recovery, constrained SQLite disk-full and requested native/evaluation checks passed.'
    }
} catch { $ExitCode = 1; Write-Warning $_.ToString() }
finally {
    foreach ($Name in $SavedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($Name, $SavedEnvironment[$Name], 'Process')
    }
    $Summary = [ordered]@{status=$(if($ExitCode -eq 0){'passed'}else{'failed'});startedAtUtc=$Started;finishedAtUtc=[DateTimeOffset]::UtcNow;packageDir=$PackageDir;checks=$Checks;
        limitations=@('Not an installed-application UI test.','CPU mode verified; no GPU vendor matrix claim.','Actual updater application is a separate baseline-to-target gate.','Synthetic corpus results do not establish real-world retrieval accuracy.');nativeRequested=[bool]$Native;evaluationRequested=(!$SkipEvaluation)}
    $Summary | ConvertTo-Json -Depth 10 | Out-File -LiteralPath (Join-Path $ReportDir 'release-gate.json') -Encoding utf8
}
exit $ExitCode
