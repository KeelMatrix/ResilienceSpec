[CmdletBinding()]
param(
    [string]$Revision = 'HEAD',

    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/Invoke-ExternalCommand.ps1')

function Fail-History {
    param([Parameter(Mandatory = $true)][string]$Message)

    throw "History contract failed: $Message"
}

function Invoke-GitOutput {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $result = Invoke-ExternalCommand -FilePath 'git' -ArgumentList (@('-C', $RepositoryPath) + $Arguments) -WorkingDirectory $RepositoryPath -TimeoutSeconds 60
    if (-not $result.Succeeded) {
        Fail-History "Git command failed closed: git -C '$RepositoryPath' $($Arguments -join ' ')`n$($result.FailureReason)`n$($result.Output)`n$($result.Error)"
    }

    return $result.Output
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    return @((Invoke-GitOutput -Arguments $Arguments) -split "`r?`n")
}

function Get-CommitData {
    param([Parameter(Mandatory = $true)][string]$Revision)

    $format = '%H%x00%an%x00%ae%x00%cn%x00%ce%x00%B%x00'
    $serialized = Invoke-GitOutput -Arguments @('log', '--topo-order', "--format=$format", $Revision)
    $fields = $serialized -split ([char]0)
    $commits = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index + 5 -lt $fields.Count; $index += 6) {
        $commit = $fields[$index]
        if ([string]::IsNullOrWhiteSpace($commit)) {
            continue
        }

        [void]$commits.Add([pscustomobject]@{
            Commit = $commit
            AuthorName = $fields[$index + 1]
            AuthorEmail = $fields[$index + 2]
            CommitterName = $fields[$index + 3]
            CommitterEmail = $fields[$index + 4]
            Message = $fields[$index + 5]
        })
    }

    return $commits.ToArray()
}

function Test-KeelMatrixAuthor {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Email
    )

    return $Name -ceq 'KeelMatrix' -and $Email -ceq 'keelmatrix@gmail.com'
}

function Test-DependabotAuthor {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Email
    )

    return $Name -ceq 'dependabot[bot]' -and $Email -match '@users\.noreply\.github\.com$'
}

function Convert-CodePoints {
    param([Parameter(Mandatory = $true)][int[]]$Codes)

    return -join ($Codes | ForEach-Object { [char]$_ })
}

$runtimeTerms = @(
    (Convert-CodePoints @(112, 97, 112, 101, 114, 99, 108, 105, 112)),
    (Convert-CodePoints @(99, 111, 100, 101, 120)),
    (Convert-CodePoints @(100, 101, 101, 112, 115, 101, 101, 107)),
    (Convert-CodePoints @(99, 104, 97, 116, 103, 112, 116)),
    (Convert-CodePoints @(99, 108, 97, 117, 100, 101)),
    (Convert-CodePoints @(103, 112, 116)),
    (Convert-CodePoints @(115, 111, 108)),
    (Convert-CodePoints @(108, 117, 110, 97)),
    ((Convert-CodePoints @(100, 115)) + ' ' + (Convert-CodePoints @(102, 108, 97, 115, 104)))
)
$runtimePattern = '(?i)\b(?:' + (($runtimeTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
$headerTerms = @(
    (Convert-CodePoints @(97, 103, 101, 110, 116)),
    (Convert-CodePoints @(109, 111, 100, 101, 108))
)
$headerPattern = '(?im)^\s*(?:' + (($headerTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?:\s+name)?\s*[:=]'
$trailerTerms = @(
    (Convert-CodePoints @(99, 111, 45, 97, 117, 116, 104, 111, 114, 101, 100, 45, 98, 121)),
    (Convert-CodePoints @(115, 105, 103, 110, 101, 100, 45, 111, 102, 102, 45, 98, 121)),
    (Convert-CodePoints @(114, 101, 118, 105, 101, 119, 101, 100, 45, 98, 121)),
    (Convert-CodePoints @(103, 101, 110, 101, 114, 97, 116, 101, 100, 45, 98, 121)),
    (Convert-CodePoints @(97, 105, 45, 97, 115, 115, 105, 115, 116, 101, 100, 45, 98, 121))
)
$trailerPattern = '(?im)^\s*(?:' + (($trailerTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\s*:\s*\S+'

try {
    if (-not (Test-Path -LiteralPath $RepositoryPath -PathType Container)) {
        Fail-History "Repository path was not found: $RepositoryPath"
    }

    [void](Invoke-GitText -Arguments @('rev-parse', '--verify', "$Revision^{commit}"))
    $commits = @(Get-CommitData -Revision $Revision)
    if ($commits.Count -eq 0) {
        Fail-History "Revision '$Revision' did not resolve to any reachable commit."
    }

    $taskIdPattern = '(?<![A-Za-z0-9])[A-Z]{2,}-\d+(?!\d)'
    $provenancePatterns = @(
        @{ Pattern = $runtimePattern; Label = 'non-product runtime name' },
        @{ Pattern = '(?i)\b' + (Convert-CodePoints @(103, 101, 110, 101, 114, 97, 116, 101, 100)) + '\s+' + (Convert-CodePoints @(98, 121)) + '\s+(?:an?\s+)?' + (Convert-CodePoints @(97, 103, 101, 110, 116)) + '\b'; Label = 'generated provenance' },
        @{ Pattern = $headerPattern; Label = 'header provenance' }
    )

    foreach ($commitId in $commits) {
        $commit = $commitId
        $keelMatrixAuthor = Test-KeelMatrixAuthor -Name $commit.AuthorName -Email $commit.AuthorEmail
        $dependabotAuthor = Test-DependabotAuthor -Name $commit.AuthorName -Email $commit.AuthorEmail
        if (-not $keelMatrixAuthor -and -not $dependabotAuthor) {
            Fail-History "Commit $($commit.Commit) has non-conforming author '$($commit.AuthorName) <$($commit.AuthorEmail)>'."
        }

        $keelMatrixCommitter = Test-KeelMatrixAuthor -Name $commit.CommitterName -Email $commit.CommitterEmail
        $githubWebCommitter = $commit.CommitterName -ceq 'GitHub' -and $commit.CommitterEmail -ceq 'noreply@github.com'
        $dependabotCommitter = Test-DependabotAuthor -Name $commit.CommitterName -Email $commit.CommitterEmail
        if (-not $keelMatrixCommitter -and -not $dependabotCommitter -and -not ($keelMatrixAuthor -and $githubWebCommitter)) {
            Fail-History "Commit $($commit.Commit) has non-conforming committer '$($commit.CommitterName) <$($commit.CommitterEmail)>'."
        }

        $taskMatch = [regex]::Match($commit.Message, $taskIdPattern)
        if ($taskMatch.Success) {
            Fail-History "Commit $($commit.Commit) contains a non-product identifier ('$($taskMatch.Value)')."
        }

        foreach ($metadataPattern in $provenancePatterns) {
            if ($commit.Message -match $metadataPattern.Pattern) {
                Fail-History "Commit $($commit.Commit) contains non-product $($metadataPattern.Label)."
            }
        }

        if ($commit.Message -match $trailerPattern) {
            Fail-History "Commit $($commit.Commit) contains a non-product message trailer."
        }
    }

    Write-Output "History contract passed: revision=$Revision commits=$($commits.Count)"
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
