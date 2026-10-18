[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/Invoke-ExternalCommand.ps1')

function Fail-WebsiteMetadata {
    param([Parameter(Mandatory = $true)][string]$Message)

    throw "Website metadata contract failed: $Message"
}

function Get-JsonObjectProperties {
    param(
        [Parameter(Mandatory = $true)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Fail-WebsiteMetadata "$Description must be a JSON object."
    }

    $properties = [System.Collections.Specialized.OrderedDictionary]::new([System.StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if ($properties.Contains($property.Name)) {
            Fail-WebsiteMetadata "$Description contains duplicate property '$($property.Name)'."
        }

        $properties.Add($property.Name, $property.Value)
    }

    return ,$properties
}

function Assert-JsonPropertySet {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Properties,
        [Parameter(Mandatory = $true)][string[]]$ExpectedNames,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Properties.Count -ne $ExpectedNames.Count) {
        Fail-WebsiteMetadata "$Description has unexpected or missing properties."
    }

    foreach ($name in $ExpectedNames) {
        if (-not $Properties.Contains($name)) {
            Fail-WebsiteMetadata "$Description is missing property '$name'."
        }
    }
}

function Get-JsonString {
    param(
        [Parameter(Mandatory = $true)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        Fail-WebsiteMetadata "$Description must be a string."
    }

    $value = $Element.GetString()
    if ([string]::IsNullOrWhiteSpace($value)) {
        Fail-WebsiteMetadata "$Description must not be empty."
    }

    return $value
}

function Test-RepositoryUrl {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [switch]$AllowReadmeFragment
    )

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) {
        return $false
    }

    $allowedFragments = @('')
    if ($AllowReadmeFragment) {
        $allowedFragments += '#readme'
    }

    return (
        $uri.Scheme -ceq 'https' -and
        $uri.Host -ceq 'github.com' -and
        $uri.Port -eq 443 -and
        [string]::IsNullOrEmpty($uri.UserInfo) -and
        [string]::IsNullOrEmpty($uri.Query) -and
        $uri.AbsolutePath -ceq '/KeelMatrix/ResilienceSpec' -and
        $allowedFragments -ccontains $uri.Fragment
    )
}

function Get-MSBuildPackageMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $arguments = @(
        'msbuild',
        $ProjectPath,
        '-nologo',
        '-getProperty:IsPackable',
        '-getProperty:PackageId',
        '-getProperty:PackageTags',
        '-getProperty:PackageProjectUrl',
        '-getProperty:RepositoryUrl',
        '-getProperty:RepositoryType'
    )
    $result = Invoke-ExternalCommand -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $RepositoryRoot -TimeoutSeconds 120
    if (-not $result.Succeeded -or $result.ExitCode -ne 0) {
        Fail-WebsiteMetadata "MSBuild could not evaluate '$ProjectPath': $($result.FailureReason)"
    }

    try {
        $response = $result.Output | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Fail-WebsiteMetadata "MSBuild returned invalid JSON for '$ProjectPath'."
    }

    if ($null -eq $response -or $null -eq $response.Properties) {
        Fail-WebsiteMetadata "MSBuild returned no evaluated properties for '$ProjectPath'."
    }

    return $response.Properties
}

function Get-SourceSentinel {
    param(
        [Parameter(Mandatory = $true)][string[]]$Tags,
        [Parameter(Mandatory = $true)][string[]]$AllowedSentinels,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$PackageId
    )

    $matches = @()
    foreach ($tag in $Tags) {
        foreach ($sentinel in $AllowedSentinels) {
            if ($tag -ieq $sentinel) {
                $matches += $tag
                break
            }
        }
    }

    if ($matches.Count -ne 1) {
        Fail-WebsiteMetadata "Package '$PackageId' must have exactly one $Description sentinel tag."
    }

    $value = [string]$matches[0]
    if ($AllowedSentinels -cnotcontains $value) {
        Fail-WebsiteMetadata "Package '$PackageId' has a $Description sentinel with incorrect casing."
    }

    return $value
}

try {
    if (-not (Test-Path -LiteralPath $RepositoryPath -PathType Container)) {
        Fail-WebsiteMetadata "Repository directory was not found: $RepositoryPath"
    }

    $RepositoryPath = (Resolve-Path -LiteralPath $RepositoryPath).Path
    $manifestPath = Join-Path $RepositoryPath 'keelmatrix.website.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Fail-WebsiteMetadata 'Root keelmatrix.website.json was not found.'
    }

    try {
        $manifestText = [System.IO.File]::ReadAllText($manifestPath)
        $manifestDocument = [System.Text.Json.JsonDocument]::Parse($manifestText)
    }
    catch {
        Fail-WebsiteMetadata 'Root keelmatrix.website.json is malformed JSON.'
    }

    $manifestPackages = [System.Collections.Generic.List[object]]::new()
    $seenManifestPackageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    try {
        $rootProperties = Get-JsonObjectProperties -Element $manifestDocument.RootElement -Description 'Manifest root'
        Assert-JsonPropertySet -Properties $rootProperties -ExpectedNames @('schemaVersion', 'packages') -Description 'Manifest root'

        $schemaVersionElement = $rootProperties['schemaVersion']
        if ($schemaVersionElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) {
            Fail-WebsiteMetadata 'Manifest schemaVersion must be the number 1.'
        }

        try {
            $schemaVersion = $schemaVersionElement.GetInt32()
        }
        catch {
            Fail-WebsiteMetadata 'Manifest schemaVersion must be the number 1.'
        }

        if ($schemaVersion -ne 1) {
            Fail-WebsiteMetadata "Unsupported manifest schemaVersion '$schemaVersion'."
        }

        $packagesElement = $rootProperties['packages']
        $packageProperties = Get-JsonObjectProperties -Element $packagesElement -Description 'Manifest packages'
        foreach ($packageEntry in $packageProperties.GetEnumerator()) {
            $packageId = [string]$packageEntry.Key
            if ([string]::IsNullOrWhiteSpace($packageId)) {
                Fail-WebsiteMetadata 'Manifest package IDs must not be empty.'
            }

            if (-not $seenManifestPackageIds.Add($packageId)) {
                Fail-WebsiteMetadata "Manifest contains duplicate package ID '$packageId'."
            }

            $classification = Get-JsonObjectProperties -Element $packageEntry.Value -Description "Manifest entry for '$packageId'"
            Assert-JsonPropertySet -Properties $classification -ExpectedNames @('visibility', 'role') -Description "Manifest entry for '$packageId'"

            $visibility = Get-JsonString -Element $classification['visibility'] -Description "Manifest visibility for '$packageId'"
            if (@('public-product', 'internal-package') -cnotcontains $visibility) {
                Fail-WebsiteMetadata "Manifest visibility for '$packageId' must be 'public-product' or 'internal-package'."
            }

            $role = Get-JsonString -Element $classification['role'] -Description "Manifest role for '$packageId'"
            if (@('primary', 'component') -cnotcontains $role) {
                Fail-WebsiteMetadata "Manifest role for '$packageId' must be 'primary' or 'component'."
            }

            $manifestPackages.Add([pscustomobject]@{
                PackageId = $packageId
                Visibility = $visibility
                Role = $role
            })
        }
    }
    finally {
        $manifestDocument.Dispose()
    }

    $projectFiles = @(
        Get-ChildItem -LiteralPath $RepositoryPath -Recurse -File -Filter '*.csproj' |
            Where-Object {
                $relativePath = [System.IO.Path]::GetRelativePath($RepositoryPath, $_.FullName)
                $relativePath -notmatch '(?i)(^|[\\/])(?:\.git|bin|obj|artifacts)([\\/]|$)'
            } |
            Sort-Object FullName
    )
    if ($projectFiles.Count -eq 0) {
        Fail-WebsiteMetadata 'No project files were found to evaluate.'
    }

    $sourcePackages = [System.Collections.Generic.List[object]]::new()
    $seenSourcePackageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $visibilitySentinels = @('keelmatrix-public-product', 'keelmatrix-internal-package')
    $roleSentinels = @('keelmatrix-primary', 'keelmatrix-component')

    foreach ($projectFile in $projectFiles) {
        $metadata = Get-MSBuildPackageMetadata -ProjectPath $projectFile.FullName -RepositoryRoot $RepositoryPath
        $isPackable = [string]$metadata.IsPackable
        if ($isPackable -ieq 'false') {
            continue
        }

        if ($isPackable -ine 'true') {
            Fail-WebsiteMetadata "Project '$($projectFile.FullName)' has an invalid IsPackable value."
        }

        $packageId = [string]$metadata.PackageId
        if ([string]::IsNullOrWhiteSpace($packageId)) {
            Fail-WebsiteMetadata "Packable project '$($projectFile.FullName)' has no PackageId."
        }

        if (-not $seenSourcePackageIds.Add($packageId)) {
            Fail-WebsiteMetadata "Packable projects contain duplicate PackageId '$packageId'."
        }

        $packageTags = [string]$metadata.PackageTags
        $tags = @($packageTags -split '[;\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $visibilitySentinel = Get-SourceSentinel -Tags $tags -AllowedSentinels $visibilitySentinels -Description 'visibility' -PackageId $packageId
        $visibility = if ($visibilitySentinel -ceq 'keelmatrix-public-product') { 'public-product' } else { 'internal-package' }
        $roleSentinel = Get-SourceSentinel -Tags $tags -AllowedSentinels $roleSentinels -Description 'role' -PackageId $packageId
        $role = if ($roleSentinel -ceq 'keelmatrix-primary') { 'primary' } else { 'component' }

        $packageProjectUrl = [string]$metadata.PackageProjectUrl
        if (-not (Test-RepositoryUrl -Value $packageProjectUrl -AllowReadmeFragment)) {
            Fail-WebsiteMetadata "Package '$packageId' PackageProjectUrl must point to the canonical KeelMatrix/ResilienceSpec GitHub repository."
        }

        $repositoryUrl = [string]$metadata.RepositoryUrl
        if (-not (Test-RepositoryUrl -Value $repositoryUrl)) {
            Fail-WebsiteMetadata "Package '$packageId' RepositoryUrl must point to the canonical KeelMatrix/ResilienceSpec GitHub repository."
        }

        if ([string]$metadata.RepositoryType -ine 'git') {
            Fail-WebsiteMetadata "Package '$packageId' RepositoryType must be 'git'."
        }

        $sourcePackages.Add([pscustomobject]@{
            PackageId = $packageId
            Visibility = $visibility
            Role = $role
        })
    }

    if ($sourcePackages.Count -eq 0) {
        Fail-WebsiteMetadata 'No packable package projects were found.'
    }

    $sourcePackageIds = @($sourcePackages | ForEach-Object { $_.PackageId })
    $manifestPackageIds = @($manifestPackages | ForEach-Object { $_.PackageId })
    foreach ($sourcePackage in $sourcePackages) {
        if ($manifestPackageIds -cnotcontains $sourcePackage.PackageId) {
            Fail-WebsiteMetadata "Packable package '$($sourcePackage.PackageId)' is missing from the manifest."
        }

        $classification = @($manifestPackages | Where-Object { $_.PackageId -ceq $sourcePackage.PackageId })
        if ($classification.Count -ne 1) {
            Fail-WebsiteMetadata "Manifest must contain exactly one entry for '$($sourcePackage.PackageId)'."
        }

        if ($classification[0].Visibility -cne $sourcePackage.Visibility -or $classification[0].Role -cne $sourcePackage.Role) {
            Fail-WebsiteMetadata "Manifest and source sentinel tags disagree for '$($sourcePackage.PackageId)'."
        }
    }

    foreach ($manifestPackageId in $manifestPackageIds) {
        if ($sourcePackageIds -cnotcontains $manifestPackageId) {
            Fail-WebsiteMetadata "Manifest package '$manifestPackageId' has no packable project in this repository."
        }
    }

    $primaryPackages = @($sourcePackages | Where-Object { $_.Role -ceq 'primary' })
    if ($primaryPackages.Count -gt 1) {
        Fail-WebsiteMetadata 'The repository product family has more than one primary package.'
    }

    $publicPackages = @($sourcePackages | Where-Object { $_.Visibility -ceq 'public-product' })
    $publicPrimaryPackages = @($publicPackages | Where-Object { $_.Role -ceq 'primary' })
    if ($publicPackages.Count -gt 0 -and $publicPrimaryPackages.Count -ne 1) {
        Fail-WebsiteMetadata 'A public product family must have exactly one primary package.'
    }

    Write-Output ("Website package metadata passed: packages={0} public={1} primary={2}" -f $sourcePackages.Count, $publicPackages.Count, $primaryPackages.Count)
}
catch {
    [Console]::Error.WriteLine("Website metadata validation failed: $($_.Exception.Message)")
    exit 1
}
