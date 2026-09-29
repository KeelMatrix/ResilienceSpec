function Invoke-ExternalCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $false)][string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][ValidateRange(1, 3600)][int]$TimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Unable to start '$FilePath'."
        }

        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $timeoutMilliseconds = [int64]$TimeoutSeconds * 1000
        $completed = $process.WaitForExit([int]$timeoutMilliseconds)
        $timedOut = -not $completed
        $killError = $null

        if ($timedOut) {
            try {
                $process.Kill($true)
            }
            catch {
                $killError = $_.Exception.Message
            }

            [void]$process.WaitForExit(5000)
        }

        [void]$standardOutput.Wait(5000)
        [void]$standardError.Wait(5000)

        $output = if ($standardOutput.IsCompleted) {
            $standardOutput.GetAwaiter().GetResult()
        }
        else {
            '[standard output was not fully captured within 5 seconds after process termination]'
        }
        $standardErrorText = if ($standardError.IsCompleted) {
            $standardError.GetAwaiter().GetResult()
        }
        else {
            '[standard error was not fully captured within 5 seconds after process termination]'
        }

        [pscustomobject]@{
            ExitCode = if ($completed) { $process.ExitCode } else { $null }
            TimedOut = $timedOut
            Output = $output
            Error = $standardErrorText
            KillError = if ($killError) { $killError } else { $null }
        }
    }
    finally {
        $process.Dispose()
    }
}
