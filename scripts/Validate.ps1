[CmdletBinding()]
param(
    [ValidateSet('Focused', 'Standard', 'Full')]
    [string]$Mode = 'Standard',

    [string]$ResilienceVersion,

    [switch]$SkipPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $false)][string[]]$Arguments = @()
    )

    Write-Host "== $Name"
    $start = Get-Date
    & $File @Arguments
    $exitCode = $LASTEXITCODE
    $elapsed = (Get-Date) - $start
    Write-Host ("   exit {0} in {1:n1}s" -f $exitCode, $elapsed.TotalSeconds)
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }

    $script:stepDuration = $elapsed
}

$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'KeelMatrix.ResilienceSpec.slnx'
$nugetConfig = Join-Path $repo 'NuGet.config'
$smokeScript = Join-Path $PSScriptRoot 'Invoke-PackageSmoke.ps1'
$auditScript = Join-Path $PSScriptRoot 'Invoke-DependencyAudit.ps1'
$durations = [ordered]@{}

$saved = [Environment]::GetEnvironmentVariable('KEELMATRIX_NO_TELEMETRY', 'Process')
try {
    [Environment]::SetEnvironmentVariable('KEELMATRIX_NO_TELEMETRY', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', 'en', 'Process')

    $common = @("-p:NuGetAudit=false")
    if (-not [string]::IsNullOrWhiteSpace($ResilienceVersion)) {
        $common += "-p:ResilienceVersion=$ResilienceVersion"
    }

    Invoke-Step -Name 'Restore' -File 'dotnet' -Arguments (@('restore', $solution, '--configfile', $nugetConfig) + $common)
    $durations['restore'] = $script:stepDuration

    if ($Mode -ne 'Focused') {
        Invoke-Step -Name 'Verify formatting and analyzers' -File 'dotnet' -Arguments @(
            'format', $solution, '--verify-no-changes', '--no-restore', '--verbosity', 'quiet')
        $durations['format'] = $script:stepDuration
    }

    Invoke-Step -Name 'Release build of the solution' -File 'dotnet' -Arguments (@('build', $solution, '-c', 'Release', '--no-restore') + $common)
    $durations['build'] = $script:stepDuration

    Invoke-Step -Name 'Release test run of the solution' -File 'dotnet' -Arguments (@('test', $solution, '-c', 'Release', '--no-build') + $common)
    $durations['test'] = $script:stepDuration

    if (-not $SkipPackage) {
        Invoke-Step -Name 'Package build, inspection, and clean consumer smoke' -File 'pwsh' -Arguments @('-NoProfile', '-File', $smokeScript)
        $durations['smoke'] = $script:stepDuration
    }

    if ($Mode -eq 'Full') {
        Invoke-Step -Name 'Dependency vulnerability audit' -File 'pwsh' -Arguments @('-NoProfile', '-File', $auditScript, '-Mode', 'Required')
        $durations['audit'] = $script:stepDuration
    }

    Write-Host ''
    Write-Host "Validation summary ($Mode)"
    foreach ($entry in $durations.GetEnumerator()) {
        Write-Host ("  {0,-12} {1:n1}s" -f $entry.Key, $entry.Value.TotalSeconds)
    }

    Write-Host 'All requested gates passed.'
    exit 0
}
catch {
    [Console]::Error.WriteLine("Validation failed: $($_.Exception.Message)")
    exit 1
}
finally {
    [Environment]::SetEnvironmentVariable('KEELMATRIX_NO_TELEMETRY', $saved, 'Process')
}
