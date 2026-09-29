[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Streams', 'StdoutOnly', 'StderrOnly', 'NonZero', 'Timeout', 'Descendant')]
    [string]$Scenario,

    [int]$TimeoutSeconds = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../../build/Invoke-ExternalCommand.ps1')

$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workingDirectory = $repository
$pwsh = (Get-Command ([string]::Join('', @('p', 'w', 's', 'h'))) -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$arguments = @('-NoProfile', '-Command')

switch ($Scenario) {
    'Streams' {
        $arguments += '[Console]::WriteLine("stdout-marker"); [Console]::Error.WriteLine("stderr-marker"); exit 0'
    }
    'StdoutOnly' {
        $arguments += '[Console]::WriteLine("stdout-marker"); exit 0'
    }
    'StderrOnly' {
        $arguments += '[Console]::Error.WriteLine("stderr-marker"); exit 0'
    }
    'NonZero' {
        $arguments += '[Console]::WriteLine("stdout-marker"); [Console]::Error.WriteLine("stderr-marker"); exit 17'
    }
    'Timeout' {
        $arguments += 'Start-Sleep -Seconds 30'
    }
    'Descendant' {
        $pwshBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pwsh))
        $arguments += @'
$pwsh = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PWSH_PATH__'))
$childStartInfo = [Diagnostics.ProcessStartInfo]::new()
$childStartInfo.FileName = $pwsh
$childStartInfo.UseShellExecute = $false
$childStartInfo.CreateNoWindow = $true
[void]$childStartInfo.ArgumentList.Add('-NoProfile')
[void]$childStartInfo.ArgumentList.Add('-Command')
[void]$childStartInfo.ArgumentList.Add('Start-Sleep -Seconds 30')
$child = [Diagnostics.Process]::Start($childStartInfo)
[Console]::WriteLine(('child=' + $child.Id))
$child.Dispose()
exit 0
'@.Replace('__PWSH_PATH__', $pwshBase64)
    }
}

$result = Invoke-ExternalCommand -FilePath $pwsh -ArgumentList $arguments -WorkingDirectory $workingDirectory -TimeoutSeconds $TimeoutSeconds
$result | ConvertTo-Json -Compress
