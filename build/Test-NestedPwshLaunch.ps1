[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $PSScriptRoot 'Invoke-NestedPwsh.ps1'
$routingHelperPath = Join-Path $PSScriptRoot 'Invoke-ExternalScript.ps1'
$guardPath = $PSCommandPath

function Get-ParsedCommandRecords(
    [string]$Text,
    [string]$Path,
    [System.Management.Automation.Language.Ast]$InitialAst
) {
    $pending = [System.Collections.Generic.Queue[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending.Enqueue([pscustomobject]@{
            Text = $Text
            Ast = $InitialAst
            BaseLine = 0
            Embedded = $false
        })

    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        if ([string]::IsNullOrWhiteSpace($item.Text) -or -not $seen.Add($item.Text)) {
            continue
        }

        $ast = $item.Ast
        if ($null -eq $ast) {
            $tokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput(
                $item.Text,
                [ref]$tokens,
                [ref]$parseErrors)
            if ($parseErrors.Count -gt 0) {
                continue
            }
        }

        foreach ($command in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))) {
            [pscustomobject]@{
                Path = $Path
                BaseLine = $item.BaseLine
                Command = $command
            }
        }

        foreach ($stringAst in @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                    $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst]
                }, $true))) {
            $value = [string]$stringAst.Value
            $isHereString = $stringAst -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                ([string]$stringAst.StringConstantType -match 'HereString')
            $isScriptLike = $value -match '(?i)(?:\r?\n|(?:^|\s)(?:Start-Process|pwsh(?:\.exe)?|powershell(?:\.exe)?|Invoke-Expression)\b\s+\S)'
            if (-not ($isHereString -or ($item.Embedded -and $isScriptLike))) {
                continue
            }

            $pending.Enqueue([pscustomobject]@{
                    Text = $value
                    Ast = $null
                    BaseLine = $item.BaseLine + $stringAst.Extent.StartLineNumber - 1
                    Embedded = $true
                })
        }
    }
}

function Remove-CSharpNonCode([string]$Text) {
    $pattern = '(?s)//.*?(?=\r?\n|$)|/\*.*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*'''
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        [regex]::Replace($match.Value, '[^\r\n]', ' ')
    }
    return [regex]::Replace($Text, $pattern, $evaluator)
}

function Get-SourceLineNumber([string]$Text, [int]$Index) {
    return 1 + ([regex]::Matches($Text.Substring(0, $Index), '\r\n|\r|\n')).Count
}

function Find-MatchingBrace([string]$Text, [int]$OpeningIndex) {
    $depth = 0
    for ($index = $OpeningIndex; $index -lt $Text.Length; $index++) {
        switch ($Text[$index]) {
            '{' { $depth++ }
            '}' {
                $depth--
                if ($depth -eq 0) {
                    return $index
                }
            }
        }
    }
    return -1
}

function Get-CSharpLaunchReport([string]$Path) {
    $source = [IO.File]::ReadAllText($Path)
    $code = Remove-CSharpNonCode $source
    $violations = [System.Collections.Generic.List[string]]::new()
    $safeVariables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unsafeVariables = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $initializerStartInfoIndexes = [System.Collections.Generic.HashSet[int]]::new()

    $initializerMatches = @([regex]::Matches(
            $code,
            '(?m)(?<name>[A-Za-z_]\w*)\s*=\s*new\s+(?:(?:System\.)?Diagnostics\.)?ProcessStartInfo\s*\{'))
    $allStartInfoMatches = @([regex]::Matches(
            $code,
            '\bnew\s+(?:(?:System\.)?Diagnostics\.)?ProcessStartInfo\b'))

    foreach ($match in $initializerMatches) {
        [void]$initializerStartInfoIndexes.Add($match.Index + $match.Value.IndexOf('new ', [StringComparison]::Ordinal))
        $openingIndex = $code.IndexOf('{', $match.Index + $match.Length - 1)
        $closingIndex = Find-MatchingBrace $code $openingIndex
        $variableName = $match.Groups['name'].Value
        if ($closingIndex -lt 0) {
            [void]$violations.Add("${Path}:$(Get-SourceLineNumber $source $match.Index): ProcessStartInfo initializer is not structurally closed")
            [void]$unsafeVariables.Add($variableName)
            continue
        }

        $initializer = $code.Substring($openingIndex + 1, $closingIndex - $openingIndex - 1)
        $requiredProperties = @(
            '(?m)\bUseShellExecute\s*=\s*false\b',
            '(?m)\bRedirectStandardOutput\s*=\s*true\b',
            '(?m)\bRedirectStandardError\s*=\s*true\b',
            '(?m)\bCreateNoWindow\s*=\s*true\b')
        $missingProperties = @($requiredProperties | Where-Object { $initializer -notmatch $_ })
        if ($missingProperties.Count -gt 0) {
            [void]$violations.Add("${Path}:$(Get-SourceLineNumber $source $match.Index): ProcessStartInfo '$variableName' lacks required child-process containment settings")
            [void]$unsafeVariables.Add($variableName)
        }
        else {
            [void]$safeVariables.Add($variableName)
        }
    }

    foreach ($match in $allStartInfoMatches) {
        if (-not $initializerStartInfoIndexes.Contains($match.Index)) {
            [void]$violations.Add("${Path}:$(Get-SourceLineNumber $source $match.Index): ProcessStartInfo must use an inspectable object initializer")
        }
    }

    $processStartMatches = @([regex]::Matches(
            $code,
            '(?<![A-Za-z0-9_\.])(?:Process|System\.Diagnostics\.Process)\s*\.\s*Start\s*\('))
    foreach ($match in $processStartMatches) {
        $argumentStart = $match.Index + $match.Length
        $argument = [regex]::Match($code.Substring($argumentStart), '^\s*(?<name>[A-Za-z_]\w*)')
        if (-not $argument.Success) {
            [void]$violations.Add("${Path}:$(Get-SourceLineNumber $source $match.Index): Process.Start must receive a contained ProcessStartInfo")
            continue
        }

        $variableName = $argument.Groups['name'].Value
        if ($unsafeVariables.Contains($variableName) -or -not $safeVariables.Contains($variableName)) {
            [void]$violations.Add("${Path}:$(Get-SourceLineNumber $source $match.Index): Process.Start uses an unverified ProcessStartInfo '$variableName'")
        }
    }

    [pscustomobject]@{
        Path = $Path
        ProcessStartInfoCount = $allStartInfoMatches.Count
        ProcessStartCount = $processStartMatches.Count
        Violations = @($violations.ToArray())
    }
}

function Get-LaunchViolations([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        return @("${Path} contains PowerShell parse errors.")
    }

    $violations = [System.Collections.Generic.List[string]]::new()
    $source = [IO.File]::ReadAllText($Path)
    $commands = @(Get-ParsedCommandRecords -Text $source -Path $Path -InitialAst $ast)
    foreach ($record in $commands) {
        $command = $record.Command
        $lineNumber = $record.BaseLine + $command.Extent.StartLineNumber
        $nameAst = $command.CommandElements[0]
        $commandName = if ($nameAst -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
            $nameAst.Value
        }
        elseif ($nameAst -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
            $nameAst.Value
        }
        else {
            $null
        }

        if ($commandName -eq 'Invoke-ExternalCommand') {
            continue
        }

        if ($commandName -match '^(?i:pwsh|powershell)(?:\.exe)?$') {
            [void]$violations.Add("${Path}:$lineNumber`: direct nested PowerShell launch")
            continue
        }

        $literalArguments = @($command.CommandElements | Select-Object -Skip 1 | Where-Object {
                $_ -is [System.Management.Automation.Language.StringConstantExpressionAst]
            } | ForEach-Object { $_.Value })
        if ($literalArguments | Where-Object { $_ -match '^(?i:pwsh|powershell)(?:\.exe)?$' }) {
            [void]$violations.Add("${Path}:$lineNumber`: nested PowerShell executable passed to '$commandName'")
        }

        if ($commandName -eq 'Start-Process' -and
            $command.Extent.Text -notmatch '(?i)(?:-\s*WindowStyle\s*(?:=|\s)\s*[''"]?Hidden[''"]?(?=\s|$)|(?<!\w)-NoNewWindow(?=\s|$))') {
            [void]$violations.Add("${Path}:$lineNumber`: Start-Process lacks hidden-window containment")
        }
    }

    return $violations.ToArray()
}

if ($SelfTest) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) "nested-pwsh-guard-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $selfTestRoot -Force | Out-Null
    try {
        $directPath = Join-Path $selfTestRoot 'direct.ps1'
        $processPath = Join-Path $selfTestRoot 'process.ps1'
        $embeddedPath = Join-Path $selfTestRoot 'embedded.ps1'
        $embeddedSafePath = Join-Path $selfTestRoot 'embedded-safe.ps1'
        $safePath = Join-Path $selfTestRoot 'safe.ps1'
        [IO.File]::WriteAllText($directPath, '& pwsh -NoProfile')
        [IO.File]::WriteAllText($processPath, "Start-Process 'example.exe'")
        [IO.File]::WriteAllText($embeddedPath, @'
$nested = @"
Start-Process -FilePath 'example.exe'
"@
Invoke-NestedPwsh -ArgumentList $nested
'@)
        [IO.File]::WriteAllText($embeddedSafePath, @'
$nested = @"
Start-Process -FilePath 'example.exe' -WindowStyle Hidden
"@
Invoke-NestedPwsh -ArgumentList $nested
'@)
        [IO.File]::WriteAllText($safePath, "Invoke-NestedPwsh -ArgumentList @('-NoProfile')")
        if (@(Get-LaunchViolations $directPath).Count -eq 0) {
            throw 'The guard self-test did not reject a direct nested PowerShell launch.'
        }
        if (@(Get-LaunchViolations $processPath).Count -eq 0) {
            throw 'The guard self-test did not reject a visible Start-Process launch.'
        }
        if (@(Get-LaunchViolations $embeddedPath).Count -eq 0) {
            throw 'The guard self-test did not reject a visible Start-Process launch embedded in a here-string.'
        }
        if (@(Get-LaunchViolations $embeddedSafePath).Count -ne 0) {
            throw 'The guard self-test rejected a hidden Start-Process launch embedded in a here-string.'
        }
        if (@(Get-LaunchViolations $safePath).Count -ne 0) {
            throw 'The guard self-test rejected a helper-mediated launch.'
        }

        $safeCSharpPath = Join-Path $selfTestRoot 'safe.cs'
        $unsafeCSharpPath = Join-Path $selfTestRoot 'unsafe.cs'
        [IO.File]::WriteAllText($safeCSharpPath, @'
using System.Diagnostics;
class Fixture {
    void Run() {
        var startInfo = new ProcessStartInfo {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
    }
}
'@)
        [IO.File]::WriteAllText($unsafeCSharpPath, @'
using System.Diagnostics;
class Fixture {
    void Run() {
        var startInfo = new ProcessStartInfo { UseShellExecute = false };
        using var process = Process.Start(startInfo);
    }
}
'@)
        $safeCSharpReport = Get-CSharpLaunchReport $safeCSharpPath
        if ($safeCSharpReport.ProcessStartInfoCount -ne 1 -or $safeCSharpReport.ProcessStartCount -ne 1 -or $safeCSharpReport.Violations.Count -ne 0) {
            throw 'The guard self-test rejected a contained C# ProcessStartInfo/Process.Start launch.'
        }
        $unsafeCSharpReport = Get-CSharpLaunchReport $unsafeCSharpPath
        if ($unsafeCSharpReport.Violations.Count -eq 0) {
            throw 'The guard self-test did not reject an uncontained C# ProcessStartInfo/Process.Start launch.'
        }

        $fixturePath = Join-Path $repositoryRoot 'tests/KeelMatrix.ResilienceSpec.Tests/ExternalCommandFixture.ps1'
        if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
            throw "The nested PowerShell launch guard fixture is missing: $fixturePath"
        }
        $fixtureViolations = @(Get-LaunchViolations $fixturePath)
        if ($fixtureViolations.Count -ne 0) {
            throw "The external-command fixture contains uncontained launch sites: $($fixtureViolations -join '; ')"
        }
    }
    finally {
        Remove-Item -LiteralPath $selfTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Output 'Nested PowerShell launch guard self-test passed.'
    exit 0
}

if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "Shared nested PowerShell launch helper is missing: $helperPath"
}

$scriptFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.ps1' |
    Where-Object {
        $_.FullName -notin @($helperPath, $routingHelperPath, $guardPath) -and
        $_.FullName -notmatch '[\\/]((\.git)|(bin)|(obj)|(artifacts)|_probe[\\/]corpus)([\\/]|$)'
    }
$violations = @($scriptFiles | ForEach-Object { Get-LaunchViolations $_.FullName })
if ($violations.Count -gt 0) {
    throw "Visible child process launch sites must use the shared containment helper."
}

$csharpFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.cs' |
    Where-Object {
        $_.FullName -notmatch '[\\/]((\.git)|(bin)|(obj)|(artifacts)|_probe[\\/]corpus)([\\/]|$)'
    }
$csharpReports = @($csharpFiles | ForEach-Object { Get-CSharpLaunchReport $_.FullName })
$csharpProcessStartInfoCount = ($csharpReports | Measure-Object -Property ProcessStartInfoCount -Sum).Sum
$csharpProcessStartCount = ($csharpReports | Measure-Object -Property ProcessStartCount -Sum).Sum
if ($csharpProcessStartInfoCount -lt 1 -or $csharpProcessStartCount -lt 1) {
    throw 'The C# launch guard scan was vacuous: no ProcessStartInfo/Process.Start sites were inspected.'
}
$csharpViolations = @($csharpReports | ForEach-Object { $_.Violations })
if ($csharpViolations.Count -gt 0) {
    throw "C# child process launch sites must use contained ProcessStartInfo instances: $($csharpViolations -join '; ')"
}

Write-Output 'Nested PowerShell launch guard passed.'
