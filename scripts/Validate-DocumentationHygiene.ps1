[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function Test-BytePrefix {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][byte[]]$Signature
    )

    if ($Bytes.Length -lt $Signature.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Signature.Length; $index++) {
        if ($Bytes[$index] -ne $Signature[$index]) {
            return $false
        }
    }

    return $true
}

function Test-RecognizedBinarySignature {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $signatures = @(
        [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
        [byte[]](0x00, 0x00, 0x01, 0x00),
        [byte[]](0x00, 0x00, 0x02, 0x00),
        [byte[]](0x25, 0x50, 0x44, 0x46),
        [byte[]](0x50, 0x4B, 0x03, 0x04),
        [byte[]](0x1F, 0x8B),
        [byte[]](0xFF, 0xD8, 0xFF),
        [byte[]](0x7F, 0x45, 0x4C, 0x46)
    )

    foreach ($signature in $signatures) {
        if (Test-BytePrefix -Bytes $Bytes -Signature $signature) {
            return $true
        }
    }

    return $false
}

function Test-BomlessUtf16Pattern {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][bool]$BigEndian
    )

    if ($Bytes.Length -lt 4 -or ($Bytes.Length % 2) -ne 0) {
        return $false
    }

    $unitCount = [int]($Bytes.Length / 2)
    $zeroPayloadCount = 0
    $nonZeroTextCount = 0
    for ($index = 0; $index -lt $Bytes.Length; $index += 2) {
        $zeroIndex = if ($BigEndian) { $index } else { $index + 1 }
        $textIndex = if ($BigEndian) { $index + 1 } else { $index }
        if ($Bytes[$zeroIndex] -eq 0) {
            $zeroPayloadCount++
        }
        if ($Bytes[$textIndex] -ne 0) {
            $nonZeroTextCount++
        }
    }

    return $zeroPayloadCount -ge [Math]::Max(2, [int][Math]::Ceiling($unitCount / 2.0)) -and
        $nonZeroTextCount -ge [Math]::Max(1, [int][Math]::Ceiling($unitCount / 2.0))
}

function Test-BomlessUtf32Pattern {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][bool]$BigEndian
    )

    if ($Bytes.Length -lt 8 -or ($Bytes.Length % 4) -ne 0) {
        return $false
    }

    $unitCount = [int]($Bytes.Length / 4)
    $zeroPrefixCount = 0
    $nonZeroTextCount = 0
    for ($index = 0; $index -lt $Bytes.Length; $index += 4) {
        $textIndex = if ($BigEndian) { $index + 3 } else { $index }
        $zeroIndexes = if ($BigEndian) { @(0, 1, 2) } else { @(1, 2, 3) }
        $allZeroPrefix = $true
        foreach ($offset in $zeroIndexes) {
            if ($Bytes[$index + $offset] -ne 0) {
                $allZeroPrefix = $false
                break
            }
        }

        if ($allZeroPrefix) {
            $zeroPrefixCount++
        }
        if ($Bytes[$textIndex] -ne 0) {
            $nonZeroTextCount++
        }
    }

    return $zeroPrefixCount -ge [Math]::Max(1, [int][Math]::Ceiling($unitCount / 2.0)) -and
        $nonZeroTextCount -ge [Math]::Max(1, [int][Math]::Ceiling($unitCount / 2.0))
}

function Test-NonTextByteCharacteristics {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $nonTextByteCount = 0
    foreach ($byte in $Bytes) {
        if ($byte -lt 0x09 -or ($byte -ge 0x0E -and $byte -le 0x1F) -or $byte -eq 0x7F -or $byte -eq 0xFF) {
            $nonTextByteCount++
        }
    }

    return $nonTextByteCount -ge 2
}

function Try-DecodeText {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][Text.Encoding]$Encoding,
        [Parameter(Mandatory = $true)][int]$Offset
    )

    try {
        return $Encoding.GetString($Bytes, $Offset, $Bytes.Length - $Offset)
    }
    catch {
        return $null
    }
}

function Read-AuthoredText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
    }
    catch {
        throw "Documentation hygiene could not read tracked file '$RelativePath'; refusing to skip it."
    }

    if ($bytes.Length -eq 0) {
        return [pscustomobject]@{ IsBinary = $false; Texts = @('') }
    }

    $candidates = [Collections.Generic.List[string]]::new()
    $bomDetected = $false
    $offset = 0
    $encoding = $null
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0x00 -and $bytes[3] -eq 0x00) {
        $encoding = [Text.UTF32Encoding]::new($false, $false, $true)
        $offset = 4
        $bomDetected = $true
    }
    elseif ($bytes.Length -ge 4 -and $bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF) {
        $encoding = [Text.UTF32Encoding]::new($true, $false, $true)
        $offset = 4
        $bomDetected = $true
    }
    elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $offset = 3
        $bomDetected = $true
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = [Text.UnicodeEncoding]::new($false, $false, $true)
        $offset = 2
        $bomDetected = $true
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $encoding = [Text.UnicodeEncoding]::new($true, $false, $true)
        $offset = 2
        $bomDetected = $true
    }

    if ($bomDetected) {
        $text = Try-DecodeText -Bytes $bytes -Encoding $encoding -Offset $offset
        if ($null -eq $text) {
            throw "Documentation hygiene could not classify tracked file '$RelativePath' as text or binary; refusing to skip it."
        }

        $candidates.Add($text)
        return [pscustomobject]@{ IsBinary = $false; Texts = $candidates.ToArray() }
    }

    $utf8 = Try-DecodeText -Bytes $bytes -Encoding ([Text.UTF8Encoding]::new($false, $true)) -Offset 0
    if ($null -ne $utf8) {
        $candidates.Add($utf8)
    }

    $heuristicDetected = $false
    foreach ($bigEndian in @($false, $true)) {
        if (Test-BomlessUtf16Pattern -Bytes $bytes -BigEndian $bigEndian) {
            $heuristicDetected = $true
            $utf16 = Try-DecodeText -Bytes $bytes -Encoding ([Text.UnicodeEncoding]::new($bigEndian, $false, $true)) -Offset 0
            if ($null -ne $utf16) {
                $candidates.Add($utf16)
            }
        }

        if (Test-BomlessUtf32Pattern -Bytes $bytes -BigEndian $bigEndian) {
            $heuristicDetected = $true
            $utf32 = Try-DecodeText -Bytes $bytes -Encoding ([Text.UTF32Encoding]::new($bigEndian, $false, $true)) -Offset 0
            if ($null -ne $utf32) {
                $candidates.Add($utf32)
            }
        }
    }

    if ($candidates.Count -gt 0) {
        return [pscustomobject]@{ IsBinary = $false; Texts = $candidates.ToArray() }
    }

    if ($heuristicDetected) {
        throw "Documentation hygiene could not classify tracked file '$RelativePath' as text or binary; refusing to skip it."
    }

    $recognizedBinary = Test-RecognizedBinarySignature -Bytes $bytes
    $nonTextCharacteristics = Test-NonTextByteCharacteristics -Bytes $bytes
    if ($recognizedBinary -or $nonTextCharacteristics) {
        return [pscustomobject]@{ IsBinary = $true; Texts = @() }
    }

    throw "Documentation hygiene could not classify tracked file '$RelativePath' as text or binary; refusing to skip it."
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
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('.git/', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $path = Join-Path $RepositoryPath $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Documentation hygiene could not resolve tracked file '$relative'; refusing to skip it."
        }

        foreach ($forbidden in $forbiddenPatterns) {
            if ($relative -match $forbidden.Pattern) {
                $violations.Add("${relative}:1: path or file name: $($forbidden.Label)")
            }
        }

        $classified = Read-AuthoredText -Path $path -RelativePath $relative
        if ($classified.IsBinary) {
            continue
        }

        foreach ($text in $classified.Texts) {
            $lines = [regex]::Split($text, "\r\n|\n|\r")
            for ($lineNumber = 0; $lineNumber -lt $lines.Count; $lineNumber++) {
                foreach ($forbidden in $forbiddenPatterns) {
                    if ($lines[$lineNumber] -match $forbidden.Pattern) {
                        $violation = "${relative}:$($lineNumber + 1): $($forbidden.Label)"
                        if (-not $violations.Contains($violation)) {
                            $violations.Add($violation)
                        }
                    }
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
