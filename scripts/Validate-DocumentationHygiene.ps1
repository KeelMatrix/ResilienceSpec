[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$includedExtensions = @(
    '', '.config', '.cs', '.csproj', '.editorconfig', '.gitattributes', '.gitignore', '.json', '.md', '.props',
    '.ps1', '.sh', '.slnx', '.targets', '.txt', '.yml', '.yaml'
)

function Convert-CodePoints {
    param([Parameter(Mandatory = $true)][int[]]$Codes)

    return -join ($Codes | ForEach-Object { [char]$_ })
}

function Get-AuthoredFiles {
    $tracked = @(& git -C $RepositoryPath ls-files 2>$null)
    if ($LASTEXITCODE -eq 0 -and $tracked.Count -gt 0) {
        return $tracked
    }

    return @(
        Get-ChildItem -LiteralPath $RepositoryPath -File -Recurse |
            ForEach-Object { [IO.Path]::GetRelativePath($RepositoryPath, $_.FullName).Replace('\', '/') }
    )
}

$processName = Convert-CodePoints @(112, 97, 112, 101, 114, 99, 108, 105, 112)
$runtimeTerms = @(
    (Convert-CodePoints @(99, 111, 100, 101, 120)),
    (Convert-CodePoints @(100, 101, 101, 112, 115, 101, 101, 107)),
    (Convert-CodePoints @(99, 104, 97, 116, 103, 112, 116)),
    (Convert-CodePoints @(99, 108, 97, 117, 100, 101)),
    (Convert-CodePoints @(103, 112, 116)),
    (Convert-CodePoints @(115, 111, 108)),
    (Convert-CodePoints @(108, 117, 110, 97)),
    ((Convert-CodePoints @(100, 115)) + ' ' + (Convert-CodePoints @(102, 108, 97, 115, 104))),
    (Convert-CodePoints @(97, 103, 101, 110, 116))
)
$processTerms = @(
    (Convert-CodePoints @(111, 114, 99, 104, 101, 115, 116, 114, 97, 116, 105, 111, 110)),
    (Convert-CodePoints @(111, 114, 99, 104, 101, 115, 116, 114, 97, 116, 111, 114)),
    (Convert-CodePoints @(112, 114, 111, 109, 112, 116)),
    ((Convert-CodePoints @(99, 104, 97, 105, 110)) + '-' + (Convert-CodePoints @(111, 102)) + '-' + (Convert-CodePoints @(116, 104, 111, 117, 103, 104, 116))),
    ((Convert-CodePoints @(114, 101, 118, 105, 101, 119)) + ' ' + (Convert-CodePoints @(112, 114, 111, 99, 101, 115, 115))),
    ((Convert-CodePoints @(102, 114, 111, 110, 116, 105, 101, 114)) + ' ' + (Convert-CodePoints @(114, 101, 118, 105, 101, 119)))
)
$metadataTerms = @(
    ((Convert-CodePoints @(105, 110, 116, 101, 114, 110, 97, 108)) + ' ' + (Convert-CodePoints @(116, 97, 115, 107)) + ' ' + (Convert-CodePoints @(105, 100, 101, 110, 116, 105, 102, 105, 101, 114, 115))),
    ((Convert-CodePoints @(105, 115, 115, 117, 101)) + ' ' + (Convert-CodePoints @(105, 100, 101, 110, 116, 105, 102, 105, 101, 115))),
    ((Convert-CodePoints @(109, 111, 100, 101, 108)) + ' ' + (Convert-CodePoints @(109, 101, 116, 97, 100, 97, 116, 97))),
    ((Convert-CodePoints @(112, 114, 111, 104, 105, 98, 105, 116, 101, 100)) + ' ' + (Convert-CodePoints @(97, 116, 116, 114, 105, 98, 117, 116, 105, 111, 110))),
    ((Convert-CodePoints @(99, 111, 45, 97, 117, 116, 104, 111, 114)) + '.*' + (Convert-CodePoints @(116, 114, 97, 105, 108, 101, 114, 115))),
    ((Convert-CodePoints @(103, 101, 110, 101, 114, 97, 116, 101, 100)) + ' ' + (Convert-CodePoints @(98, 121)) + ' ' + (Convert-CodePoints @(97, 103, 101, 110, 116)))
)

$runtimePattern = '(?i)\b(?:' + (($runtimeTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
$processPattern = '(?i)\b(?:' + (($processTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
$metadataPattern = '(?i)\b(?:' + (($metadataTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
$forbiddenPatterns = @(
    @{ Pattern = '(?i)\b[A-Z]{2,10}-\d{2,}\b'; Label = 'non-product identifier' },
    @{ Pattern = "(?i)\b$processName\b"; Label = 'non-product name' },
    @{ Pattern = $runtimePattern; Label = 'runtime vocabulary' },
    @{ Pattern = $processPattern; Label = 'process vocabulary' },
    @{ Pattern = $metadataPattern; Label = 'provenance wording' }
)

try {
    $violations = [Collections.Generic.List[string]]::new()
    foreach ($relative in Get-AuthoredFiles) {
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative.StartsWith('.git/', [StringComparison]::OrdinalIgnoreCase) -or
            $includedExtensions -notcontains ([IO.Path]::GetExtension($relative).ToLowerInvariant())) {
            continue
        }

        $path = Join-Path $RepositoryPath $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $lines = @(Get-Content -LiteralPath $path)
        for ($lineNumber = 0; $lineNumber -lt $lines.Count; $lineNumber++) {
            foreach ($forbidden in $forbiddenPatterns) {
                if ($lines[$lineNumber] -match $forbidden.Pattern) {
                    $violations.Add("${relative}:$($lineNumber + 1): $($forbidden.Label)")
                }
            }
        }
    }

    if ($violations.Count -gt 0) {
        $violations | ForEach-Object { [Console]::Error.WriteLine($_) }
        throw "Documentation hygiene failed with $($violations.Count) violation(s)."
    }

    Write-Output 'Documentation hygiene passed: the authored tree contains no non-product process language.'
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
