[CmdletBinding()]
param(
    [string]$PackageVersion = '0.1.0',

    [string]$ExpectedRepositoryCommit
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

function Remove-TemporaryDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    }
}

$repo = Split-Path -Parent $PSScriptRoot
$packageProject = Join-Path $repo 'src/KeelMatrix.ResilienceSpec/KeelMatrix.ResilienceSpec.csproj'
$normalizationScript = Join-Path $PSScriptRoot 'Normalize-PackageArchive.ps1'
$sampleProject = Join-Path $repo 'samples/KeelMatrix.ResilienceSpec.Sample/KeelMatrix.ResilienceSpec.Sample.csproj'
$sampleRoot = Join-Path ([IO.Path]::GetTempPath()) "resiliencespec-sample-$([Guid]::NewGuid().ToString('N'))"
$packageFeed = Join-Path $sampleRoot 'feed'
$consumerPackages = Join-Path $sampleRoot 'packages'
$nugetConfig = Join-Path $sampleRoot 'NuGet.config'
$httpCache = Join-Path $sampleRoot 'http-cache'
$scratch = Join-Path $sampleRoot 'scratch'
$pluginsCache = Join-Path $sampleRoot 'plugins'
$dotnetHome = Join-Path $sampleRoot 'dotnet-home'
$nupkgName = "KeelMatrix.ResilienceSpec.$PackageVersion.nupkg"
$snupkgName = "KeelMatrix.ResilienceSpec.$PackageVersion.snupkg"
$savedEnvironment = @{}

try {
    New-Item -ItemType Directory -Path $packageFeed, $consumerPackages, $httpCache, $scratch, $pluginsCache, $dotnetHome -Force | Out-Null

    $expectedCommit = $ExpectedRepositoryCommit
    if ([string]::IsNullOrWhiteSpace($expectedCommit)) {
        $expectedCommit = (& git -C $repo rev-parse HEAD 2>&1 | Out-String).Trim()
        Assert-Contract ($LASTEXITCODE -eq 0) 'Unable to resolve the repository commit.'
    }

    foreach ($name in @(
            'NUGET_PACKAGES',
            'NUGET_HTTP_CACHE_PATH',
            'NUGET_SCRATCH',
            'NUGET_PLUGINS_CACHE_PATH',
            'DOTNET_CLI_HOME',
            'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
            'DOTNET_CLI_TELEMETRY_OPTOUT',
            'DOTNET_NOLOGO',
            'KEELMATRIX_NO_TELEMETRY')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    [Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1', 'Process')
    [Environment]::SetEnvironmentVariable('KEELMATRIX_NO_TELEMETRY', '1', 'Process')

    Write-Output 'Pack the shipping project for the sample'
    Invoke-Checked 'dotnet' @(
        'pack', $packageProject, '-c', 'Release',
        '--include-symbols', '-p:SymbolPackageFormat=snupkg',
        "-p:PackageVersion=$PackageVersion",
        "-p:SourceRevisionId=$expectedCommit",
        "-p:RepositoryCommit=$expectedCommit",
        '-p:NuGetAudit=false',
        '-o', $packageFeed)

    $nupkg = Join-Path $packageFeed $nupkgName
    $snupkg = Join-Path $packageFeed $snupkgName
    Assert-Contract (Test-Path -LiteralPath $nupkg -PathType Leaf) "Packed package was not found: $nupkg"
    Assert-Contract (Test-Path -LiteralPath $snupkg -PathType Leaf) "Packed symbol package was not found: $snupkg"
    Invoke-Checked 'pwsh' @('-NoProfile', '-File', $normalizationScript, '-PackagePath', $nupkg)
    Invoke-Checked 'pwsh' @('-NoProfile', '-File', $normalizationScript, '-PackagePath', $snupkg)
    Write-Output ("Packed artifact: {0} ({1:n0} bytes)" -f $nupkg, (Get-Item -LiteralPath $nupkg).Length)

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

    [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $consumerPackages, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_HTTP_CACHE_PATH', $httpCache, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_SCRATCH', $scratch, 'Process')
    [Environment]::SetEnvironmentVariable('NUGET_PLUGINS_CACHE_PATH', $pluginsCache, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_HOME', $dotnetHome, 'Process')

    Write-Output 'Restore the sample from the isolated package feed'
    Invoke-Checked 'dotnet' @(
        'restore', $sampleProject,
        '--configfile', $nugetConfig,
        "-p:RestorePackagesPath=$consumerPackages",
        '-p:NuGetAudit=false')

    $restoredPackage = Join-Path $consumerPackages 'keelmatrix.resiliencespec'
    Assert-Contract (Test-Path -LiteralPath $restoredPackage -PathType Container) 'The sample did not restore the candidate package.'
    $restoredVersion = Get-ChildItem -LiteralPath $restoredPackage -Directory |
        Where-Object { $_.Name -eq $PackageVersion } |
        Select-Object -First 1
    Assert-Contract ($null -ne $restoredVersion) "The sample did not restore version $PackageVersion."
    $restoredArtifact = Join-Path $restoredVersion.FullName $nupkgName.ToLowerInvariant()
    Assert-Contract (Test-Path -LiteralPath $restoredArtifact -PathType Leaf) 'The restored sample package artifact was not retained in the isolated cache.'
    Assert-Contract ((Get-FileHash -LiteralPath $restoredArtifact -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash) 'The sample did not restore the freshly packed artifact.'
    Write-Output 'Sample restored the exact packed artifact from the isolated local feed.'

    Write-Output 'Run the sample against the packed package'
    $sampleStart = Get-Date
    $sampleOutput = (& dotnet run --project $sampleProject -c Release --no-restore "-p:RestorePackagesPath=$consumerPackages" 2>&1 | Out-String)
    $sampleExitCode = $LASTEXITCODE
    $sampleElapsed = (Get-Date) - $sampleStart
    Write-Output $sampleOutput.TrimEnd()
    Assert-Contract ($sampleExitCode -eq 0) "The package-backed sample failed with exit code $sampleExitCode."
    Write-Output ("Package-backed sample passed in {0:n1}s." -f $sampleElapsed.TotalSeconds)
    exit 0
}
catch {
    [Console]::Error.WriteLine("Sample validation failed: $($_.Exception.Message)")
    exit 1
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }

    Remove-TemporaryDirectory -Path $sampleRoot
}
