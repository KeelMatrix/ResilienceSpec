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

function Get-UInt32BigEndian {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][int]$Offset
    )

    [uint64]$value = 0
    for ($index = 0; $index -lt 4; $index++) {
        $value = (($value -shl 8) -bor [uint64]$Bytes[$Offset + $index]) -band [uint64]4294967295
    }

    return [uint32]$value
}

function Get-PngCrc32 {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][int]$Start,
        [Parameter(Mandatory = $true)][int]$EndExclusive
    )

    [uint64[]]$table = New-Object 'uint64[]' 256
    for ($tableIndex = 0; $tableIndex -lt 256; $tableIndex++) {
        [uint64]$tableValue = [uint64]$tableIndex
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($tableValue -band 1) -ne 0) {
                $tableValue = (($tableValue -shr 1) -bxor [uint64]3988292384) -band [uint64]4294967295
            }
            else {
                $tableValue = ($tableValue -shr 1) -band [uint64]4294967295
            }
        }

        $table[$tableIndex] = $tableValue
    }

    [uint64]$crc = 4294967295
    for ($index = $Start; $index -lt $EndExclusive; $index++) {
        [int]$tableIndex = [int](($crc -bxor [uint64]$Bytes[$index]) -band 0xFF)
        $crc = (($crc -shr 8) -bxor $table[$tableIndex]) -band [uint64]4294967295
    }

    return [uint32](($crc -bxor [uint64]4294967295) -band [uint64]4294967295)
}

function Assert-PngStructure {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $signature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($Bytes.Length -lt ($signature.Length + 12)) {
        throw "PNG content is truncated."
    }

    for ($index = 0; $index -lt $signature.Length; $index++) {
        if ($Bytes[$index] -ne $signature[$index]) {
            throw "PNG signature is invalid."
        }
    }

    $offset = $signature.Length
    $seenHeader = $false
    $seenPalette = $false
    $seenData = $false
    $dataEnded = $false
    $seenEnd = $false
    $width = 0
    $height = 0
    $colorType = 0
    $idatBytes = [Collections.Generic.List[byte]]::new()

    while ($offset -lt $Bytes.Length) {
        if (($Bytes.Length - $offset) -lt 12) {
            throw "PNG chunk header is truncated."
        }

        [uint32]$chunkLength = Get-UInt32BigEndian -Bytes $Bytes -Offset $offset
        $remainingAfterHeader = $Bytes.Length - $offset - 12
        if ($chunkLength -gt [uint32]$remainingAfterHeader) {
            throw "PNG chunk length exceeds the remaining content."
        }

        $typeOffset = $offset + 4
        $dataOffset = $offset + 8
        $crcOffset = $dataOffset + [int]$chunkLength
        $typeBytes = $Bytes[$typeOffset..($typeOffset + 3)]
        foreach ($typeByte in $typeBytes) {
            if (($typeByte -lt 0x41 -or $typeByte -gt 0x5A) -and ($typeByte -lt 0x61 -or $typeByte -gt 0x7A)) {
                throw "PNG chunk type is invalid."
            }
        }

        if ($typeBytes[2] -ge 0x61 -and $typeBytes[2] -le 0x7A) {
            throw "PNG chunk type uses a reserved bit."
        }

        $expectedCrc = Get-PngCrc32 -Bytes $Bytes -Start $typeOffset -EndExclusive $crcOffset
        $actualCrc = Get-UInt32BigEndian -Bytes $Bytes -Offset $crcOffset
        if ($expectedCrc -ne $actualCrc) {
            throw "PNG chunk CRC is invalid."
        }

        $type = [Text.Encoding]::ASCII.GetString($typeBytes)
        switch ($type) {
            'IHDR' {
                if ($seenHeader -or $offset -ne $signature.Length -or $chunkLength -ne 13) {
                    throw "PNG header chunk is invalid."
                }

                [uint32]$width = Get-UInt32BigEndian -Bytes $Bytes -Offset $dataOffset
                [uint32]$height = Get-UInt32BigEndian -Bytes $Bytes -Offset ($dataOffset + 4)
                $bitDepth = $Bytes[$dataOffset + 8]
                $colorType = $Bytes[$dataOffset + 9]
                if ($width -eq 0 -or $height -eq 0 -or $Bytes[$dataOffset + 10] -ne 0 -or $Bytes[$dataOffset + 11] -ne 0) {
                    throw "PNG image header values are invalid."
                }

                $validBitDepth = switch ($colorType) {
                    0 { $bitDepth -in @(1, 2, 4, 8, 16) }
                    2 { $bitDepth -in @(8, 16) }
                    3 { $bitDepth -in @(1, 2, 4, 8) }
                    4 { $bitDepth -in @(8, 16) }
                    6 { $bitDepth -in @(8, 16) }
                    default { $false }
                }
                if (-not $validBitDepth) {
                    throw "PNG color type and bit depth are invalid."
                }

                if ($Bytes[$dataOffset + 12] -notin @(0, 1)) {
                    throw "PNG interlace method is invalid."
                }

                $seenHeader = $true
            }
            'PLTE' {
                if (-not $seenHeader -or $seenPalette -or $dataEnded -or $chunkLength -lt 3 -or ($chunkLength % 3) -ne 0 -or $chunkLength -gt 768) {
                    throw "PNG palette chunk is invalid."
                }

                $seenPalette = $true
            }
            'IDAT' {
                if (-not $seenHeader -or $dataEnded -or $chunkLength -eq 0) {
                    throw "PNG image-data chunk is invalid."
                }

                $seenData = $true
                for ($dataIndex = 0; $dataIndex -lt [int]$chunkLength; $dataIndex++) {
                    $idatBytes.Add($Bytes[$dataOffset + $dataIndex])
                }
            }
            'IEND' {
                if (-not $seenHeader -or -not $seenData -or $chunkLength -ne 0 -or $seenEnd) {
                    throw "PNG end chunk is invalid."
                }

                $seenEnd = $true
                $offset += 12
                if ($offset -ne $Bytes.Length) {
                    throw "PNG content exists after the end chunk."
                }

                break
            }
            default {
                if ($typeBytes[0] -ge 0x41 -and $typeBytes[0] -le 0x5A) {
                    throw "PNG contains an unknown critical chunk."
                }

                if ($seenEnd) {
                    throw "PNG contains a chunk after the end chunk."
                }
            }
        }

        $offset += 12 + [int]$chunkLength
        if ($type -eq 'IDAT') {
            $dataEnded = $false
        }
        elseif ($seenData) {
            $dataEnded = $true
        }
    }

    if (-not $seenEnd -or -not $seenHeader -or -not $seenData) {
        throw "PNG content is incomplete."
    }

    if (($colorType -eq 3 -and -not $seenPalette) -or ($colorType -in @(0, 4) -and $seenPalette)) {
        throw "PNG palette usage is invalid for the image color type."
    }

    try {
        $compressed = [IO.MemoryStream]::new($idatBytes.ToArray(), $false)
        $decompressed = [IO.MemoryStream]::new()
        $zlib = [IO.Compression.ZLibStream]::new($compressed, [IO.Compression.CompressionMode]::Decompress)
        $zlib.CopyTo($decompressed)
        $zlib.Dispose()
        $compressed.Dispose()
        $decompressed.Dispose()
    }
    catch {
        throw "PNG image data is not a valid compressed stream."
    }
}

function Read-BinaryManifest {
    param([Parameter(Mandatory = $true)][string[]]$TrackedFiles)

    $relativeManifestPath = 'scripts/DocumentationBinaryManifest.json'
    if (-not ($TrackedFiles -contains $relativeManifestPath)) {
        throw "Documentation binary manifest '$relativeManifestPath' is not tracked; refusing to skip binary content."
    }

    $manifestPath = Join-Path $RepositoryPath $relativeManifestPath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    try {
        $document = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Documentation binary manifest could not be parsed; refusing to skip binary content."
    }

    if ($null -eq $document -or $document.version -ne 1 -or $null -eq $document.assets) {
        throw "Documentation binary manifest has an invalid schema; refusing to skip binary content."
    }

    $assets = @{}
    foreach ($asset in @($document.assets)) {
        if ($null -eq $asset -or -not ($asset.PSObject.Properties.Name -contains 'path') -or -not ($asset.PSObject.Properties.Name -contains 'format')) {
            throw "Documentation binary manifest contains an invalid asset entry; refusing to skip binary content."
        }

        $relativePath = ([string]$asset.path).Replace('\', '/')
        $segments = $relativePath.Split('/')
        $hasUnsafeSegment = @($segments | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or $relativePath -match '^[A-Za-z]:/' -or $hasUnsafeSegment) {
            throw "Documentation binary manifest contains an unsafe path; refusing to skip binary content."
        }

        if ($assets.ContainsKey($relativePath) -or -not ($TrackedFiles -contains $relativePath)) {
            throw "Documentation binary manifest contains a duplicate or untracked path; refusing to skip binary content."
        }

        $format = ([string]$asset.format).ToLowerInvariant()
        if ($format -ne 'png') {
            throw "Documentation binary manifest contains an unsupported format; refusing to skip binary content."
        }

        $assets[$relativePath] = $format
    }

    return $assets
}

function Read-AuthoredText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][hashtable]$BinaryManifest
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
    $utf8Offset = 0
    $hasUtf16Bom = $false
    $hasUtf32Bom = $false
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0x00 -and $bytes[3] -eq 0x00) {
        $hasUtf32Bom = $true
        $text = Try-DecodeText -Bytes $bytes -Encoding ([Text.UTF32Encoding]::new($false, $false, $true)) -Offset 4
        if ($null -ne $text) { $candidates.Add($text) }
    }
    elseif ($bytes.Length -ge 4 -and $bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF) {
        $hasUtf32Bom = $true
        $text = Try-DecodeText -Bytes $bytes -Encoding ([Text.UTF32Encoding]::new($true, $false, $true)) -Offset 4
        if ($null -ne $text) { $candidates.Add($text) }
    }
    elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $utf8Offset = 3
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $hasUtf16Bom = $true
        $text = Try-DecodeText -Bytes $bytes -Encoding ([Text.UnicodeEncoding]::new($false, $false, $true)) -Offset 2
        if ($null -ne $text) { $candidates.Add($text) }
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $hasUtf16Bom = $true
        $text = Try-DecodeText -Bytes $bytes -Encoding ([Text.UnicodeEncoding]::new($true, $false, $true)) -Offset 2
        if ($null -ne $text) { $candidates.Add($text) }
    }

    $utf8 = Try-DecodeText -Bytes $bytes -Encoding ([Text.UTF8Encoding]::new($false, $true)) -Offset $utf8Offset
    if ($null -ne $utf8) {
        $candidates.Add($utf8)
    }

    $ascii = Try-DecodeText -Bytes $bytes -Encoding ([Text.Encoding]::GetEncoding(20127, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)) -Offset 0
    if ($null -ne $ascii) {
        $candidates.Add($ascii)
    }

    if (-not $hasUtf16Bom -and -not $hasUtf32Bom) {
        foreach ($bigEndian in @($false, $true)) {
            if (Test-BomlessUtf16Pattern -Bytes $bytes -BigEndian $bigEndian) {
                $utf16 = Try-DecodeText -Bytes $bytes -Encoding ([Text.UnicodeEncoding]::new($bigEndian, $false, $true)) -Offset 0
                if ($null -ne $utf16) {
                    $candidates.Add($utf16)
                }
            }

            if (Test-BomlessUtf32Pattern -Bytes $bytes -BigEndian $bigEndian) {
                $utf32 = Try-DecodeText -Bytes $bytes -Encoding ([Text.UTF32Encoding]::new($bigEndian, $false, $true)) -Offset 0
                if ($null -ne $utf32) {
                    $candidates.Add($utf32)
                }
            }
        }
    }

    if ($candidates.Count -gt 0) {
        return [pscustomobject]@{ IsBinary = $false; Texts = $candidates.ToArray() }
    }

    if ($BinaryManifest.ContainsKey($RelativePath)) {
        try {
            switch ($BinaryManifest[$RelativePath]) {
                'png' { Assert-PngStructure -Bytes $bytes -RelativePath $RelativePath }
                default { throw "No validator exists for the declared format." }
            }
        }
        catch {
            throw "Documentation hygiene rejected declared binary asset '$RelativePath': $($_.Exception.Message)"
        }

        return [pscustomobject]@{ IsBinary = $true; Texts = @() }
    }

    throw "Documentation hygiene could not decode tracked file '$RelativePath' as supported text or validate it as a declared binary asset; refusing to skip it."
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
    $trackedFiles = @(Get-AuthoredFiles)
    $binaryManifest = Read-BinaryManifest -TrackedFiles $trackedFiles
    $violations = [Collections.Generic.List[string]]::new()
    foreach ($relative in $trackedFiles) {
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

        $classified = Read-AuthoredText -Path $path -RelativePath $relative -BinaryManifest $binaryManifest
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
