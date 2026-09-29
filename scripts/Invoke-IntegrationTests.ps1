[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('9.8.0', '10.10.0')]
    [string]$ResilienceVersion,

    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/Invoke-ExternalCommand.ps1')

$repo = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repo 'tests/KeelMatrix.ResilienceSpec.IntegrationTests/KeelMatrix.ResilienceSpec.IntegrationTests.csproj'
$result = Invoke-ExternalCommand -FilePath 'dotnet' -ArgumentList @(
    'test', $testProject, '-c', 'Release', "-p:ResilienceVersion=$ResilienceVersion") `
    -WorkingDirectory $repo -TimeoutSeconds $TimeoutSeconds

if (-not [string]::IsNullOrWhiteSpace($result.Output)) {
    Write-Output $result.Output.TrimEnd()
}
if (-not [string]::IsNullOrWhiteSpace($result.Error)) {
    Write-Output $result.Error.TrimEnd()
}
if (-not $result.Succeeded) {
    throw "Integration endpoint $ResilienceVersion failed closed: $($result.FailureReason)"
}

exit 0
