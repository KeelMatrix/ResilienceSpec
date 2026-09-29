[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

foreach ($name in @(
        'GIT_DIR',
        'GIT_WORK_TREE',
        'GIT_COMMON_DIR',
        'GIT_INDEX_FILE',
        'GIT_OBJECT_DIRECTORY',
        'GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/Invoke-ExternalCommand.ps1')

$result = Invoke-ExternalCommand -FilePath 'git' -ArgumentList @('-C', $SourceRoot, "--work-tree=$SourceRoot", 'rev-parse', '--verify', 'HEAD') -WorkingDirectory $SourceRoot -TimeoutSeconds 60
if (-not [string]::IsNullOrWhiteSpace($result.Output)) {
    Write-Output $result.Output.TrimEnd()
}
if (-not [string]::IsNullOrWhiteSpace($result.Error)) {
    Write-Error $result.Error.TrimEnd()
}
if (-not $result.Succeeded) {
    Write-Error "Unable to resolve the repository revision through the bounded runner: $($result.FailureReason)"
    exit 1
}

exit 0
