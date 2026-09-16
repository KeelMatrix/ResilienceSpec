[CmdletBinding()]
param(
    [ValidateSet('Required', 'Tolerant')]
    [string]$Mode = 'Required',

    [string]$Solution = 'KeelMatrix.ResilienceSpec.slnx'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-AuditSummary {
    param([Parameter(Mandatory = $true)][string]$Message)

    Write-Output $Message
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $Message | Out-File -LiteralPath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
}

function Get-UnavailableMatch {
    param([Parameter(Mandatory = $true)][string]$Output)

    $patterns = @(
        '(?im)NU1900',
        '(?im)unable to load the service index',
        '(?im)failed to retrieve',
        '(?im)vulnerability data.*unavailable',
        '(?im)network',
        '(?im)timed out',
        '(?im)connection',
        '(?im)temporary failure',
        '(?im)name or service not known',
        '(?im)429'
    )

    foreach ($pattern in $patterns) {
        if ($Output -match $pattern) {
            return $pattern
        }
    }

    return $null
}

try {
    $repo = Split-Path -Parent $PSScriptRoot
    $auditOutput = (& dotnet list (Join-Path $repo $Solution) package --vulnerable --include-transitive 2>&1 | Out-String).TrimEnd()
    $auditExitCode = $LASTEXITCODE
    Write-Output $auditOutput

    if ($auditOutput -match '(?im)has the following vulnerable package|NU190[1-4]') {
        Write-AuditSummary 'Dependency audit: vulnerable package data was returned.'
        exit 1
    }

    $unavailable = Get-UnavailableMatch -Output $auditOutput
    if ($null -ne $unavailable) {
        if ($Mode -eq 'Required') {
            Write-AuditSummary "Dependency audit: unavailable; required audit failed closed (matching condition: $unavailable)."
            exit 1
        }

        Write-AuditSummary "Dependency audit: unavailable; tolerated transient advisory-service condition (matching condition: $unavailable)."
        exit 0
    }

    $clean = $auditExitCode -eq 0 -and
        $auditOutput -match '(?im)(No vulnerable packages found|no vulnerable packages given the current sources)' -and
        $auditOutput -match '(?im)following sources were used'

    if ($clean) {
        Write-AuditSummary 'Dependency audit: clean (direct and transitive dependency graph checked).'
        exit 0
    }

    Write-AuditSummary "Dependency audit: status not determined (exit code $auditExitCode)."
    exit 1
}
catch {
    Write-AuditSummary "Dependency audit: unavailable; status could not be determined. $($_.Exception.Message)"
    exit 1
}
