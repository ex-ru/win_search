# Publish a tested package as a draft; make it public only after asset verification.
param(
    [string]$Version = '1.2.1',
    [string]$Repo = 'ex-ru/win_search',
    [string]$ReleasesDir = '',
    [string]$TargetCommitish = '',
    [string]$NotesFile = '',
    [string]$Token = $env:GITHUB_TOKEN
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (!$ReleasesDir) { $ReleasesDir = Join-Path $RepoRoot "dist\$Version\releases" }
$ReleasesDir = [IO.Path]::GetFullPath($ReleasesDir)
if (!$TargetCommitish) { $TargetCommitish = (& git -C $RepoRoot rev-parse HEAD | Out-String).Trim() }
if ($TargetCommitish -notmatch '^[a-fA-F0-9]{40}$') { throw 'TargetCommitish must be the tested full commit SHA.' }
if (!$Token) { throw 'Pass Token or set GITHUB_TOKEN. No credentials are written to disk.' }
if (!$NotesFile -or !(Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw 'Pass NotesFile containing the reviewed release notes.' }
$Notes = Get-Content -LiteralPath $NotesFile -Raw -Encoding UTF8
$Tag = "v$Version"
$ExpectedNames = @('WinSearch.WAI-win-Setup.exe', 'WinSearch.WAI-win-Portable.zip', "WinSearch.WAI-$Version-full.nupkg",
    'ffmpeg-tools-win-x64.zip', 'ffmpeg-corresponding-source.zip', 'native-provenance.json', 'releases.win.json', 'assets.win.json', 'RELEASES', 'SHA256SUMS.txt')
$Files = @($ExpectedNames | ForEach-Object {
    $Path = Join-Path $ReleasesDir $_
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing release artifact: $_" }
    Get-Item -LiteralPath $Path
})
$Headers = @{ Authorization="Bearer $Token"; Accept='application/vnd.github+json'; 'User-Agent'='WAI-Release'; 'X-GitHub-Api-Version'='2022-11-28' }
$Api = "https://api.github.com/repos/$Repo"
function Invoke-Json([string]$Uri, [string]$Method, $Body) {
    $Json = $Body | ConvertTo-Json -Depth 10 -Compress
    Invoke-RestMethod -Uri $Uri -Headers $Headers -Method $Method -Body ([Text.Encoding]::UTF8.GetBytes($Json)) -ContentType 'application/json; charset=utf-8'
}
function Get-Optional([string]$Uri) {
    try { Invoke-RestMethod -Uri $Uri -Headers $Headers }
    catch {
        # The commits endpoint returns 422, rather than 404, for an unknown tag.
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -in @(404,422)) { return $null }
        throw
    }
}
function Assert-Asset($Asset, $File) {
    if ($Asset.state -ne 'uploaded' -or $Asset.size -ne $File.Length) { throw "Asset incomplete: $($File.Name)" }
    if ($Asset.digest) {
        $Digest = 'sha256:' + (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Asset.digest -ne $Digest) { throw "Remote asset digest differs: $($File.Name)" }
    } else { throw "GitHub did not return a SHA-256 digest for $($File.Name); draft retained." }
}
# Verify local checksums before any remote mutation.
foreach ($Line in Get-Content -LiteralPath (Join-Path $ReleasesDir 'SHA256SUMS.txt')) {
    if ([string]::IsNullOrWhiteSpace($Line)) { continue }
    if ($Line -notmatch '^([a-fA-F0-9]{64})\s+\*?(.+)$') { throw "Malformed checksum: $Line" }
    $Hash = $Matches[1]; $Name = $Matches[2]
    if ([IO.Path]::GetFileName($Name) -ne $Name) { throw 'Checksum paths must be local filenames.' }
    if ((Get-FileHash -LiteralPath (Join-Path $ReleasesDir $Name) -Algorithm SHA256).Hash -ne $Hash) { throw "Checksum mismatch: $Name" }
}
$ExistingCommit = Get-Optional "$Api/commits/$Tag"
if ($ExistingCommit -and $ExistingCommit.sha -ne $TargetCommitish) { throw "$Tag already points to another commit; no tag was replaced." }
$Releases = @(Invoke-RestMethod -Uri "$Api/releases?per_page=100" -Headers $Headers)
$Release = @($Releases | Where-Object { $_.tag_name -eq $Tag }) | Select-Object -First 1
if ($Release -and !$Release.draft) { throw "$Tag is already published; public assets were not changed." }
if (!$Release) {
    $Release = Invoke-Json "$Api/releases" 'Post' @{tag_name=$Tag;target_commitish=$TargetCommitish;name="WAI $Version for Windows";body=$Notes;draft=$true;prerelease=$false}
} else {
    $Release = Invoke-Json "$Api/releases/$($Release.id)" 'Patch' @{tag_name=$Tag;target_commitish=$TargetCommitish;body=$Notes;name="WAI $Version for Windows"}
}
$UploadBase = $Release.upload_url -replace '\{.*$', ''
foreach ($File in $Files) {
    $Asset = @($Release.assets | Where-Object { $_.name -eq $File.Name }) | Select-Object -First 1
    if (!$Asset) {
        Write-Host "Uploading $($File.Name) ($($File.Length) bytes)"
        $UploadUrl = "${UploadBase}?name=$([Uri]::EscapeDataString($File.Name))"
        $Asset = Invoke-RestMethod -Uri $UploadUrl -Headers $Headers -Method Post -InFile $File.FullName -ContentType 'application/octet-stream' -TimeoutSec 1800
    }
    Assert-Asset $Asset $File
    Write-Host "Verified $($File.Name)"
}
$Release = Invoke-RestMethod -Uri "$Api/releases/$($Release.id)" -Headers $Headers
foreach ($File in $Files) {
    $Asset = @($Release.assets | Where-Object { $_.name -eq $File.Name }) | Select-Object -First 1
    if (!$Asset) { throw "Missing uploaded artifact: $($File.Name)" }
    Assert-Asset $Asset $File
}
if (!$ExistingCommit) {
    $CreatedRef = Invoke-Json "$Api/git/refs" 'Post' @{ref="refs/tags/$Tag";sha=$TargetCommitish}
    if ($CreatedRef.object.sha -ne $TargetCommitish) { throw 'Created tag does not resolve to the tested commit; draft retained.' }
}
# Keep tag_name explicit on every update. Draft tags without a real Git ref can
# otherwise be published under GitHub's generated untagged-* name.
$Published = Invoke-Json "$Api/releases/$($Release.id)" 'Patch' @{tag_name=$Tag;target_commitish=$TargetCommitish;draft=$false;make_latest='true'}
if ($Published.draft -or $Published.tag_name -ne $Tag) { throw 'Release was not published under the requested tag.' }
Write-Host "Published and verified $($Published.html_url) at $TargetCommitish"
