[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail-WorkflowContract {
    param([Parameter(Mandatory = $true)][string]$Message)

    throw "External-command workflow contract failed: $Message"
}

function Resolve-WorkflowScript {
    param(
        [Parameter(Mandatory = $true)][string]$WorkflowPath,
        [Parameter(Mandatory = $true)][string]$CommandLine
    )

    if ($CommandLine -match '(?i)-File\s+(?<path>(?:\.\/|\.\\)?(?:build|scripts)[/\\]\S+\.(?:ps1|sh))') {
        $relativePath = $Matches.path.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
        $path = Join-Path $RepositoryPath $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Fail-WorkflowContract "workflow '$WorkflowPath' invokes missing script '$relativePath'."
        }

        return $path
    }

    return $null
}

try {
    $workflowPaths = @(
        Join-Path $RepositoryPath '.github/workflows/validate.yml'
        Join-Path $RepositoryPath '.github/workflows/release.yml')
    $directGatePattern = '(?i)^\s*(?:-\s*)?(?:run:\s*)?(?<command>git|dotnet|pwsh|powershell|bash|sh)(?:\s|$)'
    $allowedStandaloneScripts = @(
        'build/Test-NestedPwshLaunch.ps1',
        'build/Test-ExternalCommandWorkflow.ps1',
        'scripts/validate-linux.sh')

    foreach ($workflowPath in $workflowPaths) {
        if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
            Fail-WorkflowContract "workflow is missing: $workflowPath"
        }

        $relativeWorkflow = [IO.Path]::GetRelativePath($RepositoryPath, $workflowPath)
        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $workflowPath) {
            $lineNumber++
            $match = [regex]::Match($line, $directGatePattern)
            if (-not $match.Success) {
                continue
            }

            $scriptPath = Resolve-WorkflowScript -WorkflowPath $relativeWorkflow -CommandLine $line
            if ($null -eq $scriptPath) {
                Fail-WorkflowContract "direct '$($match.Groups['command'].Value)' gate at ${relativeWorkflow}:$lineNumber is not routed through a repository script."
            }

            $relativeScript = [IO.Path]::GetRelativePath($RepositoryPath, $scriptPath).Replace('\', '/')
            if ($allowedStandaloneScripts -contains $relativeScript) {
                continue
            }

            $script = Get-Content -LiteralPath $scriptPath -Raw
            if ($script -notmatch '(?m)Invoke-ExternalCommand') {
                Fail-WorkflowContract "workflow gate '$relativeScript' at ${relativeWorkflow}:$lineNumber does not reference build/Invoke-ExternalCommand.ps1."
            }
        }
    }

    Write-Output 'External-command workflow contract passed: validate.yml and release.yml route gates through repository runners.'
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
