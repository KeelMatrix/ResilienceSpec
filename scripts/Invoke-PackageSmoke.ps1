[CmdletBinding()]
param(
    [string]$ExpectedRepositoryCommit,

    [string]$PackageVersion = '0.1.0',

    [string]$ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $false)][string[]]$Arguments = @(),
        [string]$WorkingDirectory
    )

    $start = Get-Date
    if ($WorkingDirectory) {
        Push-Location $WorkingDirectory
    }

    try {
        & $File @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($WorkingDirectory) {
            Pop-Location
        }
    }

    $elapsed = (Get-Date) - $start
    Write-Output ("  {0} {1} (exit {2}, {3:n1}s)" -f $File, ($Arguments -join ' '), $exitCode, $elapsed.TotalSeconds)
    if ($exitCode -ne 0) {
        throw "Command '$File $($Arguments -join ' ')' failed with exit code $exitCode."
    }
}

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-ArchiveEntryHash {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $matches = @($archive.Entries | Where-Object { $_.FullName.Replace('\\', '/') -ceq $EntryName })
        Assert-Contract ($matches.Count -eq 1) "Archive '$ArchivePath' must contain exactly one '$EntryName' entry."

        $stream = $matches[0].Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try {
            return (($hash.ComputeHash($stream) | ForEach-Object ToString x2) -join '')
        }
        finally {
            $hash.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Remove-TemporaryDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
}

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/KeelMatrix.ResilienceSpec/KeelMatrix.ResilienceSpec.csproj'
$smokeProject = Join-Path $repo 'tests/PackageSmoke/PackageSmoke.csproj'
$consumerSpoofProject = Join-Path $repo 'tests/PackageSmoke/ConsumerSpoof/ConsumerSpoof.csproj'
$inspectionScript = Join-Path $PSScriptRoot 'Inspect-Package.ps1'
$normalizationScript = Join-Path $PSScriptRoot 'Normalize-PackageArchive.ps1'
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = Join-Path $repo 'artifacts/packages'
}

$nupkgName = "KeelMatrix.ResilienceSpec.$PackageVersion.nupkg"
$snupkgName = "KeelMatrix.ResilienceSpec.$PackageVersion.snupkg"
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) "resiliencespec-package-smoke-$([Guid]::NewGuid().ToString('N'))"
$packageFeed = Join-Path $ArtifactsDirectory 'feed'
$reproducibilityRoot = Join-Path $smokeRoot 'reproducibility'
$firstPack = Join-Path $reproducibilityRoot 'first'
$secondPack = Join-Path $reproducibilityRoot 'second'
$packPackages = Join-Path $smokeRoot 'pack-packages'
$consumerPackages = Join-Path $smokeRoot 'packages'
$nugetConfig = Join-Path $smokeRoot 'NuGet.config'
$httpCache = Join-Path $smokeRoot 'http-cache'
$scratch = Join-Path $smokeRoot 'scratch'
$pluginsCache = Join-Path $smokeRoot 'plugins'
$dotnetHome = Join-Path $smokeRoot 'dotnet-home'
$consumerSpoofOutput = Join-Path $smokeRoot 'consumer-spoof'
$smokeLog = Join-Path $ArtifactsDirectory 'package-smoke.log'
$savedEnvironment = @{}

try {
    New-Item -ItemType Directory -Path $packageFeed, $packPackages, $consumerPackages, $httpCache, $scratch, $pluginsCache, $dotnetHome, $firstPack, $secondPack, $consumerSpoofOutput -Force | Out-Null

    $consumerSpoofPublicKey = Join-Path $consumerSpoofOutput 'MicrosoftPublic.snk'
    [IO.File]::WriteAllBytes(
        $consumerSpoofPublicKey,
        [Convert]::FromHexString(
            '0024000004800000940000000602000000240000525341310004000001000100' +
            'B5FC90E7027F67871E773A8FDE8938C81DD402BA65B9201D60593E96C492651E' +
            '889CC13F1415EBB53FAC1131AE0BD333C5EE6021672D9718EA31A8AEBD0DA007' +
            '2F25D87DBA6FC90FFD598ED4DA35E44C398C454307E8E33B8426143DAEC9F596' +
            '836F97C8F74750E5975C64E2189F45DEF46B2A2B1247ADC3652BF5C308055DA9'))

    $expectedCommit = $ExpectedRepositoryCommit
    if ([string]::IsNullOrWhiteSpace($expectedCommit)) {
        $expectedCommit = (& git -C $repo rev-parse HEAD 2>&1 | Out-String).Trim()
        Assert-Contract ($LASTEXITCODE -eq 0) 'Unable to resolve the repository commit.'
    }

    $globalJson = Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json
    $expectedSdkVersion = [string]$globalJson.sdk.version
    $rollForward = [string]$globalJson.sdk.rollForward
    Push-Location $repo
    try {
        $selectedSdkVersion = (& dotnet --version 2>&1 | Out-String).Trim()
        $sdkExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    Assert-Contract ($sdkExitCode -eq 0 -and $selectedSdkVersion -match '^\d+\.\d+\.\d+$') 'Unable to resolve the selected .NET SDK version.'
    Assert-Contract ($rollForward -ceq 'disable') 'global.json must disable SDK roll-forward for package identity evidence.'
    Assert-Contract ($selectedSdkVersion -ceq $expectedSdkVersion) "The selected .NET SDK '$selectedSdkVersion' does not match pinned global.json SDK '$expectedSdkVersion'."
    Write-Output "Package identity toolchain: .NET SDK $selectedSdkVersion; global.json rollForward=$rollForward"

    foreach ($name in @(
            'NUGET_PACKAGES',
            'NUGET_HTTP_CACHE_PATH',
            'NUGET_SCRATCH',
            'NUGET_PLUGINS_CACHE_PATH',
            'DOTNET_CLI_HOME',
            'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
            'DOTNET_CLI_TELEMETRY_OPTOUT',
            'DOTNET_NOLOGO',
            'KEELMATRIX_NO_TELEMETRY',
            'RESILIENCE_SPEC_CONSUMER_SPOOF_PATH')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    [Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1', 'Process')
    [Environment]::SetEnvironmentVariable('KEELMATRIX_NO_TELEMETRY', '1', 'Process')

    Write-Output 'Build the isolated consumer spoof fixture'
    Invoke-Checked 'dotnet' @(
        'build', $consumerSpoofProject, '-c', 'Release',
        "-p:ConsumerSpoofPublicKeyFile=$consumerSpoofPublicKey",
        '-p:NuGetAudit=false', '-o', $consumerSpoofOutput)

    $packArguments = @(
        'pack', $project, '-c', 'Release',
        "--include-symbols", '-p:SymbolPackageFormat=snupkg',
        "-p:PackageVersion=$PackageVersion",
        "-p:SourceRevisionId=$expectedCommit",
        "-p:RepositoryCommit=$expectedCommit",
        '-p:RepositoryBranch=refs/heads/main',
        '-p:RepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec',
        '-p:PrivateRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec',
        '-p:ScmRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec',
        '-p:GitRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec.git',
        '-p:GitRepositoryRemoteName=origin',
        '-p:PublishRepositoryUrl=true',
        '-p:NuGetAudit=false')

    Write-Output 'Pack the shipping project twice for reproducibility'
    [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $packPackages, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_HTTP_CACHE_PATH', $httpCache, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_SCRATCH', $scratch, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_PLUGINS_CACHE_PATH', $pluginsCache, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $dotnetHome, 'Process')
    Invoke-Checked 'dotnet' ($packArguments + @('-o', $firstPack))
    Invoke-Checked 'dotnet' ($packArguments + @('-o', $secondPack))

    foreach ($packDirectory in @($firstPack, $secondPack)) {
        foreach ($archiveName in @($nupkgName, $snupkgName)) {
            Invoke-Checked 'pwsh' @('-NoProfile', '-File', $normalizationScript, '-PackagePath', (Join-Path $packDirectory $archiveName))
        }
    }

    $firstPackage = Join-Path $firstPack $nupkgName
    $secondPackage = Join-Path $secondPack $nupkgName
    $firstSymbols = Join-Path $firstPack $snupkgName
    $secondSymbols = Join-Path $secondPack $snupkgName
    $packageHash = (Get-FileHash -LiteralPath $firstPackage -Algorithm SHA256).Hash
    $secondPackageHash = (Get-FileHash -LiteralPath $secondPackage -Algorithm SHA256).Hash
    $symbolsHash = (Get-FileHash -LiteralPath $firstSymbols -Algorithm SHA256).Hash
    $secondSymbolsHash = (Get-FileHash -LiteralPath $secondSymbols -Algorithm SHA256).Hash
    Write-Output "Reproducible package SHA256: $packageHash / $secondPackageHash"
    Write-Output "Reproducible symbols SHA256: $symbolsHash / $secondSymbolsHash"
    Assert-Contract ($packageHash -ceq $secondPackageHash) 'Two normalized package archives differ.'
    Assert-Contract ($symbolsHash -ceq $secondSymbolsHash) 'Two normalized symbol archives differ.'

    $firstPackageNuspecHash = Get-ArchiveEntryHash -ArchivePath $firstPackage -EntryName 'KeelMatrix.ResilienceSpec.nuspec'
    $secondPackageNuspecHash = Get-ArchiveEntryHash -ArchivePath $secondPackage -EntryName 'KeelMatrix.ResilienceSpec.nuspec'
    $firstSymbolsNuspecHash = Get-ArchiveEntryHash -ArchivePath $firstSymbols -EntryName 'KeelMatrix.ResilienceSpec.nuspec'
    $secondSymbolsNuspecHash = Get-ArchiveEntryHash -ArchivePath $secondSymbols -EntryName 'KeelMatrix.ResilienceSpec.nuspec'
    Write-Output "Reproducible package nuspec SHA256: $firstPackageNuspecHash / $secondPackageNuspecHash"
    Write-Output "Reproducible symbols nuspec SHA256: $firstSymbolsNuspecHash / $secondSymbolsNuspecHash"
    Assert-Contract ($firstPackageNuspecHash -ceq $secondPackageNuspecHash) 'Two normalized package nuspec entries differ.'
    Assert-Contract ($firstSymbolsNuspecHash -ceq $secondSymbolsNuspecHash) 'Two normalized symbol nuspec entries differ.'

    Copy-Item -LiteralPath $firstPackage -Destination (Join-Path $packageFeed $nupkgName) -Force
    Copy-Item -LiteralPath $firstSymbols -Destination (Join-Path $packageFeed $snupkgName) -Force

    $nupkg = Join-Path $packageFeed $nupkgName
    $snupkg = Join-Path $packageFeed $snupkgName
    Assert-Contract (Test-Path -LiteralPath $nupkg -PathType Leaf) "Packed package was not found: $nupkg"
    Assert-Contract (Test-Path -LiteralPath $snupkg -PathType Leaf) "Packed symbol package was not found: $snupkg"
    Write-Output ("Package: {0} ({1:n0} bytes)" -f $nupkg, (Get-Item -LiteralPath $nupkg).Length)
    Write-Output ("Symbols: {0} ({1:n0} bytes)" -f $snupkg, (Get-Item -LiteralPath $snupkg).Length)

    Write-Output 'Inspect the packed package'
    $inspectionArguments = @('-NoProfile', '-File', $inspectionScript, '-PackagePath', $nupkg, '-SymbolsPath', $snupkg, '-ExpectedVersion', $PackageVersion, '-ExpectedRepositoryCommit', $expectedCommit)
    if ($IsWindows) {
        # Keep child PowerShell windows hidden on Windows; pwsh does not implement
        # -WindowStyle on Unix-like hosts.
        $inspectionArguments = @('-NoProfile', '-WindowStyle', 'Hidden', '-File', $inspectionScript, '-PackagePath', $nupkg, '-SymbolsPath', $snupkg, '-ExpectedVersion', $PackageVersion, '-ExpectedRepositoryCommit', $expectedCommit)
    }
    & pwsh @inspectionArguments
    Assert-Contract ($LASTEXITCODE -eq 0) 'Package inspection failed.'

    $escapedFeed = [Security.SecurityElement]::Escape($packageFeed)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate">
      <package pattern="KeelMatrix.ResilienceSpec" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="Polly*" />
      <package pattern="System.*" />
      <package pattern="KeelMatrix.Telemetry" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8

    Write-Output 'Restore the clean consumer from the local feed only'
    # The pack and consumer restores use isolated package caches so stale global state or a project reference
    # cannot make the smoke pass accidentally.
    [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $consumerPackages, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_HTTP_CACHE_PATH', $httpCache, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_SCRATCH', $scratch, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_PLUGINS_CACHE_PATH', $pluginsCache, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $dotnetHome, 'Process')
    [Environment]::SetEnvironmentVariable('RESILIENCE_SPEC_CONSUMER_SPOOF_PATH', (Join-Path $consumerSpoofOutput 'Microsoft.Extensions.TimeProvider.Testing.dll'), 'Process')

    Invoke-Checked 'dotnet' @(
        'restore', $smokeProject,
        '--configfile', $nugetConfig,
        "-p:RestorePackagesPath=$consumerPackages",
        '-p:NuGetAudit=false')

    $restoredPackage = Join-Path $consumerPackages 'keelmatrix.resiliencespec'
    Assert-Contract (Test-Path -LiteralPath $restoredPackage -PathType Container) 'The clean consumer did not restore the candidate package.'
    $restoredVersion = Get-ChildItem -LiteralPath $restoredPackage -Directory |
        Where-Object { $_.Name -eq $PackageVersion } |
        Select-Object -First 1
    Assert-Contract ($null -ne $restoredVersion) "The clean consumer did not restore version $PackageVersion."
    $restoredArtifact = Join-Path $restoredVersion.FullName $nupkgName.ToLowerInvariant()
    Assert-Contract (Test-Path -LiteralPath $restoredArtifact -PathType Leaf) 'The restored package artifact was not retained in the isolated cache.'
    Assert-Contract ((Get-FileHash -LiteralPath $restoredArtifact -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash) 'The restored package does not match the freshly packed artifact.'
    Write-Output 'Clean consumer restored the exact packed artifact from the isolated local feed.'

    Write-Output 'Run the clean consumer'
    $smokeStart = Get-Date
    $smokeOutput = (& dotnet run --project $smokeProject -c Release --no-restore "-p:RestorePackagesPath=$consumerPackages" 2>&1 | Out-String)
    $smokeExitCode = $LASTEXITCODE
    $smokeElapsed = (Get-Date) - $smokeStart
    Write-Output $smokeOutput
    $smokeOutput | Set-Content -LiteralPath $smokeLog -Encoding utf8
    Assert-Contract ($smokeExitCode -eq 0) "The clean package consumer failed with exit code $smokeExitCode."

    Write-Output ("Package smoke passed in {0:n1}s. Log: {1}" -f $smokeElapsed.TotalSeconds, $smokeLog)
    exit 0
}
catch {
    [Console]::Error.WriteLine("Package smoke failed: $($_.Exception.Message)")
    exit 1
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }

    Remove-TemporaryDirectory -Path $smokeRoot
}
