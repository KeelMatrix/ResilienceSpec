[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$fixedDosTime = [uint16]0
$fixedDosDate = [uint16]0x0021
$utf8Flag = [uint16]0x0800
$storedMethod = [uint16]0
$zipVersion = [uint16]20
$crcTable = [uint32[]]::new(256)

for ($index = 0; $index -lt $crcTable.Length; $index++) {
    $value = [uint32]$index
    for ($bit = 0; $bit -lt 8; $bit++) {
        if (($value -band [uint32]1) -ne 0) {
            $value = [uint32]([uint64]3988292384 -bxor [uint64]($value -shr 1))
        }
        else {
            $value = [uint32]($value -shr 1)
        }
    }

    $crcTable[$index] = $value
}

function Get-Crc32 {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    $value = [uint32]4294967295
    foreach ($byte in $Bytes) {
        $tableIndex = [int](([uint64]$value -bxor [uint64]$byte) -band [uint64]255)
        $value = [uint32]([uint64]$script:crcTable[$tableIndex] -bxor [uint64]($value -shr 8))
    }

    return [uint32]([uint64]4294967295 -bxor [uint64]$value)
}

function Get-CanonicalPayloadBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    if ($Name -notmatch '\.(xml|nuspec|rels|psmdcp)$' -and $Name -cne 'LICENSE') {
        return ,$Bytes
    }

    $hasUtf8Bom = $Bytes.Length -ge 3 -and
        $Bytes[0] -eq 0xEF -and
        $Bytes[1] -eq 0xBB -and
        $Bytes[2] -eq 0xBF
    $offset = if ($hasUtf8Bom) { 3 } else { 0 }
    $text = [Text.Encoding]::UTF8.GetString($Bytes, $offset, $Bytes.Length - $offset)
    $text = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $canonical = [Text.Encoding]::UTF8.GetBytes($text)
    if (-not $hasUtf8Bom) {
        return ,$canonical
    }

    $withBom = [byte[]]::new($canonical.Length + 3)
    $withBom[0] = 0xEF
    $withBom[1] = 0xBB
    $withBom[2] = 0xBF
    [Array]::Copy($canonical, 0, $withBom, 3, $canonical.Length)
    return ,$withBom
}

function Get-ArchiveRecords {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $records = @()
        foreach ($entry in ($archive.Entries | Sort-Object FullName)) {
            $stream = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try {
                $stream.CopyTo($memory)
                $bytes = [byte[]]$memory.ToArray()
            }
            finally {
                $memory.Dispose()
                $stream.Dispose()
            }

            $name = $entry.FullName.Replace('\', '/')
            $bytes = Get-CanonicalPayloadBytes -Name $name -Bytes $bytes
            $hash = [Security.Cryptography.SHA256]::Create()
            try {
                $digest = (($hash.ComputeHash($bytes) | ForEach-Object ToString x2) -join '')
            }
            finally {
                $hash.Dispose()
            }

            $nameBytes = [Text.Encoding]::UTF8.GetBytes($name)
            if ($nameBytes.Length -gt [uint16]::MaxValue) {
                throw "Archive entry name is too long: '$name'."
            }

            $records += [pscustomobject]@{
                Name = $name
                NameBytes = $nameBytes
                Bytes = $bytes
                Length = [uint32]$bytes.Length
                Hash = $digest
                Crc32 = Get-Crc32 -Bytes $bytes
            }
        }

        return $records
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-SameManifest {
    param(
        [Parameter(Mandatory = $true)][object[]]$Expected,
        [Parameter(Mandatory = $true)][object[]]$Actual
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "Normalized archive entry count changed from $($Expected.Count) to $($Actual.Count)."
    }

    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Expected[$index].Name -cne $Actual[$index].Name -or
            $Expected[$index].Length -ne $Actual[$index].Length -or
            $Expected[$index].Hash -cne $Actual[$index].Hash) {
            throw "Normalized archive entry changed: '$($Expected[$index].Name)'."
        }
    }
}

function Write-CanonicalArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Records
    )

    $stream = [IO.File]::Create($Path)
    $writer = [IO.BinaryWriter]::new($stream)
    $centralRecords = [Collections.Generic.List[object]]::new()
    try {
        foreach ($record in $Records) {
            $localOffset = [uint32]$stream.Position
            $writer.Write([uint32]0x04034B50)
            $writer.Write($zipVersion)
            $writer.Write($utf8Flag)
            $writer.Write($storedMethod)
            $writer.Write($fixedDosTime)
            $writer.Write($fixedDosDate)
            $writer.Write([uint32]$record.Crc32)
            $writer.Write([uint32]$record.Length)
            $writer.Write([uint32]$record.Length)
            $writer.Write([uint16]$record.NameBytes.Length)
            $writer.Write([uint16]0)
            $writer.Write($record.NameBytes)
            $writer.Write($record.Bytes)

            $centralRecords.Add([pscustomobject]@{
                    Record = $record
                    LocalOffset = $localOffset
                })
        }

        $centralOffset = [uint32]$stream.Position
        foreach ($central in $centralRecords) {
            $record = $central.Record
            $writer.Write([uint32]0x02014B50)
            $writer.Write($zipVersion)
            $writer.Write($zipVersion)
            $writer.Write($utf8Flag)
            $writer.Write($storedMethod)
            $writer.Write($fixedDosTime)
            $writer.Write($fixedDosDate)
            $writer.Write([uint32]$record.Crc32)
            $writer.Write([uint32]$record.Length)
            $writer.Write([uint32]$record.Length)
            $writer.Write([uint16]$record.NameBytes.Length)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint32]0)
            $writer.Write([uint32]$central.LocalOffset)
            $writer.Write($record.NameBytes)
        }

        $centralSize = [uint32]($stream.Position - $centralOffset)
        $entryCount = [uint16]$centralRecords.Count
        $writer.Write([uint32]0x06054B50)
        $writer.Write([uint16]0)
        $writer.Write([uint16]0)
        $writer.Write($entryCount)
        $writer.Write($entryCount)
        $writer.Write($centralSize)
        $writer.Write($centralOffset)
        $writer.Write([uint16]0)
    }
    finally {
        $writer.Dispose()
    }
}

$temporaryPath = $null
try {
    $resolvedPath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
    $temporaryPath = "$resolvedPath.normalizing-$([Guid]::NewGuid().ToString('N'))"
    $records = @(Get-ArchiveRecords -Path $resolvedPath)
    Write-CanonicalArchive -Path $temporaryPath -Records $records

    $normalizedRecords = @(Get-ArchiveRecords -Path $temporaryPath)
    Assert-SameManifest -Expected $records -Actual $normalizedRecords
    Move-Item -LiteralPath $temporaryPath -Destination $resolvedPath -Force

    Write-Output ("Normalized archive: {0} entries={1} timestamp=1980-01-01 00:00:00 ZIP-local-time storage=uncompressed canonical-headers UTF-8-text=LF" -f $resolvedPath, $records.Count)
}
catch {
    if ($temporaryPath -and (Test-Path -LiteralPath $temporaryPath)) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }

    [Console]::Error.WriteLine("Package normalization failed: $($_.Exception.Message)")
    exit 1
}
