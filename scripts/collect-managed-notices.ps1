[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$NuGetPackages,

    [string]$OutputDir,

    [string]$LicenseOverridesDir,

    [string[]]$ExcludeDeps = @('WAI.ReleaseSmoke.deps.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$repositoryRoot = Split-Path -Parent $scriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repositoryRoot 'state/managed-notices'
}
if ([string]::IsNullOrWhiteSpace($LicenseOverridesDir)) {
    $LicenseOverridesDir = Join-Path $repositoryRoot 'licenses/managed'
}

function Get-NuspecValue {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$Name
    )

    if ($null -eq $Node) { return $null }
    $child = $Node.SelectSingleNode("./*[local-name()='$Name']")
    if ($null -eq $child) { return $null }
    $value = [string]$child.InnerText
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

function Read-Nuspec {
    param([string]$Path)

    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = New-Object System.Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) { throw 'nuspec-metadata-element-missing' }

    $licenseNode = $metadata.SelectSingleNode("./*[local-name()='license']")
    $licenseType = $null
    $licenseValue = $null
    if ($null -ne $licenseNode) {
        $licenseType = [string]$licenseNode.GetAttribute('type')
        $licenseValue = [string]$licenseNode.InnerText
        if ([string]::IsNullOrWhiteSpace($licenseValue)) { $licenseValue = $null }
        else { $licenseValue = $licenseValue.Trim() }
    }

    return [pscustomobject]@{
        Id = Get-NuspecValue -Node $metadata -Name 'id'
        Version = Get-NuspecValue -Node $metadata -Name 'version'
        RepositoryUrl = if ($metadata.SelectSingleNode("./*[local-name()='repository']")) {
            [string]$metadata.SelectSingleNode("./*[local-name()='repository']").GetAttribute('url')
        } else { $null }
        ProjectUrl = Get-NuspecValue -Node $metadata -Name 'projectUrl'
        LicenseType = $licenseType
        LicenseValue = $licenseValue
        LicenseUrl = Get-NuspecValue -Node $metadata -Name 'licenseUrl'
        Copyright = Get-NuspecValue -Node $metadata -Name 'copyright'
    }
}

function Get-SafeWebUrl {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $candidate = $Value.Trim()
    if ($candidate.StartsWith('git+', [System.StringComparison]::OrdinalIgnoreCase)) {
        $candidate = $candidate.Substring(4)
    }

    $uri = $null
    if (![System.Uri]::TryCreate($candidate, [System.UriKind]::Absolute, [ref]$uri)) { return $null }
    if ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https') { return $null }

    # Do not preserve credentials, query parameters, or fragments from package metadata.
    return '{0}://{1}{2}' -f $uri.Scheme, $uri.Authority, $uri.AbsolutePath.TrimEnd('/')
}

function Get-SafeLicenseExpression {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $expression = $Value.Trim()
    if ($expression -notmatch '^[A-Za-z0-9.+() _-]+$') { return $null }
    return $expression
}

function Get-SafeCopyright {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $copyright = [regex]::Replace($Value.Trim(), '\s+', ' ')
    if ($copyright.Length -gt 1024) { return $null }
    if ($copyright -match '(?i)([A-Z]:\\|\\\\|/Users/|/home/|Bearer\s|(?:token|password|secret)=)') { return $null }
    return $copyright
}

function Resolve-OverrideFile {
    param(
        [string]$OverridesRoot,
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) { return $null }
    if ([System.IO.Path]::IsPathRooted($RelativePath)) { return $null }
    $relative = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar).Replace('\', [System.IO.Path]::DirectorySeparatorChar)
    $rootFull = [System.IO.Path]::GetFullPath($OverridesRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $rootFull $relative))
    if (!$candidate.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    return $null
}

function Get-ValidatedHttpsUrl {
    param(
        [string]$Value,
        [string]$Commit
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $uri = $null
    if (![System.Uri]::TryCreate($Value.Trim(), [System.UriKind]::Absolute, [ref]$uri)) { return $null }
    if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { return $null }
    if ($Commit) {
        if ($Commit -notmatch '^(?:[A-Fa-f0-9]{40}|[A-Fa-f0-9]{64})$') { return $null }
        if ($uri.AbsolutePath.IndexOf($Commit, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { return $null }
    }
    return Get-SafeWebUrl -Value $Value
}

function Get-JsonPropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-SafeSegment {
    param([string]$Value)
    $safe = [regex]::Replace($Value, '[^A-Za-z0-9._-]', '_')
    if ([string]::IsNullOrWhiteSpace($safe) -or $safe -eq '.' -or $safe -eq '..') { return '_unknown' }
    return $safe
}

function Get-PackageRoot {
    param(
        [string]$PackagesRoot,
        [string]$PackageId,
        [string]$PackageVersion
    )

    $idRoot = Join-Path $PackagesRoot $PackageId.ToLowerInvariant()
    if (!(Test-Path -LiteralPath $idRoot -PathType Container)) { return $null }

    $versionRoot = Join-Path $idRoot $PackageVersion.ToLowerInvariant()
    if (Test-Path -LiteralPath $versionRoot -PathType Container) { return (Get-Item -LiteralPath $versionRoot).FullName }

    # NuGet normalizes package-cache directory names. Search only this package's cache subtree.
    foreach ($directory in @(Get-ChildItem -LiteralPath $idRoot -Directory -ErrorAction SilentlyContinue)) {
        $candidateNuspecs = @(Get-ChildItem -LiteralPath $directory.FullName -Filter '*.nuspec' -File -ErrorAction SilentlyContinue)
        foreach ($candidate in $candidateNuspecs) {
            try {
                $candidateMetadata = Read-Nuspec -Path $candidate.FullName
                if ($candidateMetadata.Id -and $candidateMetadata.Version -and
                    $candidateMetadata.Id.Equals($PackageId, [System.StringComparison]::OrdinalIgnoreCase) -and
                    $candidateMetadata.Version.Equals($PackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $directory.FullName
                }
            }
            catch { }
        }
    }
    return $null
}

function Resolve-PackageFile {
    param(
        [string]$PackageRoot,
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($PackageRoot) -or [string]::IsNullOrWhiteSpace($RelativePath)) { return $null }
    $relative = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar).Replace('\', [System.IO.Path]::DirectorySeparatorChar)
    if ([System.IO.Path]::IsPathRooted($relative)) { return $null }

    $rootFull = [System.IO.Path]::GetFullPath($PackageRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $rootFull $relative))
    if (!$candidate.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    return $null
}

function Get-LegalFiles {
    param([string]$PackageRoot)

    if ([string]::IsNullOrWhiteSpace($PackageRoot) -or !(Test-Path -LiteralPath $PackageRoot -PathType Container)) { return @() }
    $legalName = '^(license|licence|copying|notice|patents|third[._ -]?party[._ -]?notices)([._-].*)?$'
    $results = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -ErrorAction SilentlyContinue)) {
        if ($file.Name -notmatch $legalName) { continue }
        $extension = $file.Extension.ToLowerInvariant()
        if ($extension -and $extension -notin @('.txt', '.md', '.rst', '.html', '.htm', '.xml')) { continue }
        if ($file.Length -gt 5MB) { continue }
        $relative = $file.FullName.Substring($PackageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar).Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
        $results += [pscustomobject]@{ FullName = $file.FullName; RelativePath = $relative }
    }
    return $results
}

function Get-LegalFileKind {
    param([string]$RelativePath)
    $name = [System.IO.Path]::GetFileName($RelativePath)
    if ($name -match '^(?i:license|licence|copying)([._-].*)?$') { return 'license' }
    if ($name -match '^(?i:third[._ -]?party[._ -]?notices)([._-].*)?$') { return 'third-party-notice' }
    if ($name -match '^(?i:notice)([._-].*)?$') { return 'notice' }
    if ($name -match '^(?i:patents)([._-].*)?$') { return 'patents' }
    return 'other-legal-text'
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$publishRoot = [System.IO.Path]::GetFullPath($PublishDir)
$packagesRoot = [System.IO.Path]::GetFullPath($NuGetPackages)
$outputRoot = [System.IO.Path]::GetFullPath($OutputDir)
$overridesRoot = [System.IO.Path]::GetFullPath($LicenseOverridesDir)
if (!(Test-Path -LiteralPath $publishRoot -PathType Container)) { throw "PublishDir not found: $publishRoot" }
if (!(Test-Path -LiteralPath $packagesRoot -PathType Container)) { throw "NuGetPackages not found: $packagesRoot" }
if ($publishRoot -eq $outputRoot -or $outputRoot.StartsWith($publishRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDir must not be inside or replace the publish directory.'
}

$excludeSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in $ExcludeDeps) { if (![string]::IsNullOrWhiteSpace($name)) { [void]$excludeSet.Add([System.IO.Path]::GetFileName($name)) } }

$overrideIndexPath = Join-Path $overridesRoot 'overrides.json'
$overrideRecords = @()
$overrideIndexIssues = @()
$overrideIndexPresent = Test-Path -LiteralPath $overrideIndexPath -PathType Leaf
if ($overrideIndexPresent) {
    $overrideIndexData = Get-Content -LiteralPath $overrideIndexPath -Raw | ConvertFrom-Json
    $schemaVersion = Get-JsonPropertyValue -Object $overrideIndexData -Name 'schema_version'
    if ($schemaVersion -ne 1) { throw 'Unsupported licenses/managed/overrides.json schema_version.' }
    $licenseListProperty = $overrideIndexData.PSObject.Properties['licenses']
    if ($null -eq $licenseListProperty -or $licenseListProperty.Value -isnot [System.Array]) { throw 'overrides.json must contain a licenses array.' }
    $licenseList = $licenseListProperty.Value

    $recordNumber = 0
    foreach ($rawOverride in $licenseList) {
        $recordNumber++
        $id = [string](Get-JsonPropertyValue -Object $rawOverride -Name 'id')
        $version = [string](Get-JsonPropertyValue -Object $rawOverride -Name 'version')
        $relativePath = [string](Get-JsonPropertyValue -Object $rawOverride -Name 'path')
        $sha256 = [string](Get-JsonPropertyValue -Object $rawOverride -Name 'sha256')
        $url = [string](Get-JsonPropertyValue -Object $rawOverride -Name 'url')
        $commit = [string](Get-JsonPropertyValue -Object $rawOverride -Name 'commit')

        if ($id -notmatch '^[A-Za-z0-9._-]+$' -or $version -notmatch '^[A-Za-z0-9.+_-]+$') {
            $overrideIndexIssues += [pscustomobject]@{ record = $recordNumber; reason = 'package-id-or-version-invalid' }
            continue
        }
        $recordIssue = $null
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [System.IO.Path]::IsPathRooted($relativePath) -or @($relativePath -split '[\\/]+' | Where-Object { $_ -eq '..' }).Count -gt 0) {
            $recordIssue = 'override-path-must-be-relative'
        }
        elseif ($sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            $recordIssue = 'override-sha256-invalid'
        }
        elseif ($commit -and $commit -notmatch '^(?:[A-Fa-f0-9]{40}|[A-Fa-f0-9]{64})$') {
            $recordIssue = 'upstream-commit-invalid'
        }
        elseif (!(Get-ValidatedHttpsUrl -Value $url -Commit $commit)) {
            $recordIssue = 'upstream-url-must-be-https-without-credentials-or-query'
        }

        if ($recordIssue) {
            $overrideIndexIssues += [pscustomobject]@{ record = $recordNumber; reason = $recordIssue }
        }
        $overrideRecords += [pscustomobject]@{
            id = $id
            version = $version
            path = $relativePath
            sha256 = $sha256
            url = $url
            commit = $commit
            validation_error = $recordIssue
        }
    }
}

$depsFiles = @(Get-ChildItem -LiteralPath $publishRoot -Filter '*.deps.json' -File -Recurse | Where-Object { !$excludeSet.Contains($_.Name) } | Sort-Object FullName)
if ($depsFiles.Count -eq 0) { throw "No application .deps.json files found under PublishDir (excluded: $($ExcludeDeps -join ', '))." }

$apps = @()
$packageIndex = @{}
$runtimePacks = @{}
foreach ($depsFile in $depsFiles) {
    $deps = Get-Content -LiteralPath $depsFile.FullName -Raw | ConvertFrom-Json
    $appName = [System.IO.Path]::GetFileNameWithoutExtension([System.IO.Path]::GetFileNameWithoutExtension($depsFile.Name))
    $targetName = [string]$deps.runtimeTarget.name
    $appPackageKeys = @()

    foreach ($libraryProperty in $deps.libraries.PSObject.Properties) {
        $library = $libraryProperty.Value
        $libraryKey = [string]$libraryProperty.Name
        $slash = $libraryKey.LastIndexOf('/')
        if ($slash -lt 1 -or $slash -ge ($libraryKey.Length - 1)) { continue }
        $id = $libraryKey.Substring(0, $slash)
        $version = $libraryKey.Substring($slash + 1)
        $kind = [string]$library.type

        if ($kind -eq 'runtimepack' -or $id -match '(?i)^runtimepack\.Microsoft\.(NETCore\.App|WindowsDesktop\.App)(\.|$)') {
            if ($id -match '(?i)^runtimepack\.Microsoft\.(NETCore\.App|WindowsDesktop\.App)(\.|$)') {
                $runtimeKey = "$id/$version"
                if (!$runtimePacks.ContainsKey($runtimeKey)) {
                    $runtimePacks[$runtimeKey] = [pscustomobject]@{ id = $id; version = $version; apps = @($appName) }
                }
                elseif ($runtimePacks[$runtimeKey].apps -notcontains $appName) {
                    $runtimePacks[$runtimeKey].apps += $appName
                }
            }
            continue
        }

        if ($kind -ne 'package') { continue }
        $key = "$id/$version".ToLowerInvariant()
        if (!$packageIndex.ContainsKey($key)) {
            $packageIndex[$key] = [ordered]@{
                id = $id
                version = $version
                apps = @()
                source = $null
                source_kind = 'not-recorded-in-nuspec'
                license_expression = $null
                license_expression_invalid = $false
                license_type = $null
                license_url = $null
                copyright = $null
                cache_package_found = $false
                nuspec_found = $false
                license_files = @()
                notices = @()
                license_override = $null
                license_override_status = 'not-checked'
                status = 'unresolved'
                unresolved_reason = $null
            }
        }
        if ($packageIndex[$key].apps -notcontains $appName) { $packageIndex[$key].apps += $appName }
        if ($appPackageKeys -notcontains $key) { $appPackageKeys += $key }
    }

    $apps += [pscustomobject]@{
        name = $appName
        deps_file = $depsFile.Name
        runtime_target = $targetName
        packages = @($appPackageKeys | Sort-Object)
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
# Keep reruns deterministic: remove only the two generated subtrees after confirming
# that each resolved target remains inside OutputDir.
$outputPrefix = $outputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($ownedDirectoryName in @('packages', 'runtime', 'license-overrides')) {
    $ownedDirectory = [System.IO.Path]::GetFullPath((Join-Path $outputRoot $ownedDirectoryName))
    if (!$ownedDirectory.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated output directory escaped OutputDir.'
    }
    if (Test-Path -LiteralPath $ownedDirectory) {
        Remove-Item -LiteralPath $ownedDirectory -Recurse -Force
    }
}
$oldIndexPath = Join-Path $outputRoot 'index.json'
if (Test-Path -LiteralPath $oldIndexPath -PathType Leaf) { Remove-Item -LiteralPath $oldIndexPath -Force }

$copiedFiles = @{}
$unresolved = @()
foreach ($key in @($packageIndex.Keys | Sort-Object)) {
    $entry = $packageIndex[$key]
    $packageRoot = Get-PackageRoot -PackagesRoot $packagesRoot -PackageId $entry.id -PackageVersion $entry.version
    if ($packageRoot) { $entry.cache_package_found = $true }
    if (!$packageRoot) {
        $entry.unresolved_reason = 'package-not-found-in-nuget-cache'
    }
    else {
        $nuspecCandidates = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File -ErrorAction SilentlyContinue)
        $nuspecPath = $nuspecCandidates | Where-Object { $_.BaseName.Equals($entry.id, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (!$nuspecPath -and $nuspecCandidates.Count -eq 1) { $nuspecPath = $nuspecCandidates[0] }
        if (!$nuspecPath) {
            $entry.unresolved_reason = 'nuspec-not-found-in-package-cache'
        }
        else {
            try {
                $metadata = Read-Nuspec -Path $nuspecPath.FullName
                if (!$metadata.Id -or !$metadata.Version -or
                    !$metadata.Id.Equals($entry.id, [System.StringComparison]::OrdinalIgnoreCase) -or
                    !$metadata.Version.Equals($entry.version, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $entry.unresolved_reason = 'nuspec-id-or-version-mismatch'
                }
                else {
                    $entry.nuspec_found = $true
                    $entry.source = Get-SafeWebUrl -Value $metadata.RepositoryUrl
                    if ($entry.source) { $entry.source_kind = 'repository' }
                    else {
                        $entry.source = Get-SafeWebUrl -Value $metadata.ProjectUrl
                        if ($entry.source) { $entry.source_kind = 'project' }
                    }
                    $entry.license_type = if ($metadata.LicenseType) { $metadata.LicenseType.ToLowerInvariant() } else { $null }
                    $entry.license_url = Get-SafeWebUrl -Value $metadata.LicenseUrl
                    $entry.copyright = Get-SafeCopyright -Value $metadata.Copyright
                    if ($entry.license_type -eq 'expression') {
                        $entry.license_expression = Get-SafeLicenseExpression -Value $metadata.LicenseValue
                        $entry.license_expression_invalid = [bool]($metadata.LicenseValue -and !$entry.license_expression)
                    }
                    elseif ($entry.license_type -eq 'file') { $entry.license_expression = $null }
                    elseif ($metadata.LicenseValue) {
                        $entry.license_expression = Get-SafeLicenseExpression -Value $metadata.LicenseValue
                        $entry.license_expression_invalid = !$entry.license_expression
                    }

                    $legalFiles = @(Get-LegalFiles -PackageRoot $packageRoot)
                    $declaredLicense = $null
                    if ($entry.license_type -eq 'file' -and $metadata.LicenseValue) {
                        $declaredLicense = Resolve-PackageFile -PackageRoot $packageRoot -RelativePath $metadata.LicenseValue
                        if ($declaredLicense) {
                            $declaredRelative = $declaredLicense.Substring($packageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar).Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
                            if (!($legalFiles | Where-Object { $_.FullName -eq $declaredLicense })) {
                                $legalFiles += [pscustomobject]@{ FullName = $declaredLicense; RelativePath = $declaredRelative }
                            }
                        }
                    }

                    $destinationLicenseFiles = @()
                    $destinationNotices = @()
                    foreach ($legalFile in $legalFiles) {
                        $relative = $legalFile.RelativePath.Replace('\', '/')
                        $destinationRelative = 'packages/{0}/{1}/{2}' -f (Get-SafeSegment $entry.id), (Get-SafeSegment $entry.version), $relative
                        $destinationPath = Join-Path $outputRoot ($destinationRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                        $destinationParent = Split-Path -Parent $destinationPath
                        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
                        Copy-Item -LiteralPath $legalFile.FullName -Destination $destinationPath -Force
                        $fileKind = Get-LegalFileKind -RelativePath $relative
                        if ($declaredLicense -and $legalFile.FullName -eq $declaredLicense) { $fileKind = 'license' }
                        $fileEntry = [pscustomobject]@{
                            kind = $fileKind
                            package_path = $relative
                            output_path = $destinationRelative
                            sha256 = Get-Sha256 -Path $legalFile.FullName
                        }
                        if ($fileKind -eq 'license') { $destinationLicenseFiles += $fileEntry }
                        else { $destinationNotices += $fileEntry }
                        $copiedFiles[$destinationRelative] = $true
                    }
                    $entry.license_files = @($destinationLicenseFiles | Sort-Object output_path -Unique)
                    $entry.notices = @($destinationNotices | Sort-Object output_path -Unique)

                    if ($entry.license_expression_invalid) {
                        $entry.unresolved_reason = 'license-expression-contains-unsupported-content'
                    }
                    elseif ($entry.license_type -eq 'file' -and !$declaredLicense) {
                        $entry.unresolved_reason = 'declared-license-file-missing-from-package'
                    }
                    elseif ($entry.license_type -eq 'file' -and $declaredLicense) {
                        $entry.status = 'resolved'
                    }
                    elseif (!$entry.license_expression -and !$entry.license_url) {
                        $entry.unresolved_reason = 'license-metadata-missing-in-nuspec'
                    }
                    elseif ($entry.license_files.Count -eq 0) {
                        if ($entry.license_url) { $entry.unresolved_reason = 'license-url-present-package-text-not-in-cache' }
                        else { $entry.unresolved_reason = 'license-text-not-in-package-cache' }
                    }
                    else {
                        $entry.status = 'resolved'
                    }
                }
            }
            catch {
                $entry.unresolved_reason = 'nuspec-parse-failed'
            }
        }
    }

    if ($entry.status -ne 'resolved') {
        $unresolved += [pscustomobject]@{
            id = $entry.id
            version = $entry.version
            reason = $entry.unresolved_reason
        }
    }
}

$overrideCopiedFiles = @{}
foreach ($key in @($packageIndex.Keys | Sort-Object)) {
    $entry = $packageIndex[$key]
    $matchingOverrides = @($overrideRecords | Where-Object {
        $_.id.Equals($entry.id, [System.StringComparison]::OrdinalIgnoreCase) -and
        $_.version.Equals($entry.version, [System.StringComparison]::Ordinal)
    })

    if ($entry.status -eq 'resolved') {
        $entry.license_override_status = if ($matchingOverrides.Count -gt 0) { 'not-needed-package-has-license-text' } else { 'not-needed' }
        continue
    }
    if ($matchingOverrides.Count -eq 0) {
        $entry.license_override_status = 'no-exact-id-version-match'
        continue
    }
    if ($matchingOverrides.Count -gt 1) {
        $entry.license_override_status = 'duplicate-exact-id-version-entries'
        $entry.unresolved_reason = 'license-override-duplicate-exact-id-version-entries'
        continue
    }

    $override = $matchingOverrides[0]
    if ($override.validation_error) {
        $entry.license_override_status = $override.validation_error
        $entry.unresolved_reason = 'license-override-' + $override.validation_error
        continue
    }

    $overridePath = Resolve-OverrideFile -OverridesRoot $overridesRoot -RelativePath $override.path
    if (!$overridePath) {
        $entry.license_override_status = 'file-not-found-or-outside-overrides-dir'
        $entry.unresolved_reason = 'license-override-file-not-found-or-outside-overrides-dir'
        continue
    }
    $actualOverrideHash = Get-Sha256 -Path $overridePath
    if (!$actualOverrideHash.Equals($override.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        $entry.license_override_status = 'sha256-mismatch'
        $entry.unresolved_reason = 'license-override-sha256-mismatch'
        continue
    }

    $validatedUrl = Get-ValidatedHttpsUrl -Value $override.url -Commit $override.commit
    if (!$validatedUrl) {
        $entry.license_override_status = 'upstream-url-invalid'
        $entry.unresolved_reason = 'license-override-upstream-url-invalid'
        continue
    }

    $relativeSourcePath = $override.path.Replace('\', '/')
    $fileName = Get-SafeSegment -Value ([System.IO.Path]::GetFileName($relativeSourcePath))
    $destinationRelative = 'license-overrides/{0}/{1}/{2}' -f (Get-SafeSegment $entry.id), (Get-SafeSegment $entry.version), $fileName
    $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $outputRoot ($destinationRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
    if (!$destinationPath.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source-license output path escaped OutputDir.'
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
    Copy-Item -LiteralPath $overridePath -Destination $destinationPath -Force
    $copiedOverrideHash = Get-Sha256 -Path $destinationPath
    if (!$copiedOverrideHash.Equals($override.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $destinationPath -Force
        $entry.license_override_status = 'copied-file-sha256-mismatch'
        $entry.unresolved_reason = 'license-override-copied-file-sha256-mismatch'
        continue
    }

    $entry.license_override = [pscustomobject]@{
        kind = 'license-override'
        path = $relativeSourcePath
        sha256 = $copiedOverrideHash
        url = $validatedUrl
        commit = if ($override.commit) { $override.commit.ToLowerInvariant() } else { $null }
        output_path = $destinationRelative
        verification_status = 'sha256-matched'
    }
    $entry.license_override_status = 'verified'
    $entry.status = 'resolved'
    $entry.unresolved_reason = $null
    $overrideCopiedFiles[$destinationRelative] = $true
}

$unresolved = @()
foreach ($entry in $packageIndex.Values) {
    if ($entry.status -ne 'resolved') {
        $unresolved += [pscustomobject]@{
            id = $entry.id
            version = $entry.version
            reason = $entry.unresolved_reason
        }
    }
}

$runtimePackList = @($runtimePacks.Values | Sort-Object id, version)
$expectedRuntimeNotices = @('LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')
$runtimeNoticeFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '^(?i:LICENSE\.txt|THIRD[-_. ]?PARTY[-_. ]?NOTICES\.txt)$'
} | ForEach-Object {
    $relative = $_.FullName.Substring($publishRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar).Length).TrimStart([System.IO.Path]::DirectorySeparatorChar).Replace('\', '/')
    [pscustomobject]@{ path = $relative; name = $_.Name }
})
$foundRuntimeNames = @($runtimeNoticeFiles | ForEach-Object { $_.name.ToLowerInvariant() })
$missingRuntimeNotices = @($expectedRuntimeNotices | Where-Object { $foundRuntimeNames -notcontains $_.ToLowerInvariant() })
$runtimePublishStatus = if ($runtimePackList.Count -eq 0) { 'not-applicable' } elseif ($missingRuntimeNotices.Count -eq 0) { 'present' } else { 'missing' }
$runtimePackMaterials = @()
$runtimeCopiedFiles = @{}
foreach ($runtimePack in $runtimePackList) {
    $cacheId = $runtimePack.id -replace '(?i)^runtimepack\.', ''
    $packageRoot = Get-PackageRoot -PackagesRoot $packagesRoot -PackageId $cacheId -PackageVersion $runtimePack.version
    $runtimeEntry = [ordered]@{
        id = $runtimePack.id
        cache_package_id = $cacheId
        version = $runtimePack.version
        apps = $runtimePack.apps
        cache_package_found = [bool]$packageRoot
        copyright = $null
        license_files = @()
        notices = @()
        status = 'unresolved'
        unresolved_reason = $null
    }
    if (!$packageRoot) {
        $runtimeEntry.unresolved_reason = 'runtime-package-not-found-in-nuget-cache'
    }
    else {
        $runtimeNuspecs = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File -ErrorAction SilentlyContinue)
        $runtimeNuspec = $runtimeNuspecs | Where-Object { $_.BaseName.Equals($cacheId, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (!$runtimeNuspec -and $runtimeNuspecs.Count -eq 1) { $runtimeNuspec = $runtimeNuspecs[0] }
        if ($runtimeNuspec) {
            try {
                $runtimeMetadata = Read-Nuspec -Path $runtimeNuspec.FullName
                if ($runtimeMetadata.Id -and $runtimeMetadata.Version -and
                    $runtimeMetadata.Id.Equals($cacheId, [System.StringComparison]::OrdinalIgnoreCase) -and
                    $runtimeMetadata.Version.Equals($runtimePack.version, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $runtimeEntry.copyright = Get-SafeCopyright -Value $runtimeMetadata.Copyright
                }
            }
            catch { }
        }
        $legalFiles = @(Get-LegalFiles -PackageRoot $packageRoot)
        $runtimeLicenses = @()
        $runtimeNotices = @()
        foreach ($legalFile in $legalFiles) {
            $relative = $legalFile.RelativePath.Replace('\', '/')
            $destinationRelative = 'runtime/{0}/{1}/{2}' -f (Get-SafeSegment $cacheId), (Get-SafeSegment $runtimePack.version), $relative
            $destinationPath = Join-Path $outputRoot ($destinationRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
            Copy-Item -LiteralPath $legalFile.FullName -Destination $destinationPath -Force
            $fileKind = Get-LegalFileKind -RelativePath $relative
            $fileEntry = [pscustomobject]@{
                kind = $fileKind
                package_path = $relative
                output_path = $destinationRelative
                sha256 = Get-Sha256 -Path $legalFile.FullName
            }
            if ($fileKind -eq 'license') { $runtimeLicenses += $fileEntry }
            else { $runtimeNotices += $fileEntry }
            $runtimeCopiedFiles[$destinationRelative] = $true
        }
        $runtimeEntry.license_files = @($runtimeLicenses | Sort-Object output_path -Unique)
        $runtimeEntry.notices = @($runtimeNotices | Sort-Object output_path -Unique)
        if ($runtimeEntry.license_files.Count -eq 0) {
            $runtimeEntry.unresolved_reason = 'runtime-license-text-not-in-package-cache'
        }
        else {
            $runtimeEntry.status = 'collected'
        }
    }
    $runtimePackMaterials += [pscustomobject]$runtimeEntry
}
$runtimeCacheFileNames = @($runtimePackMaterials | ForEach-Object { @($_.license_files) + @($_.notices) } | ForEach-Object { [System.IO.Path]::GetFileName($_.package_path).ToLowerInvariant() })
$runtimeCacheMissing = @($expectedRuntimeNotices | Where-Object { $runtimeCacheFileNames -notcontains $_.ToLowerInvariant() })
$runtimeNoticeStatus = if ($runtimePackList.Count -eq 0) { 'not-applicable' } elseif ($runtimeCacheMissing.Count -eq 0) { 'collected-from-runtime-pack-cache' } else { 'incomplete' }

$packages = @($packageIndex.Values | Sort-Object id, version)
$index = [ordered]@{
    schema_version = 1
    generated_at_utc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    application_count = $apps.Count
    excluded_deps = @($ExcludeDeps | ForEach-Object { [System.IO.Path]::GetFileName($_) } | Sort-Object -Unique)
    package_count = $packages.Count
    copied_legal_file_count = $copiedFiles.Count + $runtimeCopiedFiles.Count + $overrideCopiedFiles.Count
    unresolved_count = $unresolved.Count
    license_override_index = [ordered]@{
        file = 'overrides.json'
        present = $overrideIndexPresent
        records_read = $overrideRecords.Count
        invalid_record_count = $overrideIndexIssues.Count
        issues = $overrideIndexIssues
    }
    apps = $apps
    packages = $packages
    unresolved = $unresolved
    runtime_notice_check = [ordered]@{
        status = $runtimeNoticeStatus
        publish_status = $runtimePublishStatus
        runtime_packs_excluded_from_managed_inventory = $runtimePackList
        runtime_pack_materials = $runtimePackMaterials
        expected_files = $expectedRuntimeNotices
        found_files = $runtimeNoticeFiles
        missing_from_publish = $missingRuntimeNotices
        missing_from_runtime_pack_cache = $runtimeCacheMissing
        copied_files = @($runtimePackMaterials | ForEach-Object { @($_.license_files) + @($_.notices) })
    }
}

$indexPath = Join-Path $outputRoot 'index.json'
$json = $index | ConvertTo-Json -Depth 30
[System.IO.File]::WriteAllText($indexPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

[pscustomobject]@{
    IndexPath = $indexPath
    Applications = $apps.Count
    ManagedPackages = $packages.Count
    CopiedLegalFiles = ($copiedFiles.Count + $runtimeCopiedFiles.Count + $overrideCopiedFiles.Count)
    VerifiedLicenseOverrides = (@($packageIndex.Values | Where-Object { $_.license_override_status -eq 'verified' }).Count)
    UnresolvedPackages = $unresolved.Count
    RuntimeNoticeStatus = $runtimeNoticeStatus
    RuntimeNoticePublishStatus = $runtimePublishStatus
    RuntimeNoticeMissingInCache = ($runtimeCacheMissing -join ', ')
} | Format-List

if ($unresolved.Count -gt 0) {
    Write-Warning ('Unresolved package license notices: ' + (($unresolved | ForEach-Object { '{0}/{1} ({2})' -f $_.id, $_.version, $_.reason }) -join '; '))
}
if ($overrideIndexIssues.Count -gt 0) {
    Write-Warning ('Invalid license override entries: ' + (($overrideIndexIssues | ForEach-Object { 'record {0} ({1})' -f $_.record, $_.reason }) -join '; '))
}
if ($runtimePublishStatus -eq 'missing') {
    Write-Warning ('Self-contained .NET runtime notices are missing from PublishDir; copied files, when available, are recorded under runtime/ in OutputDir.')
}
if ($runtimeCacheMissing.Count -gt 0) {
    Write-Warning ('Self-contained .NET runtime notice files missing from the runtime-pack cache: ' + ($runtimeCacheMissing -join ', '))
}
