[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

foreach ($name in @(
        'GIT_DIR',
        'GIT_WORK_TREE',
        'GIT_COMMON_DIR',
        'GIT_INDEX_FILE',
        'GIT_OBJECT_DIRECTORY',
        'GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}

& git -C $SourceRoot --work-tree=$SourceRoot rev-parse --verify HEAD
exit $LASTEXITCODE
