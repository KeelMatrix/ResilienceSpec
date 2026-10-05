[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot),

    [string]$ResilienceVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-Text {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Join-Path $RepositoryPath $RelativePath
    Assert-Contract (Test-Path -LiteralPath $path -PathType Leaf) "Compatibility contract file is missing: $RelativePath."
    return Get-Content -LiteralPath $path -Raw
}

function Convert-CodePoints {
    param([Parameter(Mandatory = $true)][int[]]$Codes)

    return -join ($Codes | ForEach-Object { [char]$_ })
}

$compatibilityPath = Join-Path $RepositoryPath 'build/ResilienceCompatibility.props'
Assert-Contract (Test-Path -LiteralPath $compatibilityPath -PathType Leaf) 'The canonical compatibility source is missing.'
[xml]$compatibility = Get-Content -LiteralPath $compatibilityPath -Raw
$versionNodes = @($compatibility.SelectNodes('//ResilienceVerifiedVersion'))
$versions = @($versionNodes | ForEach-Object { [string]$_.Include })
$expectedVersions = @('9.8.0', '10.10.0')

Assert-Contract ($versions.Count -eq $expectedVersions.Count -and
    (($versions -join '|') -ceq ($expectedVersions -join '|'))) `
    "The canonical compatibility source must list exactly $($expectedVersions -join ', '), in that order."

$defaultNode = $compatibility.SelectSingleNode('//ResilienceDefaultVersion')
$defaultVersion = [string]$defaultNode.InnerText
Assert-Contract ($defaultVersion -ceq $expectedVersions[-1]) `
    "The canonical default resilience version must be '$($expectedVersions[-1])'."

$repositoryGuide = (Convert-CodePoints @(65, 71, 69, 78, 84, 83)) + '.md'

if (-not [string]::IsNullOrWhiteSpace($ResilienceVersion)) {
    Assert-Contract ($expectedVersions -contains $ResilienceVersion) `
        "ResilienceVersion '$ResilienceVersion' is not verified. Select one of: $($expectedVersions -join ', ')."
}

$versionSentence = "The verified Microsoft.Extensions.Http.Resilience versions are **$($versions[0])** and **$($versions[1])**; other versions are unverified."
$requiredDocuments = @(
    'README.md',
    'src/KeelMatrix.ResilienceSpec/README.md',
    'docs/DEV.md',
    'docs/Compatibility.md',
    $repositoryGuide,
    'CHANGELOG.md'
)
foreach ($document in $requiredDocuments) {
    $text = Read-Text $document
    Assert-Contract ($text.Contains($versionSentence, [StringComparison]::Ordinal)) `
        "$document must repeat the canonical verified-version statement."
    Assert-Contract (-not $text.Contains('9.8.0 and newer', [StringComparison]::OrdinalIgnoreCase)) `
        "$document contains the obsolete open-ended resilience range claim."
    Assert-Contract (-not $text.Contains('up to, but not including, 11.0.0', [StringComparison]::OrdinalIgnoreCase)) `
        "$document contains the obsolete 11.0.0 upper-bound claim."
}

$workflow = Read-Text '.github/workflows/validate.yml'
$linuxGate = Read-Text 'scripts/validate-linux.sh'
foreach ($version in $versions) {
    $matrixToken = "-ResilienceVersion $version"
    Assert-Contract ($workflow.Contains($matrixToken, [StringComparison]::Ordinal)) `
        ".github/workflows/validate.yml must execute the $version integration endpoint."
    Assert-Contract ($linuxGate.Contains($matrixToken, [StringComparison]::Ordinal)) `
        "scripts/validate-linux.sh must execute the $version integration endpoint."
}

$shippingProject = Read-Text 'src/KeelMatrix.ResilienceSpec/KeelMatrix.ResilienceSpec.csproj'
Assert-Contract ($shippingProject.Contains('<PackageReference Include="Microsoft.Extensions.Http" />', [StringComparison]::Ordinal)) `
    'The shipping project must retain the Microsoft.Extensions.Http DI adapter dependency.'

$publicApi = Read-Text 'src/KeelMatrix.ResilienceSpec/PublicAPI.Shipped.txt'
Assert-Contract ($publicApi.Contains('UseResilienceSpecDownstream', [StringComparison]::Ordinal)) `
    'The shipped public API must retain the UseResilienceSpecDownstream DI adapter.'

$httpFault = Read-Text 'src/KeelMatrix.ResilienceSpec/HttpFault.cs'
Assert-Contract ($httpFault.Contains('HTTP-date form is deliberately not supported', [StringComparison]::Ordinal)) `
    'The HTTP-date exclusion must remain explicit in the public Retry-After contract.'

$telemetry = Read-Text 'src/KeelMatrix.ResilienceSpec/ScenarioTelemetry.cs'
Assert-Contract ($telemetry.Contains('Instance.TrackActivation();', [StringComparison]::Ordinal)) `
    'Eligible scenarios must request the shared activation signal.'
Assert-Contract ($telemetry.Contains('Instance.TrackHeartbeat();', [StringComparison]::Ordinal)) `
    'Eligible scenarios must request the shared heartbeat signal.'
Assert-Contract (-not $telemetry.Contains('ScenarioTelemetrySignal', [StringComparison]::Ordinal)) `
    'The product integration must not define or pass a local telemetry payload.'

$privacy = Read-Text 'PRIVACY.md'
Assert-Contract ($privacy.Contains('Telemetry/blob/main/app/PRIVACY.md', [StringComparison]::Ordinal)) `
    'PRIVACY.md must point to the maintained shared telemetry policy.'

Write-Output "Compatibility contract passed: $($versions -join ', '); default $defaultVersion."
