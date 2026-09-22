[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$SymbolsPath,

    [string]$ExpectedVersion = '0.1.0',

    [string]$ExpectedRepositoryCommit,

    [string]$ExpectedRepositoryUrl = 'https://github.com/KeelMatrix/ResilienceSpec',

    [string]$ExpectedIconPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packageId = 'KeelMatrix.ResilienceSpec'
$description = 'Verify how your real .NET HttpClient resilience chain retries, delays, times out, and finishes under deterministic scripted failures for testing and CI.'
$tags = 'httpclient resilience retry polly testing ci retry-after timeout dotnet'
$targetFramework = 'net8.0'
$packageNamespace = 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'
$normalizedArchiveTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-ExpectedCommit {
    param([string]$Commit)

    if (-not [string]::IsNullOrWhiteSpace($Commit)) {
        $normalized = $Commit.Trim().ToLowerInvariant()
        Assert-Contract ($normalized -match '^[0-9a-f]{40}$') "Expected repository commit '$Commit' is not a 40-character Git SHA."
        return $normalized
    }

    $repo = Split-Path -Parent $PSScriptRoot
    $resolved = (& git -C $repo rev-parse HEAD 2>&1 | Out-String).Trim()
    Assert-Contract ($LASTEXITCODE -eq 0 -and $resolved -match '^[0-9a-fA-F]{40}$') 'Unable to resolve the expected repository commit.'
    return $resolved.ToLowerInvariant()
}

function Read-ZipEntryBytes {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = @($Archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq $Name })
    Assert-Contract ($matches.Count -eq 1) "Archive entry '$Name' must exist exactly once."

    $stream = $matches[0].Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($memory)
        return ,([byte[]]$memory.ToArray())
    }
    finally {
        $memory.Dispose()
        $stream.Dispose()
    }
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $bytes = Read-ZipEntryBytes -Archive $Archive -Name $Name
    return ([Text.Encoding]::UTF8.GetString($bytes)).TrimStart([char]0xFEFF)
}

function Read-XmlEntry {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $document = [Xml.XmlDocument]::new()
    $document.LoadXml((Read-ZipEntryText -Archive $Archive -Name $Name))
    return $document
}

function Get-EntryHash {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        return (($hash.ComputeHash($stream) | ForEach-Object ToString x2) -join '')
    }
    finally {
        $hash.Dispose()
        $stream.Dispose()
    }
}

function Assert-CanonicalTextEntries {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$ArchiveDescription
    )

    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name -notmatch '\.(xml|nuspec|rels|psmdcp|md)$' -and $name -cne 'LICENSE') {
            continue
        }

        $text = Read-ZipEntryText -Archive $Archive -Name $name
        Assert-Contract (-not $text.Contains("`r")) "$ArchiveDescription text entry '$name' is not LF-only."
    }
}

function Write-EntryManifest {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$ArchiveDescription
    )

    foreach ($entry in ($Archive.Entries | Sort-Object FullName)) {
        Write-Output ("{0} entry SHA256: {1}={2}" -f $ArchiveDescription, $entry.FullName.Replace('\', '/'), (Get-EntryHash -Entry $entry))
    }
}

function Get-MetadataText {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)][string]$XPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $namespace = [Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespace.AddNamespace('n', $packageNamespace)
    $node = $Document.SelectSingleNode($XPath, $namespace)
    Assert-Contract ($null -ne $node) "Package metadata is missing $Description."
    return $node.InnerText
}

function Get-MetadataNode {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)][string]$XPath
    )

    $namespace = [Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespace.AddNamespace('n', $packageNamespace)
    return $Document.SelectSingleNode($XPath, $namespace)
}

function Assert-ArchiveEntries {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string[]]$Allowlist,
        [Parameter(Mandatory = $true)][string]$ArchiveDescription
    )

    $names = @($Archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $duplicates = @($names | Group-Object -CaseSensitive | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        $duplicateNames = @($duplicates | ForEach-Object Name) -join ', '
        throw "$ArchiveDescription contains duplicate archive entries: $duplicateNames."
    }

    foreach ($name in $names) {
        $allowed = $false
        foreach ($pattern in $Allowlist) {
            if ($name -cmatch $pattern) {
                $allowed = $true
                break
            }
        }

        Assert-Contract $allowed "$ArchiveDescription contains unexpected archive entry '$name'."
    }
}

function Assert-ArchiveTimestamps {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$ArchiveDescription
    )

    foreach ($entry in $Archive.Entries) {
        Assert-Contract ($entry.LastWriteTime.DateTime -eq $normalizedArchiveTimestamp.DateTime) "$ArchiveDescription entry '$($entry.FullName)' has a non-normalized archive timestamp."
    }
}

function Assert-ArchiveStorage {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$ArchiveDescription
    )

    foreach ($entry in $Archive.Entries) {
        Assert-Contract ($entry.CompressedLength -eq $entry.Length) "$ArchiveDescription entry '$($entry.FullName)' is compressed instead of stored."
    }
}

function Assert-PngContract {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$ExpectedIconFile
    )

    Assert-Contract ($Bytes.Length -le 200000) 'Package icon must be no larger than 200 KB.'
    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    Assert-Contract ($Bytes.Length -ge 24) 'Package icon is not a complete PNG.'
    for ($index = 0; $index -lt $signature.Count; $index++) {
        Assert-Contract ($Bytes[$index] -eq $signature[$index]) 'Package icon is not a PNG image.'
    }

    $width = ([long]$Bytes[16] * 16777216) + ([long]$Bytes[17] * 65536) + ([long]$Bytes[18] * 256) + $Bytes[19]
    $height = ([long]$Bytes[20] * 16777216) + ([long]$Bytes[21] * 65536) + ([long]$Bytes[22] * 256) + $Bytes[23]
    Assert-Contract ($width -eq 512 -and $height -eq 512) "Package icon must be 512x512 (was ${width}x${height})."

    Assert-Contract (Test-Path -LiteralPath $ExpectedIconFile -PathType Leaf) "Repository icon was not found: $ExpectedIconFile"
    $expectedHash = (Get-FileHash -LiteralPath $ExpectedIconFile -Algorithm SHA256).Hash
    $actualHash = ([Security.Cryptography.SHA256]::Create().ComputeHash($Bytes) | ForEach-Object ToString x2) -join ''
    Assert-Contract ($actualHash -ieq $expectedHash) 'The package icon does not match the repository icon.'
}

function Assert-Metadata {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit
    )

    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:id' -Description 'the package ID') -ceq $packageId) 'Package ID is incorrect.'
    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:version' -Description 'the package version') -ceq $ExpectedVersion) 'Package version is incorrect.'
    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:authors' -Description 'the authors') -ceq 'KeelMatrix') 'Package authors are incorrect.'
    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:copyright' -Description 'the copyright') -ceq 'KeelMatrix') 'Package copyright is incorrect.'
    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:description' -Description 'the description') -ceq $description) 'Package description is incorrect.'
    Assert-Contract (((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:tags' -Description 'the tags').Trim()) -ceq $tags) 'Package tags are incorrect.'
    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:readme' -Description 'the README') -ceq 'README.md') 'Package README metadata must reference README.md.'
    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:icon' -Description 'the icon') -ceq 'icon.png') 'Package icon metadata must reference icon.png.'
    Assert-Contract ((Get-MetadataText -Document $Document -XPath '/n:package/n:metadata/n:projectUrl' -Description 'the project URL') -ceq "$ExpectedRepositoryUrl#readme") 'Package project URL is incorrect.'

    $license = Get-MetadataNode -Document $Document -XPath '/n:package/n:metadata/n:license'
    Assert-Contract ($null -ne $license) 'Package license metadata is missing.'
    Assert-Contract ($license.GetAttribute('type') -ceq 'file' -and $license.InnerText -ceq 'LICENSE') 'Package license metadata must reference LICENSE.'

    $repository = Get-MetadataNode -Document $Document -XPath '/n:package/n:metadata/n:repository'
    Assert-Contract ($null -ne $repository) 'Package repository metadata is missing.'
    Assert-Contract ($repository.GetAttribute('type') -ceq 'git') 'Package repository type must be git.'
    Assert-Contract ($repository.GetAttribute('url') -ceq $ExpectedRepositoryUrl) 'Package repository URL is incorrect.'
    Assert-Contract ($repository.GetAttribute('commit').ToLowerInvariant() -ceq $ExpectedCommit) "Package repository commit must be '$ExpectedCommit'."

    $groups = @(Get-MetadataNode -Document $Document -XPath '/n:package/n:metadata/n:dependencies/n:group')
    Assert-Contract ($groups.Count -eq 1) 'The package must contain exactly one dependency group.'
    Assert-Contract ($groups[0].GetAttribute('targetFramework') -ceq $targetFramework) "The dependency group must target '$targetFramework'."

    $namespace = [Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespace.AddNamespace('n', $packageNamespace)
    $dependencies = @($groups[0].SelectNodes('n:dependency', $namespace))
    Assert-Contract ($dependencies.Count -eq 2) 'The package must declare exactly two runtime dependencies.'

    $expected = @{
        'KeelMatrix.Telemetry' = '0.1.0'
        'Microsoft.Extensions.Http' = '8.0.1'
    }
    foreach ($dependency in $dependencies) {
        $id = $dependency.GetAttribute('id')
        Assert-Contract ($expected.ContainsKey($id)) "Unexpected runtime dependency '$id'."
        Assert-Contract ($dependency.GetAttribute('version') -ceq $expected[$id]) "Runtime dependency '$id' must be exactly '$($expected[$id])'."
        Assert-Contract ($dependency.GetAttribute('exclude') -ceq 'Build,Analyzers') "Runtime dependency '$id' has unexpected exclusions."
    }
}

function Assert-SourceLink {
    param(
        [Parameter(Mandatory = $true)][byte[]]$PdbBytes,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit
    )

    $pdbText = [Text.Encoding]::UTF8.GetString($PdbBytes)
    $prefix = "https://raw.githubusercontent.com/KeelMatrix/ResilienceSpec/$ExpectedCommit/"
    Assert-Contract ($pdbText.Contains($prefix, [StringComparison]::Ordinal)) "The portable PDB does not contain SourceLink data for '$ExpectedCommit'."
}

try {
    $expectedCommit = Get-ExpectedCommit -Commit $ExpectedRepositoryCommit
    $repo = Split-Path -Parent $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($ExpectedIconPath)) {
        $ExpectedIconPath = Join-Path $repo 'icon.png'
    }

    Assert-Contract (Test-Path -LiteralPath $PackagePath -PathType Leaf) "Package was not found: $PackagePath"
    Assert-Contract (Test-Path -LiteralPath $SymbolsPath -PathType Leaf) "Symbol package was not found: $SymbolsPath"
    Assert-Contract ([IO.Path]::GetFileName($PackagePath) -ceq "$packageId.$ExpectedVersion.nupkg") 'The package filename is incorrect.'
    Assert-Contract ([IO.Path]::GetFileName($SymbolsPath) -ceq "$packageId.$ExpectedVersion.snupkg") 'The symbol package filename is incorrect.'

    $package = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        Assert-ArchiveTimestamps -Archive $package -ArchiveDescription 'The package'
        Assert-ArchiveStorage -Archive $package -ArchiveDescription 'The package'
        Assert-CanonicalTextEntries -Archive $package -ArchiveDescription 'The package'
        Assert-ArchiveEntries -Archive $package -ArchiveDescription 'The package' -Allowlist @(
            '^_rels/\.rels$',
            '^\[Content_Types\]\.xml$',
            "^$packageId\.nuspec$",
            '^README\.md$',
            '^LICENSE$',
            '^icon\.png$',
            "^lib/$targetFramework/$packageId\.dll$",
            "^lib/$targetFramework/$packageId\.xml$",
            '^package/services/metadata/core-properties/[^/]+\.psmdcp$',
            '^\.signature\.p7s$'
        )

        $nuspec = Read-XmlEntry -Archive $package -Name "$packageId.nuspec"
        Assert-Metadata -Document $nuspec -ExpectedCommit $expectedCommit

        $readme = Read-ZipEntryText -Archive $package -Name 'README.md'
        $projectReadme = (Get-Content -LiteralPath (Join-Path $repo "src/$packageId/README.md") -Raw).TrimStart([char]0xFEFF)
        Assert-Contract ($readme -ceq $projectReadme) 'The packed README does not match src/KeelMatrix.ResilienceSpec/README.md.'

        $licenseText = Read-ZipEntryText -Archive $package -Name 'LICENSE'
        Assert-Contract ($licenseText.Contains('MIT License', [StringComparison]::Ordinal)) 'The packed LICENSE is not the MIT license.'
        Assert-Contract ($licenseText.Contains('KeelMatrix', [StringComparison]::Ordinal)) 'The packed LICENSE does not name KeelMatrix.'

        Assert-PngContract -Bytes (Read-ZipEntryBytes -Archive $package -Name 'icon.png') -ExpectedIconFile $ExpectedIconPath
        $packagePdb = Read-ZipEntryBytes -Archive $package -Name "lib/$targetFramework/$packageId.dll"
        Assert-Contract ($packagePdb.Length -gt 0) 'The packed library is empty.'
        Write-EntryManifest -Archive $package -ArchiveDescription 'Package'
    }
    finally {
        $package.Dispose()
    }

    $symbols = [IO.Compression.ZipFile]::OpenRead($SymbolsPath)
    try {
        Assert-ArchiveTimestamps -Archive $symbols -ArchiveDescription 'The symbol package'
        Assert-ArchiveStorage -Archive $symbols -ArchiveDescription 'The symbol package'
        Assert-CanonicalTextEntries -Archive $symbols -ArchiveDescription 'The symbol package'
        Assert-ArchiveEntries -Archive $symbols -ArchiveDescription 'The symbol package' -Allowlist @(
            '^_rels/\.rels$',
            '^\[Content_Types\]\.xml$',
            "^$packageId\.nuspec$",
            "^lib/$targetFramework/$packageId\.pdb$",
            '^package/services/metadata/core-properties/[^/]+\.psmdcp$',
            '^\.signature\.p7s$'
        )

        Assert-SourceLink -PdbBytes (Read-ZipEntryBytes -Archive $symbols -Name "lib/$targetFramework/$packageId.pdb") -ExpectedCommit $expectedCommit
        Write-EntryManifest -Archive $symbols -ArchiveDescription 'Symbols'
    }
    finally {
        $symbols.Dispose()
    }

    $packageHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
    $symbolsHash = (Get-FileHash -LiteralPath $SymbolsPath -Algorithm SHA256).Hash
    Write-Output "Package inspection passed: $([IO.Path]::GetFileName($PackagePath)) SHA256=$packageHash"
    Write-Output "Symbol inspection passed: $([IO.Path]::GetFileName($SymbolsPath)) SHA256=$symbolsHash"
    Write-Output "Normalized archive timestamps verified: $($normalizedArchiveTimestamp.ToString('yyyy-MM-dd HH:mm:ss')) ZIP local time"
    Write-Output 'Normalized archive storage verified: all entries are stored without compression'
    Write-Output 'Canonical text payloads verified: UTF-8 package text entries are LF-only'
    Write-Output "SourceLink commit verified: $expectedCommit"
    exit 0
}
catch {
    [Console]::Error.WriteLine("Package inspection failed: $($_.Exception.Message)")
    exit 1
}
