[CmdletBinding()]
param(
    [string]$Revision = 'HEAD',

    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail-History {
    param([Parameter(Mandatory = $true)][string]$Message)

    throw "History contract failed: $Message"
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = @(& git -C $RepositoryPath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Fail-History "Git command failed: git -C '$RepositoryPath' $($Arguments -join ' ')`n$($output -join "`n")"
    }

    return $output
}

function Get-CommitData {
    param([Parameter(Mandatory = $true)][string]$Commit)

    $lines = @(Invoke-GitText -Arguments @('cat-file', 'commit', $Commit))
    $separatorIndex = [Array]::IndexOf($lines, '')
    if ($separatorIndex -lt 0) {
        Fail-History "Commit $Commit has no header/message separator."
    }

    $headers = @($lines[0..($separatorIndex - 1)])
    $message = if ($separatorIndex + 1 -lt $lines.Count) {
        ($lines[($separatorIndex + 1)..($lines.Count - 1)] -join "`n")
    }
    else {
        ''
    }

    $authorLine = $headers | Where-Object { $_ -like 'author *' } | Select-Object -First 1
    $committerLine = $headers | Where-Object { $_ -like 'committer *' } | Select-Object -First 1
    $identityPattern = '^(?:author|committer) (?<name>.+) <(?<email>[^>]+)> \d+ [+-]\d{4}$'
    $author = [regex]::Match($authorLine, $identityPattern)
    $committer = [regex]::Match($committerLine, $identityPattern)
    if (-not $author.Success -or -not $committer.Success) {
        Fail-History "Commit $Commit has an invalid author or committer header."
    }

    return [pscustomobject]@{
        Commit = $Commit
        AuthorName = $author.Groups['name'].Value
        AuthorEmail = $author.Groups['email'].Value
        CommitterName = $committer.Groups['name'].Value
        CommitterEmail = $committer.Groups['email'].Value
        Message = $message
    }
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

try {
    if (-not (Test-Path -LiteralPath $RepositoryPath -PathType Container)) {
        Fail-History "Repository path was not found: $RepositoryPath"
    }

    [void](Invoke-GitText -Arguments @('rev-parse', '--verify', "$Revision^{commit}"))
    $commits = @(Invoke-GitText -Arguments @('rev-list', '--topo-order', $Revision) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($commits.Count -eq 0) {
        Fail-History "Revision '$Revision' did not resolve to any reachable commit."
    }

    $taskIdPattern = '(?<![A-Za-z0-9])[A-Z]{2,}-\d+(?!\d)'
    $prohibitedMetadataPatterns = @(
        @{ Pattern = '(?i)\bpaperclip\b'; Label = 'Paperclip metadata' },
        @{ Pattern = '(?i)\b(?:codex|deepseek|chatgpt|claude|gpt(?:-\d+(?:\.\d+)?)?|sol|luna|ds\s+flash)\b'; Label = 'model or agent name' },
        @{ Pattern = '(?i)\bgenerated\s+by\s+(?:an?\s+)?agent\b'; Label = 'generated-by-agent metadata' },
        @{ Pattern = '(?im)^\s*(?:agent|model)(?:\s+name)?\s*[:=]'; Label = 'agent/model metadata' }
    )
    $prohibitedTrailerPattern = '(?im)^\s*(?:co-authored-by|signed-off-by|reviewed-by|generated-by|ai-assisted-by)\s*:\s*\S+'

    foreach ($commitId in $commits) {
        $commit = Get-CommitData -Commit $commitId.Trim()
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
            Fail-History "Commit $($commit.Commit) contains an internal task identifier ('$($taskMatch.Value)')."
        }

        foreach ($metadataPattern in $prohibitedMetadataPatterns) {
            if ($commit.Message -match $metadataPattern.Pattern) {
                Fail-History "Commit $($commit.Commit) contains prohibited $($metadataPattern.Label)."
            }
        }

        if ($commit.Message -match $prohibitedTrailerPattern) {
            Fail-History "Commit $($commit.Commit) contains a prohibited attribution or co-author trailer."
        }
    }

    Write-Output "History contract passed: revision=$Revision commits=$($commits.Count)"
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
