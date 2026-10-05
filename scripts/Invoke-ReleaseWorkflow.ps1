[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Verify', 'Publish')]
    [string]$Mode,

    [string]$Tag,

    [string]$PackageVersion,

    [string]$TagRevision = $env:GITHUB_SHA,

    [string]$ArtifactsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/packages'),

    [string]$NuGetSource = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/Invoke-ExternalCommand.ps1')

$repo = Split-Path -Parent $PSScriptRoot
$pwsh = 'pwsh'
$solution = Join-Path $repo 'KeelMatrix.ResilienceSpec.slnx'
$nugetConfig = Join-Path $repo 'NuGet.config'
$workflowGuard = Join-Path $repo 'build/Test-ExternalCommandWorkflow.ps1'
$ignoreContractScript = Join-Path $PSScriptRoot 'Validate-IgnoreContract.ps1'

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $false)][string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][ValidateRange(1, 3600)][int]$TimeoutSeconds
    )

    $result = Invoke-ExternalCommand -FilePath $File -ArgumentList $Arguments -WorkingDirectory $repo -TimeoutSeconds $TimeoutSeconds
    if (-not $result.Succeeded -or $result.ExitCode -ne 0) {
        $output = (@($result.Output, $result.Error) -join [Environment]::NewLine).TrimEnd()
        throw "Release gate '$File $($Arguments -join ' ')' failed closed: $($result.FailureReason)`n$output"
    }

    return $result
}

function Invoke-Script {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $false)][string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][ValidateRange(1, 3600)][int]$TimeoutSeconds
    )

    return Invoke-Checked -File $pwsh -Arguments (@('-NoProfile', '-File', $ScriptPath) + $Arguments) -TimeoutSeconds $TimeoutSeconds
}

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Release workflow contract failed: $Message"
    }
}

function Get-ReleaseVersion {
    param([Parameter(Mandatory = $true)][string]$Output)

    $match = [regex]::Match($Output, '(?m)^Release contract passed:.*\bversion=(?<version>\S+)')
    Assert-Contract $match.Success 'The release contract did not return a canonical package version.'
    return $match.Groups['version'].Value
}

function Write-ReleaseOutput {
    param([Parameter(Mandatory = $true)][string]$Version)

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        "version=$Version" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
}

try {
    Invoke-Script -ScriptPath (Join-Path $repo 'build/Test-ExternalCommandRouting.ps1') -TimeoutSeconds 60 | Out-Null
    Invoke-Script -ScriptPath $ignoreContractScript -Arguments @('-RepositoryPath', $repo) -TimeoutSeconds 60 | Out-Null

    if ($Mode -eq 'Publish') {
        Assert-Contract (-not [string]::IsNullOrWhiteSpace($PackageVersion)) 'PackageVersion is required for publication.'
        Assert-Contract (-not [string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) 'NuGet trusted-publishing output is unavailable.'

        $packageDirectory = Join-Path $repo 'artifacts/packages'
        $packagePath = Join-Path $packageDirectory "KeelMatrix.ResilienceSpec.$PackageVersion.nupkg"
        $symbolsPath = Join-Path $packageDirectory "KeelMatrix.ResilienceSpec.$PackageVersion.snupkg"
        Assert-Contract (Test-Path -LiteralPath $packagePath -PathType Leaf) "Package artifact is missing: $packagePath"
        Assert-Contract (Test-Path -LiteralPath $symbolsPath -PathType Leaf) "Symbols artifact is missing: $symbolsPath"

        Invoke-Checked -File 'dotnet' -Arguments @(
            'nuget', 'push', $packagePath, '--no-symbols',
            '--source', $NuGetSource, '--api-key', $env:NUGET_API_KEY) -TimeoutSeconds 300 | Out-Null
        Write-Output "Release package publication gate passed: $packagePath"
        Invoke-Checked -File 'dotnet' -Arguments @(
            'nuget', 'push', $symbolsPath,
            '--source', $NuGetSource, '--api-key', $env:NUGET_API_KEY) -TimeoutSeconds 300 | Out-Null
        Write-Output "Release symbol publication gate passed: $symbolsPath"
        Write-Output "Release publication gates passed: version=$PackageVersion package-and-symbol artifacts pushed through the bounded runner."
        exit 0
    }

    Assert-Contract (-not [string]::IsNullOrWhiteSpace($Tag)) 'Tag is required for verification.'
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($TagRevision)) 'TagRevision is required for verification.'

    Invoke-Script -ScriptPath $workflowGuard -TimeoutSeconds 60 | Out-Null

    Invoke-Checked -File 'git' -Arguments @('fetch', 'origin', 'main', '--force', '--prune') -TimeoutSeconds 120 | Out-Null
    $remoteMainResult = Invoke-Checked -File 'git' -Arguments @('rev-parse', 'refs/remotes/origin/main') -TimeoutSeconds 60
    $remoteMainRevision = $remoteMainResult.Output.Trim()
    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Validate-ReleaseProvenance.ps1') -Arguments @(
        '-TagRevision', $TagRevision, '-RemoteMainRevision', $remoteMainRevision) -TimeoutSeconds 60 | Out-Null

    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Validate-History.ps1') -Arguments @(
        '-Revision', 'HEAD', '-RepositoryPath', $repo) -TimeoutSeconds 60 | Out-Null
    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Validate-DocumentationHygiene.ps1') -Arguments @(
        '-RepositoryPath', $repo) -TimeoutSeconds 120 | Out-Null

    $contractResult = Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Validate-ReleaseContract.ps1') -Arguments @(
        '-Tag', $Tag,
        '-PackageProject', (Join-Path $repo 'src/KeelMatrix.ResilienceSpec/KeelMatrix.ResilienceSpec.csproj'),
        '-ChangelogPath', (Join-Path $repo 'CHANGELOG.md')) -TimeoutSeconds 180
    $version = Get-ReleaseVersion -Output $contractResult.Output
    Assert-Contract ([string]::IsNullOrWhiteSpace($PackageVersion) -or $PackageVersion -ceq $version) 'The requested package version disagrees with the release contract.'

    $common = @('--configfile', $nugetConfig, '--no-cache', '--force', '-p:NuGetAudit=false')
    Invoke-Checked -File 'dotnet' -Arguments (@('restore', $solution) + $common) -TimeoutSeconds 300 | Out-Null
    Invoke-Checked -File 'dotnet' -Arguments @('format', $solution, '--verify-no-changes', '--no-restore', '--verbosity', 'quiet') -TimeoutSeconds 300 | Out-Null
    Invoke-Checked -File 'dotnet' -Arguments @(
        'build', $solution, '--configuration', 'Release', '--no-restore',
        "-p:Version=$version", "-p:SourceRevisionId=$TagRevision", "-p:RepositoryCommit=$TagRevision",
        '-p:RepositoryBranch=refs/heads/main', '-p:RepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec',
        '-p:PrivateRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec', '-p:ScmRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec',
        '-p:GitRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec.git', '-p:GitRepositoryRemoteName=origin',
        '-p:PublishRepositoryUrl=true', '-p:NuGetAudit=false') -TimeoutSeconds 600 | Out-Null
    Invoke-Checked -File 'dotnet' -Arguments @(
        'test', (Join-Path $repo 'tests/KeelMatrix.ResilienceSpec.Tests/KeelMatrix.ResilienceSpec.Tests.csproj'),
        '--configuration', 'Release', '--no-build', '--no-restore', "-p:Version=$version", '-p:NuGetAudit=false') -TimeoutSeconds 1200 | Out-Null

    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Invoke-IntegrationTests.ps1') -Arguments @('-ResilienceVersion', '9.8.0') -TimeoutSeconds 600 | Out-Null
    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Invoke-IntegrationTests.ps1') -Arguments @('-ResilienceVersion', '10.10.0') -TimeoutSeconds 600 | Out-Null
    $feedDirectory = Join-Path $ArtifactsDirectory 'feed'
    if (Test-Path -LiteralPath $feedDirectory -PathType Container) {
        Remove-Item -LiteralPath $feedDirectory -Recurse -Force
    }
    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Invoke-PackageSmoke.ps1') -Arguments @(
        '-ExpectedRepositoryCommit', $TagRevision, '-PackageVersion', $version, '-ArtifactsDirectory', $ArtifactsDirectory) -TimeoutSeconds 1200 | Out-Null
    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Run-Sample.ps1') -Arguments @(
        '-ExpectedRepositoryCommit', $TagRevision, '-PackageVersion', $version) -TimeoutSeconds 600 | Out-Null
    Invoke-Script -ScriptPath (Join-Path $PSScriptRoot 'Invoke-DependencyAudit.ps1') -Arguments @('-Mode', 'Required') -TimeoutSeconds 300 | Out-Null

    $expectedArtifacts = @(
        "KeelMatrix.ResilienceSpec.$version.nupkg",
        "KeelMatrix.ResilienceSpec.$version.snupkg") | Sort-Object
    $actualArtifacts = @(Get-ChildItem -LiteralPath $feedDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
    Assert-Contract (($actualArtifacts -join "`n") -ceq ($expectedArtifacts -join "`n")) "Unexpected release artifact set. Expected: $($expectedArtifacts -join ', '). Actual: $($actualArtifacts -join ', ')."
    foreach ($artifact in Get-ChildItem -LiteralPath $feedDirectory -File) {
        Copy-Item -LiteralPath $artifact.FullName -Destination $ArtifactsDirectory -Force
    }
    Write-ReleaseOutput -Version $version
    Write-Output "Release verification gates passed: version=$version package/inspection/consumer evidence is in $feedDirectory."
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
