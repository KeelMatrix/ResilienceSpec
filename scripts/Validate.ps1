[CmdletBinding()]
param(
    [ValidateSet('Focused', 'Standard', 'Full')]
    [string]$Mode = 'Standard',

    [string]$ResilienceVersion,

    [switch]$SkipPackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/Invoke-ExternalCommand.ps1')
$pwshExecutable = 'pwsh'

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $false)][string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][ValidateRange(1, 3600)][int]$TimeoutSeconds
    )

    Write-Host "== $Name"
    $start = Get-Date
    $result = Invoke-ExternalCommand -FilePath $File -ArgumentList $Arguments -WorkingDirectory $repo -TimeoutSeconds $TimeoutSeconds
    if (-not [string]::IsNullOrWhiteSpace($result.Output)) {
        Write-Host $result.Output.TrimEnd()
    }
    if (-not [string]::IsNullOrWhiteSpace($result.Error)) {
        Write-Host $result.Error.TrimEnd()
    }
    $elapsed = (Get-Date) - $start
    if ($result.TimedOut) {
        $killSuffix = if ($result.KillError) { " Kill attempt reported: $($result.KillError)." } else { '' }
        throw "$Name timed out after $TimeoutSeconds seconds; blocked operation: $File $($Arguments -join ' '). The child process was terminated.$killSuffix"
    }

    if (-not $result.Succeeded) {
        throw "$Name failed closed: $($result.FailureReason)"
    }

    $exitCode = $result.ExitCode
    Write-Host ("   exit {0} in {1:n1}s" -f $exitCode, $elapsed.TotalSeconds)
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }

    $script:stepDuration = $elapsed
}

$repo = Split-Path -Parent $PSScriptRoot
$workflowGuard = Join-Path $repo 'build/Test-ExternalCommandWorkflow.ps1'
Invoke-Step -Name 'External-command workflow routing guard' -File $pwshExecutable -TimeoutSeconds 60 -Arguments @(
    '-NoProfile', '-File', $workflowGuard)
$launchGuard = Join-Path $repo 'build/Test-NestedPwshLaunch.ps1'
Invoke-Step -Name 'Nested PowerShell launch guard self-test' -File $pwshExecutable -TimeoutSeconds 60 -Arguments @(
    '-NoProfile', '-File', $launchGuard, '-SelfTest')
Invoke-Step -Name 'Nested PowerShell launch guard' -File $pwshExecutable -TimeoutSeconds 60 -Arguments @(
    '-NoProfile', '-File', $launchGuard)
$solution = Join-Path $repo 'KeelMatrix.ResilienceSpec.slnx'
$nugetConfig = Join-Path $repo 'NuGet.config'
$smokeScript = Join-Path $PSScriptRoot 'Invoke-PackageSmoke.ps1'
$sampleScript = Join-Path $PSScriptRoot 'Run-Sample.ps1'
$auditScript = Join-Path $PSScriptRoot 'Invoke-DependencyAudit.ps1'
$historyScript = Join-Path $PSScriptRoot 'Validate-History.ps1'
$compatibilityScript = Join-Path $PSScriptRoot 'Validate-CompatibilityContract.ps1'
$documentationHygieneScript = Join-Path $PSScriptRoot 'Validate-DocumentationHygiene.ps1'
$durations = [ordered]@{}

$saved = [Environment]::GetEnvironmentVariable('KEELMATRIX_NO_TELEMETRY', 'Process')
try {
    [Environment]::SetEnvironmentVariable('KEELMATRIX_NO_TELEMETRY', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', 'en', 'Process')

    $compatibilityArguments = @('-NoProfile', '-File', $compatibilityScript, '-RepositoryPath', $repo)
    if (-not [string]::IsNullOrWhiteSpace($ResilienceVersion)) {
        $compatibilityArguments += @('-ResilienceVersion', $ResilienceVersion)
    }
    Invoke-Step -Name 'Validate compatibility contract' -File $pwshExecutable -TimeoutSeconds 60 -Arguments $compatibilityArguments
    $durations['compatibility'] = $script:stepDuration

    $common = @("-p:NuGetAudit=false")
    if (-not [string]::IsNullOrWhiteSpace($ResilienceVersion)) {
        $common += "-p:ResilienceVersion=$ResilienceVersion"
    }

    Invoke-Step -Name 'Validate reachable commit history' -File $pwshExecutable -TimeoutSeconds 60 -Arguments @(
        '-NoProfile', '-File', $historyScript, '-Revision', 'HEAD', '-RepositoryPath', $repo)
    $durations['history'] = $script:stepDuration

    Invoke-Step -Name 'Validate release-facing documentation hygiene' -File $pwshExecutable -TimeoutSeconds 60 -Arguments @(
        '-NoProfile', '-File', $documentationHygieneScript, '-RepositoryPath', $repo)
    $durations['documentation'] = $script:stepDuration

    Invoke-Step -Name 'Restore' -File 'dotnet' -TimeoutSeconds 180 -Arguments (@('restore', $solution, '--configfile', $nugetConfig) + $common)
    $durations['restore'] = $script:stepDuration

    if ($Mode -ne 'Focused') {
        Invoke-Step -Name 'Verify formatting and analyzers' -File 'dotnet' -TimeoutSeconds 300 -Arguments @(
            'format', $solution, '--verify-no-changes', '--no-restore', '--verbosity', 'quiet')
        $durations['format'] = $script:stepDuration
    }

    Invoke-Step -Name 'Release build of the solution' -File 'dotnet' -TimeoutSeconds 300 -Arguments (@('build', $solution, '-c', 'Release', '--no-restore') + $common)
    $durations['build'] = $script:stepDuration

    $testProjects = @(
        (Join-Path $repo 'tests/KeelMatrix.ResilienceSpec.Tests/KeelMatrix.ResilienceSpec.Tests.csproj'),
        (Join-Path $repo 'tests/KeelMatrix.ResilienceSpec.IntegrationTests/KeelMatrix.ResilienceSpec.IntegrationTests.csproj')
    )
    foreach ($testProject in $testProjects) {
        $testName = "Release test run: $(Split-Path -Leaf (Split-Path -Parent $testProject))"
        Invoke-Step -Name $testName -File 'dotnet' -TimeoutSeconds 600 -Arguments (@('test', $testProject, '-c', 'Release', '--no-build') + $common)
        $durations[$testName] = $script:stepDuration
    }

    if (-not $SkipPackage) {
        Invoke-Step -Name 'Reproducible package build, inspection, and clean consumer smoke' -File $pwshExecutable -TimeoutSeconds 600 -Arguments @('-NoProfile', '-File', $smokeScript)
        $durations['smoke'] = $script:stepDuration

        Invoke-Step -Name 'Run sample against the packed package' -File $pwshExecutable -TimeoutSeconds 300 -Arguments @('-NoProfile', '-File', $sampleScript)
        $durations['sample'] = $script:stepDuration
    }

    if ($Mode -eq 'Full') {
        Invoke-Step -Name 'Dependency vulnerability audit' -File $pwshExecutable -TimeoutSeconds 180 -Arguments @(
            '-NoProfile', '-File', $auditScript, '-Mode', 'Required', '-TimeoutSeconds', '120')
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
