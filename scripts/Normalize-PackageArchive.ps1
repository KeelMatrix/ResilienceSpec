[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-EntryDigest {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        return [pscustomobject]@{
            Name = $Entry.FullName
            Length = $Entry.Length
            Hash = (($hash.ComputeHash($stream) | ForEach-Object ToString x2) -join '')
        }
    }
    finally {
        $hash.Dispose()
        $stream.Dispose()
    }
}

function Get-ArchiveManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($archive.Entries | Sort-Object FullName | ForEach-Object { Get-EntryDigest -Entry $_ })
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

$temporaryPath = $null
try {
    $resolvedPath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
    $temporaryPath = "$resolvedPath.normalizing-$([Guid]::NewGuid().ToString('N'))"
    $fixedTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $manifest = Get-ArchiveManifest -Path $resolvedPath

    $source = [IO.Compression.ZipFile]::OpenRead($resolvedPath)
    $destinationStream = [IO.File]::Create($temporaryPath)
    try {
        $destination = [IO.Compression.ZipArchive]::new(
            $destinationStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            foreach ($entry in ($source.Entries | Sort-Object FullName)) {
                $normalized = $destination.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                $normalized.LastWriteTime = $fixedTimestamp

                $input = $entry.Open()
                $output = $normalized.Open()
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $destination.Dispose()
        }
    }
    finally {
        $destinationStream.Dispose()
        $source.Dispose()
    }

    $normalizedManifest = Get-ArchiveManifest -Path $temporaryPath
    Assert-SameManifest -Expected $manifest -Actual $normalizedManifest
    Move-Item -LiteralPath $temporaryPath -Destination $resolvedPath -Force

    Write-Output ("Normalized archive: {0} entries={1} timestamp={2} ZIP-local-time" -f $resolvedPath, $manifest.Count, $fixedTimestamp.ToString('yyyy-MM-dd HH:mm:ss'))
}
catch {
    if ($temporaryPath -and (Test-Path -LiteralPath $temporaryPath)) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }

    [Console]::Error.WriteLine("Package normalization failed: $($_.Exception.Message)")
    exit 1
}
