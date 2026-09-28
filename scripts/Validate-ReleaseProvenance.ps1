[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TagRevision,

    [Parameter(Mandatory = $true)]
    [string]$RemoteMainRevision
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Normalize-Sha {
    param([Parameter(Mandatory = $true)][string]$Value)

    $normalized = $Value.Trim().ToLowerInvariant()
    if ($normalized -notmatch '^[0-9a-f]{40}$') {
        throw "Release provenance failed: revision '$Value' is not a full Git SHA."
    }

    return $normalized
}

try {
    $tag = Normalize-Sha $TagRevision
    $main = Normalize-Sha $RemoteMainRevision
    if ($tag -cne $main) {
        throw "Release provenance failed: tag commit '$tag' is not the exact remote-main candidate '$main'."
    }

    Write-Output "Release provenance passed: tag=$tag remote-main=$main"
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
