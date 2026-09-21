[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$PackageProject = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/KeelMatrix.ResilienceSpec/KeelMatrix.ResilienceSpec.csproj'),

    [string]$ChangelogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail-Contract {
    param([Parameter(Mandatory = $true)][string]$Message)

    throw "Release contract failed: $Message"
}

function Get-MSBuildProperties {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $output = (& dotnet msbuild $ProjectPath -nologo -getProperty:PackageId -getProperty:Version -getProperty:IsPackable 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        Fail-Contract "Unable to read package metadata from '$ProjectPath'."
    }

    try {
        return $output | ConvertFrom-Json
    }
    catch {
        Fail-Contract "MSBuild returned invalid package metadata for '$ProjectPath'."
    }
}

try {
    if (-not (Test-Path -LiteralPath $PackageProject -PathType Leaf)) {
        Fail-Contract "Package project was not found: $PackageProject"
    }

    if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
        Fail-Contract "Changelog was not found: $ChangelogPath"
    }

    $tagMatch = [regex]::Match($Tag, '^v(?<version>\d+\.\d+\.\d+)$')
    if (-not $tagMatch.Success) {
        Fail-Contract "Tag '$Tag' is not a stable vX.Y.Z release tag."
    }

    $tagVersion = $tagMatch.Groups['version'].Value
    $metadata = Get-MSBuildProperties -ProjectPath $PackageProject
    $packageId = [string]$metadata.Properties.PackageId
    $packageVersion = [string]$metadata.Properties.Version
    $isPackable = [string]$metadata.Properties.IsPackable

    if ($isPackable -ne 'true') {
        Fail-Contract "Package project '$PackageProject' is not packable."
    }

    if ($packageVersion -ne $tagVersion) {
        Fail-Contract "Tag version '$tagVersion' does not match package version '$packageVersion'."
    }

    $lines = @(Get-Content -LiteralPath $ChangelogPath)
    $versionHeadingPattern = '^## \[(?<version>[^\]]+)\](?:\s+-\s+(?<date>[^\r\n]+))?\s*$'
    $headings = @(
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $match = [regex]::Match($lines[$index], $versionHeadingPattern)
            if ($match.Success) {
                [pscustomobject]@{
                    Index = $index
                    Version = $match.Groups['version'].Value
                    Date = $match.Groups['date'].Value
                    Text = $lines[$index]
                }
            }
        }
    )

    $unreleased = @($headings | Where-Object { $_.Version -ieq 'Unreleased' })
    if ($unreleased.Count -ne 1) {
        Fail-Contract 'CHANGELOG.md must contain exactly one ## [Unreleased] section.'
    }

    $targetHeadings = @($headings | Where-Object { $_.Version -ceq $tagVersion })
    if ($targetHeadings.Count -ne 1) {
        Fail-Contract "CHANGELOG.md must contain exactly one ## [$tagVersion] section."
    }

    $target = $targetHeadings[0]
    if ($target.Index -le $unreleased[0].Index) {
        Fail-Contract "Version $tagVersion remains under [Unreleased]."
    }

    $nextHeading = @($headings | Where-Object { $_.Index -gt $target.Index } | Sort-Object Index | Select-Object -First 1)
    $endIndex = if ($nextHeading.Count -eq 0) { $lines.Count } else { $nextHeading[0].Index }
    $entryLines = if ($endIndex -gt ($target.Index + 1)) { $lines[($target.Index + 1)..($endIndex - 1)] } else { @() }
    $entry = ($entryLines -join "`n").Trim()

    if ([string]::IsNullOrWhiteSpace($entry)) {
        Fail-Contract "Version $tagVersion has no release entry."
    }

    $preReleaseHeadingPattern = '(?im)\b(?:planned|tbd|unreleased|not\s+yet\s+published|not\s+published|pending)\b'
    $preReleaseEntryPattern = '(?im)\b(?:planned|tbd|unreleased|not\s+yet\s+published|not\s+published)\b'
    if ($target.Text -match $preReleaseHeadingPattern -or $entry -match $preReleaseEntryPattern) {
        Fail-Contract "Version $tagVersion is still marked as planned, unreleased, TBD, pending, or equivalent."
    }

    if ($target.Date -notmatch '^\d{4}-\d{2}-\d{2}$') {
        Fail-Contract "Version $tagVersion must have a finalized YYYY-MM-DD release date."
    }

    $categories = @([regex]::Matches($entry, '(?im)^###\s+(?<name>.+?)\s*$') | ForEach-Object { $_.Groups['name'].Value.Trim() })
    if ($categories.Count -ne 1 -or $categories[0] -cne 'Added') {
        Fail-Contract "The first release entry for $tagVersion must contain exactly one Added section."
    }

    if ($entry -notmatch '(?m)^\s*-\s+\S+') {
        Fail-Contract "The first release entry for $tagVersion must contain at least one Added item."
    }

    $markers = @(
        'now', 'no longer', 'previously', 'formerly', 'used to', 'fixed', 'fixes', 'corrected',
        'resolved', 'addressed', 'this removes', 'this fixes', 'changed from'
    )
    foreach ($marker in $markers) {
        $markerPattern = '(?i)(?<![\p{L}\p{N}])' + [regex]::Escape($marker).Replace('\ ', '\s+') + '(?![\p{L}\p{N}])'
        if ($entry -match $markerPattern) {
            Fail-Contract "The first release entry contains the pre-release remediation marker '$marker'."
        }
    }

    Write-Output "Release contract passed: tag=$Tag version=$tagVersion package=$packageId changelog=finalized first-release=Added-only"
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
