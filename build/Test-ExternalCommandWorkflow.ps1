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

function Get-WorkflowRunScript {
    param([Parameter(Mandatory = $true)][string]$WorkflowPath)

    $lines = @(Get-Content -LiteralPath $WorkflowPath)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = [string]$lines[$index]
        $match = [regex]::Match($line, '^(?<indent>\s*)(?:-\s*)?run\s*:\s*(?<value>.*)$')
        if (-not $match.Success) {
            continue
        }

        $indentLength = $match.Groups['indent'].Value.Length
        $value = $match.Groups['value'].Value
        $sourceLine = $index + 1
        if ($value -match '^[|>][+-]?\s*$') {
            $blockLines = [System.Collections.Generic.List[string]]::new()
            $blockIndex = $index + 1
            while ($blockIndex -lt $lines.Count) {
                $blockLine = [string]$lines[$blockIndex]
                if ([string]::IsNullOrWhiteSpace($blockLine)) {
                    [void]$blockLines.Add('')
                    $blockIndex++
                    continue
                }

                $blockIndent = ([regex]::Match($blockLine, '^\s*')).Value.Length
                if ($blockIndent -le $indentLength) {
                    break
                }

                [void]$blockLines.Add($blockLine)
                $blockIndex++
            }

            $commonIndent = $null
            foreach ($blockLine in $blockLines) {
                if ([string]::IsNullOrWhiteSpace($blockLine)) {
                    continue
                }

                $blockIndent = ([regex]::Match($blockLine, '^\s*')).Value.Length
                if ($null -eq $commonIndent -or $blockIndent -lt $commonIndent) {
                    $commonIndent = $blockIndent
                }
            }

            if ($null -ne $commonIndent) {
                for ($blockLineIndex = 0; $blockLineIndex -lt $blockLines.Count; $blockLineIndex++) {
                    $blockLine = $blockLines[$blockLineIndex]
                    if ($blockLine.Length -ge $commonIndent) {
                        [void]($blockLines[$blockLineIndex] = $blockLine.Substring($commonIndent))
                    }
                }
            }

            $script = if ($value.StartsWith('|', [StringComparison]::Ordinal)) {
                $blockLines -join "`n"
            }
            else {
                ($blockLines -join ' ').Trim()
            }

            $index = $blockIndex - 1
            [pscustomobject]@{
                Script = $script
                Line = $sourceLine
            }
            continue
        }

        [pscustomobject]@{
            Script = $value.Trim()
            Line = $sourceLine
        }
    }
}

function Add-ShellToken {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Tokens,
        [Parameter(Mandatory = $true)][System.Text.StringBuilder]$Buffer,
        [Parameter(Mandatory = $true)][bool]$Quoted
    )

    if ($Buffer.Length -gt 0) {
        [void]$Tokens.Add([pscustomobject]@{
            Text = $Buffer.ToString()
            Quoted = $Quoted
            Kind = 'word'
        })
        [void]$Buffer.Clear()
    }
}

function Get-ShellTokens {
    param([Parameter(Mandatory = $true)][string]$Script)

    $tokens = [System.Collections.Generic.List[object]]::new()
    $buffer = [System.Text.StringBuilder]::new()
    $quote = [char]0
    $quoted = $false

    for ($index = 0; $index -lt $Script.Length; $index++) {
        $character = $Script[$index]
        if ($quote -ne [char]0) {
            if ($quote -eq [char]39 -and $character -eq [char]39 -and $index + 1 -lt $Script.Length -and $Script[$index + 1] -eq [char]39) {
                [void]$buffer.Append([char]39)
                $index++
                continue
            }

            if ($character -eq $quote) {
                $quote = [char]0
                continue
            }

            [void]$buffer.Append($character)
            continue
        }

        if ($character -eq [char]39 -or $character -eq [char]34) {
            $quote = $character
            $quoted = $true
            continue
        }

        if ($character -eq [char]96) {
            if ($index + 1 -lt $Script.Length -and ($Script[$index + 1] -eq [char]10 -or $Script[$index + 1] -eq [char]13)) {
                $index++
                if ($index + 1 -lt $Script.Length -and $Script[$index] -eq [char]13 -and $Script[$index + 1] -eq [char]10) {
                    $index++
                }
                continue
            }

            if ($index + 1 -lt $Script.Length) {
                $index++
                [void]$buffer.Append($Script[$index])
                continue
            }
        }

        if ($character -eq [char]35 -and $buffer.Length -eq 0) {
            while ($index -lt $Script.Length -and $Script[$index] -ne [char]10 -and $Script[$index] -ne [char]13) {
                $index++
            }
            $index--
            continue
        }

        if ([char]::IsWhiteSpace($character)) {
            Add-ShellToken -Tokens $tokens -Buffer $buffer -Quoted $quoted
            $quoted = $false
            if ($character -eq [char]10) {
                [void]$tokens.Add([pscustomobject]@{ Text = "`n"; Quoted = $false; Kind = 'separator' })
            }
            continue
        }

        if ($character -eq [char]';' -or $character -eq [char]'(' -or $character -eq [char]')' -or $character -eq [char]'{' -or $character -eq [char]'}') {
            Add-ShellToken -Tokens $tokens -Buffer $buffer -Quoted $quoted
            $quoted = $false
            [void]$tokens.Add([pscustomobject]@{ Text = [string]$character; Quoted = $false; Kind = 'separator' })
            continue
        }

        if ($character -eq [char]'|' -or $character -eq [char]'&') {
            Add-ShellToken -Tokens $tokens -Buffer $buffer -Quoted $quoted
            $quoted = $false
            $separator = [string]$character
            if ($index + 1 -lt $Script.Length -and $Script[$index + 1] -eq $character) {
                $separator += [string]$character
                $index++
            }
            [void]$tokens.Add([pscustomobject]@{ Text = $separator; Quoted = $false; Kind = 'separator' })
            continue
        }

        [void]$buffer.Append($character)
    }

    Add-ShellToken -Tokens $tokens -Buffer $buffer -Quoted $quoted
    return $tokens
}

function Get-RelativeWorkflowPath {
    param([Parameter(Mandatory = $true)][string]$Candidate)

    $normalized = $Candidate.Replace('\', '/').Trim()
    while ($normalized.StartsWith('./', [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }

    if ($normalized.Contains('..', [StringComparison]::Ordinal) -or $normalized.StartsWith('/', [StringComparison]::Ordinal)) {
        return $null
    }

    if ($normalized -notmatch '^(?:build|scripts)/[^/]+\.(?:ps1|sh)$') {
        return $null
    }

    return $normalized
}

function Test-ApprovedWorkflowScript {
    param([Parameter(Mandatory = $true)][string]$Candidate)

    $relativePath = Get-RelativeWorkflowPath -Candidate $Candidate
    if ($null -eq $relativePath) {
        return $null
    }

    $allowedStandaloneScripts = @(
        'build/Test-NestedPwshLaunch.ps1',
        'build/Test-ExternalCommandWorkflow.ps1',
        'scripts/validate-linux.sh')
    if ($allowedStandaloneScripts -contains $relativePath) {
        $path = Join-Path $RepositoryPath ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Fail-WorkflowContract "workflow invokes missing allowlisted script '$relativePath'."
        }
        return $relativePath
    }

    $path = Join-Path $RepositoryPath ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }

    $script = Get-Content -LiteralPath $path -Raw
    if ($script -match '(?m)Invoke-ExternalCommand') {
        return $relativePath
    }

    return $null
}

function Find-TokenIndex {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Tokens,
        [Parameter(Mandatory = $true)][int]$Start,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    for ($index = $Start; $index -lt $Tokens.Count; $index++) {
        if ($Tokens[$index].Kind -eq 'separator') {
            break
        }

        if ($Names -contains ([string]$Tokens[$index].Text).ToLowerInvariant()) {
            return $index
        }
    }

    return -1
}

function Test-NestedPowerShellCommand {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Tokens,
        [Parameter(Mandatory = $true)][int]$CommandIndex
    )

    $mode = $null
    for ($index = $CommandIndex + 1; $index -lt $Tokens.Count; $index++) {
        $token = $Tokens[$index]
        if ($token.Kind -eq 'separator') {
            break
        }

        $text = ([string]$token.Text).ToLowerInvariant()
        if ($text -in @('-command', '-c')) {
            if ($index + 1 -ge $Tokens.Count -or $Tokens[$index + 1].Kind -eq 'separator') {
                return 'nested PowerShell -Command had no inline script.'
            }

            $nestedViolation = Test-ShellScript -Script ([string]$Tokens[$index + 1].Text)
            if ($null -ne $nestedViolation) {
                return "nested PowerShell -Command: $nestedViolation"
            }
            $mode = 'command'
            $index++
            continue
        }

        if ($text -in @('-file', '-f')) {
            if ($index + 1 -ge $Tokens.Count -or $Tokens[$index + 1].Kind -eq 'separator') {
                return 'nested PowerShell -File had no script path.'
            }

            $approved = Test-ApprovedWorkflowScript -Candidate ([string]$Tokens[$index + 1].Text)
            if ($null -eq $approved) {
                return "nested PowerShell -File '$($Tokens[$index + 1].Text)' is not an approved repository entry script."
            }
            $mode = 'file'
            $index++
            continue
        }
    }

    if ($null -eq $mode) {
        return 'PowerShell invocation must use an approved -File repository entry script or a statically checked -Command script.'
    }

    return $null
}

function Test-StartProcessCommand {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Tokens,
        [Parameter(Mandatory = $true)][int]$CommandIndex
    )

    $fileIndex = Find-TokenIndex -Tokens $Tokens -Start ($CommandIndex + 1) -Names @('-filepath', '-file')
    if ($fileIndex -lt 0 -or $fileIndex + 1 -ge $Tokens.Count) {
        return 'Start-Process could not be structurally inspected.'
    }

    $target = [string]$Tokens[$fileIndex + 1].Text
    $targetName = [IO.Path]::GetFileName($target).ToLowerInvariant()
    if ($targetName -in @('pwsh', 'pwsh.exe', 'powershell', 'powershell.exe')) {
        return Test-NestedPowerShellCommand -Tokens $Tokens -CommandIndex $fileIndex
    }

    if ($null -ne (Test-ApprovedWorkflowScript -Candidate $target)) {
        return $null
    }

    return "Start-Process target '$target' is not an approved repository entry script."
}

function Test-ShellScript {
    param([Parameter(Mandatory = $true)][string]$Script)

    $tokens = @(Get-ShellTokens -Script $Script)
    $commandPosition = $true
    $gateNames = @('git', 'git.exe', 'dotnet', 'dotnet.exe', 'nuget', 'nuget.exe', 'msbuild', 'msbuild.exe', 'vstest', 'vstest.console.exe', 'npm', 'npm.cmd', 'node', 'node.exe', 'python', 'python.exe', 'bash', 'bash.exe', 'sh', 'sh.exe')
    $powerShellNames = @('pwsh', 'pwsh.exe', 'powershell', 'powershell.exe')

    for ($index = 0; $index -lt $tokens.Count; $index++) {
        $token = $tokens[$index]
        if ($token.Kind -eq 'separator') {
            if ($token.Text -eq ')') {
                $commandPosition = $false
            }
            else {
                $commandPosition = $true
            }
            continue
        }

        $text = [string]$token.Text
        $lower = $text.ToLowerInvariant()
        if ($lower -in @('if', 'elseif', 'else', 'while', 'for', 'foreach', 'do', 'switch', 'try', 'catch', 'finally', 'function', 'begin', 'process', 'end', 'in', 'then', 'fi', 'done')) {
            continue
        }

        if ($lower -eq 'start-process') {
            $startProcessViolation = Test-StartProcessCommand -Tokens $tokens -CommandIndex $index
            if ($null -ne $startProcessViolation) {
                return $startProcessViolation
            }
            $commandPosition = $false
            continue
        }

        if (-not $commandPosition) {
            continue
        }

        if ($lower -eq '&' -or $lower -eq '.') {
            $commandPosition = $true
            continue
        }

        if ($gateNames -contains $lower) {
            return "direct executable gate '$text' is not routed through an approved repository runner."
        }

        if ($powerShellNames -contains $lower) {
            $nestedViolation = Test-NestedPowerShellCommand -Tokens $tokens -CommandIndex $index
            if ($null -ne $nestedViolation) {
                return $nestedViolation
            }
            $commandPosition = $false
            continue
        }

        $approved = Test-ApprovedWorkflowScript -Candidate $text
        if ($null -ne $approved) {
            $commandPosition = $false
            continue
        }

        if ($text -match '(?i)(?:^|[/\\])[^/\\]+\.(?:exe|cmd|bat|sh|ps1)$') {
            return "executable gate '$text' is not an approved repository runner."
        }

        $commandPosition = $false
    }

    return $null
}

try {
    $workflowPaths = @(
        Join-Path $RepositoryPath '.github/workflows/validate.yml'
        Join-Path $RepositoryPath '.github/workflows/release.yml')

    foreach ($workflowPath in $workflowPaths) {
        if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
            Fail-WorkflowContract "workflow is missing: $workflowPath"
        }

        $relativeWorkflow = [IO.Path]::GetRelativePath($RepositoryPath, $workflowPath).Replace('\', '/')
        foreach ($runScript in @(Get-WorkflowRunScript -WorkflowPath $workflowPath)) {
            $violation = Test-ShellScript -Script $runScript.Script
            if ($null -ne $violation) {
                Fail-WorkflowContract "direct gate at ${relativeWorkflow}:$($runScript.Line) is not routed through a repository runner: $violation"
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
