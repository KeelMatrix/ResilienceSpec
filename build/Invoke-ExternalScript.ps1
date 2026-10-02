[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ScriptPath,
    [string]$ResilienceVersion,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Invoke-ExternalCommand.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedScript = (Resolve-Path -LiteralPath $ScriptPath -ErrorAction Stop).Path
$scriptArguments = @('-NoProfile', '-File', $resolvedScript)
if (-not [string]::IsNullOrWhiteSpace($ResilienceVersion)) {
    $scriptArguments += @('-ResilienceVersion', $ResilienceVersion)
}
$result = Invoke-ExternalCommand -FilePath 'pwsh' -ArgumentList $scriptArguments `
    -WorkingDirectory $repositoryRoot -TimeoutSeconds $TimeoutSeconds

if (-not [string]::IsNullOrWhiteSpace($result.Output)) {
    Write-Output $result.Output.TrimEnd()
}
if (-not [string]::IsNullOrWhiteSpace($result.Error)) {
    [Console]::Error.WriteLine($result.Error.TrimEnd())
}
if (-not $result.Succeeded) {
    throw "Bounded external script '$resolvedScript' failed closed: $($result.FailureReason)"
}

exit $result.ExitCode
