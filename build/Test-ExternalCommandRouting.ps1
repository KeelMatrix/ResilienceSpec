[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Invoke-ExternalCommand.ps1')

function Invoke-UnauthorizedText {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $text = (& $Action 2>&1 | Out-String).Trim()
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        throw "Routing negative control '$Name' unexpectedly succeeded. Output: $text"
    }
    if ($text -notmatch 'External-command routing violation') {
        throw "Routing negative control '$Name' returned an unrecognized diagnostic: $text"
    }

    Write-Output "negative[$Name] exit=$exitCode"
    Write-Output $text
}

function Invoke-UnauthorizedAlias {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$AliasName,
        [Parameter(Mandatory = $true)][string]$CommandName
    )

    $aliasCommand = @"
Set-Alias -Name '$AliasName' -Value Start-Process -Scope Local -Force
`$stdoutPath = [IO.Path]::GetTempFileName()
`$stderrPath = [IO.Path]::GetTempFileName()
try {
    `$process = $AliasName $CommandName -ArgumentList '--version' -RedirectStandardOutput `$stdoutPath -RedirectStandardError `$stderrPath -Wait -PassThru
    Write-Output (([IO.File]::ReadAllText(`$stdoutPath) + [IO.File]::ReadAllText(`$stderrPath)).Trim())
    exit `$process.ExitCode
}
finally {
    Remove-Item -LiteralPath `$stdoutPath, `$stderrPath -Force -ErrorAction SilentlyContinue
}
"@
    $result = Invoke-ExternalCommand -FilePath 'pwsh' -ArgumentList @(
        '-NoProfile', '-Command', $aliasCommand) -WorkingDirectory $RepositoryPath -TimeoutSeconds 30
    $text = (@($result.Output, $result.Error) -join [Environment]::NewLine).Trim()
    if ($result.Succeeded -or $result.ExitCode -eq 0) {
        throw "Routing negative control '$Name' unexpectedly succeeded. Output: $text"
    }
    if ($text -notmatch 'External-command routing violation') {
        throw "Routing negative control '$Name' returned an unrecognized diagnostic: $text"
    }

    Write-Output "negative[$Name] exit=$($result.ExitCode)"
    Write-Output $text
}

function Invoke-UnauthorizedDirect {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    Invoke-UnauthorizedText -Name "direct-$CommandName" -Action {
        & $CommandName '--version' 2>&1
    }
}

function Invoke-UnauthorizedExpression {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    Invoke-UnauthorizedText -Name "iex-$CommandName" -Action {
        Invoke-Expression "$CommandName --version"
    }
}

function Invoke-UnauthorizedWrapper {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    if ($IsWindows) {
        Invoke-UnauthorizedText -Name "cmd-$CommandName" -Action {
            & (Join-Path $env:SystemRoot 'System32/cmd.exe') /d /s /c "$CommandName --version" 2>&1
        }
    }
    elseif ($null -ne (Get-Command bash -CommandType Application -ErrorAction SilentlyContinue)) {
        Invoke-UnauthorizedText -Name "bash-$CommandName" -Action {
            & bash -c "$CommandName --version" 2>&1
        }
    }
}

foreach ($commandName in @('dotnet', 'git')) {
    Invoke-UnauthorizedDirect -CommandName $commandName
    Invoke-UnauthorizedAlias -Name "saps-$commandName" -AliasName 'saps' -CommandName $commandName
    Invoke-UnauthorizedAlias -Name "start-$commandName" -AliasName 'start' -CommandName $commandName
    Invoke-UnauthorizedExpression -CommandName $commandName
    Invoke-UnauthorizedWrapper -CommandName $commandName
}

$routingPowerShell = [string]::Join('', @('p', 'w', 's', 'h'))
Invoke-UnauthorizedDirect -CommandName $routingPowerShell

$positive = Invoke-ExternalCommand -FilePath 'dotnet' -ArgumentList @('--version') -WorkingDirectory $RepositoryPath -TimeoutSeconds 30
if (-not $positive.Succeeded -or $positive.ExitCode -ne 0) {
    throw "The bounded runner positive control failed: $($positive.FailureReason)"
}

Write-Output "positive[bounded-runner-dotnet] exit=$($positive.ExitCode)"
Write-Output $positive.Output.TrimEnd()
Write-Output 'External-command runtime routing controls passed.'
