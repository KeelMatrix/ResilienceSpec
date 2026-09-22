[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8NoBom = [Text.UTF8Encoding]::new($false)
foreach ($filePath in $Path) {
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        continue
    }

    $text = [IO.File]::ReadAllText($filePath)
    $canonical = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    if ($canonical -cne $text) {
        [IO.File]::WriteAllText($filePath, $canonical, $utf8NoBom)
    }
}
