[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot),
    [string]$IgnoreFilePath,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path -LiteralPath $RepositoryPath -ErrorAction Stop).Path
$contractPath = Join-Path $PSScriptRoot '../build/IgnoreContract.json'
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw "Ignore contract manifest was not found: $contractPath"
}

try {
    $contract = [IO.File]::ReadAllText($contractPath) | ConvertFrom-Json
}
catch {
    throw "Ignore contract manifest is invalid: $($_.Exception.Message)"
}

if ($null -eq $contract -or
    $null -eq $contract.PSObject.Properties['version'] -or
    $null -eq $contract.PSObject.Properties['requiredPatterns'] -or
    $null -eq $contract.PSObject.Properties['excludedPatterns']) {
    throw 'Ignore contract manifest has an unsupported or incomplete schema.'
}

if ($contract.version -ne 1) {
    throw "Ignore contract manifest version '$($contract.version)' is unsupported."
}

$requirements = @($contract.requiredPatterns)
$exclusions = @($contract.excludedPatterns)
if ($requirements.Count -eq 0) {
    throw 'Ignore contract manifest must list at least one required pattern.'
}
if ($exclusions.Count -eq 0) {
    throw 'Ignore contract manifest must explain at least one inapplicable pattern class.'
}

$seenPatterns = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($requirement in $requirements) {
    if ([string]::IsNullOrWhiteSpace([string]$requirement.pattern) -or
        [string]::IsNullOrWhiteSpace([string]$requirement.reason)) {
        throw 'Every ignore contract requirement must include a pattern and applicability reason.'
    }

    if (-not $seenPatterns.Add([string]$requirement.pattern)) {
        throw "Ignore contract manifest contains duplicate pattern '$($requirement.pattern)'."
    }
}

foreach ($exclusion in $exclusions) {
    if ([string]::IsNullOrWhiteSpace([string]$exclusion.pattern) -or
        [string]::IsNullOrWhiteSpace([string]$exclusion.reason)) {
        throw 'Every excluded ignore pattern must include its applicability reason.'
    }

    if ($seenPatterns.Contains([string]$exclusion.pattern)) {
        throw "Ignore contract manifest marks pattern '$($exclusion.pattern)' as both required and excluded."
    }
}

function Assert-IgnoreContract {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Ignore contract failed: .gitignore was not found at '$Path'."
    }

    $lines = [IO.File]::ReadAllLines($Path)
    foreach ($requirement in $script:requirements) {
        $found = $false
        foreach ($line in $lines) {
            if ([string]::Equals($line, [string]$requirement.pattern, [StringComparison]::Ordinal)) {
                $found = $true
                break
            }
        }

        if (-not $found) {
            throw "Ignore contract failed: required .gitignore entry '$($requirement.pattern)' is missing or changed ($($requirement.reason))."
        }
    }

    Write-Output "Ignore contract passed: $($script:requirements.Count) required patterns in '$Path'."
}

$ignorePath = if ([string]::IsNullOrWhiteSpace($IgnoreFilePath)) {
    Join-Path $repository '.gitignore'
}
else {
    $IgnoreFilePath
}

if (-not $SelfTest) {
    Assert-IgnoreContract -Path $ignorePath
    return
}

if (-not [string]::IsNullOrWhiteSpace($IgnoreFilePath)) {
    throw 'SelfTest cannot be combined with IgnoreFilePath.'
}

$sourceLines = [IO.File]::ReadAllLines($ignorePath)
Assert-IgnoreContract -Path $ignorePath

$scratchRoot = $env:PAPERCLIP_TMPDIR
if ([string]::IsNullOrWhiteSpace($scratchRoot) -or
    -not (Test-Path -LiteralPath $scratchRoot -PathType Container)) {
    $scratchRoot = [IO.Path]::GetTempPath()
}

$fixtureDirectory = (Resolve-Path -LiteralPath $scratchRoot).Path
$fixturePath = Join-Path $fixtureDirectory ("ignore-contract-{0}.gitignore" -f [Guid]::NewGuid().ToString('N'))
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
try {
    [IO.File]::WriteAllLines($fixturePath, $sourceLines, $utf8WithoutBom)
    & $PSCommandPath -RepositoryPath $repository -IgnoreFilePath $fixturePath
    Write-Output 'Positive control passed: the unmodified disposable .gitignore satisfies the contract.'

    foreach ($requirement in $requirements) {
        $mutatedLines = [Collections.Generic.List[string]]::new()
        foreach ($line in $sourceLines) {
            if (-not [string]::Equals($line, [string]$requirement.pattern, [StringComparison]::Ordinal)) {
                [void]$mutatedLines.Add($line)
            }
        }

        if ($mutatedLines.Count -eq $sourceLines.Length) {
            throw "Negative control could not remove required pattern '$($requirement.pattern)' from the disposable fixture."
        }

        [IO.File]::WriteAllLines($fixturePath, $mutatedLines.ToArray(), $utf8WithoutBom)
        $failureMessage = $null
        try {
            & $PSCommandPath -RepositoryPath $repository -IgnoreFilePath $fixturePath
        }
        catch {
            $failureMessage = $_.Exception.Message
        }

        $expectedFailure = "required .gitignore entry '$($requirement.pattern)' is missing or changed"
        if ([string]::IsNullOrWhiteSpace($failureMessage) -or
            -not $failureMessage.Contains($expectedFailure, [StringComparison]::Ordinal)) {
            throw "Negative control failed for '$($requirement.pattern)': expected the validator to identify the removed contract entry; " +
                "received '$failureMessage'."
        }

        Write-Output "Negative control passed: removing '$($requirement.pattern)' is rejected with the expected diagnostic."
    }
}
finally {
    if (Test-Path -LiteralPath $fixturePath -PathType Leaf) {
        Remove-Item -LiteralPath $fixturePath -Force
    }
}
