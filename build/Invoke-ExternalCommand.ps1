$nativeTypeName = 'KeelMatrix.ExternalCommandNative'
if (-not ($nativeTypeName -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace KeelMatrix
{
    public sealed class WindowsExternalCommandHandles
    {
        public IntPtr ProcessHandle { get; init; }
        public IntPtr JobHandle { get; init; }
        public IntPtr StandardOutputReadHandle { get; init; }
        public IntPtr StandardErrorReadHandle { get; init; }
        public int ProcessId { get; init; }
    }

    public static class ExternalCommandNative
    {
        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectBasicAccountingInformation = 1;
        private const uint JobObjectBasicProcessIdList = 3;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const uint StartfUseStdHandles = 0x100;
        private const uint CreateSuspended = 0x4;
        private const uint CreateNoWindow = 0x8000000;
        private const uint CreateUnicodeEnvironment = 0x400;
        private const uint HandleFlagInherit = 1;
        private const uint WaitObject0 = 0;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint StillActive = 259;

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Cb;
            public string Reserved;
            public string Desktop;
            public string Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public uint Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations;
            public ulong WriteOperations;
            public ulong OtherOperations;
            public ulong ReadBytes;
            public ulong WriteBytes;
            public ulong OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, uint informationClass, IntPtr information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, uint informationClass, IntPtr information, uint length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr readHandle, out IntPtr writeHandle, ref SecurityAttributes attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        public static WindowsExternalCommandHandles StartWindows(string filePath, string[] arguments, string workingDirectory)
        {
            IntPtr job = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            IntPtr errorRead = IntPtr.Zero;
            IntPtr errorWrite = IntPtr.Zero;
            ProcessInformation processInformation = default;

            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                ThrowIfZero(job, "CreateJobObject");

                var limits = new ExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                var limitsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimitInformation>());
                try
                {
                    Marshal.StructureToPtr(limits, limitsPointer, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, limitsPointer, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                    {
                        ThrowLastError("SetInformationJobObject");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(limitsPointer);
                }

                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    InheritHandle = 1,
                };
                if (!CreatePipe(out outputRead, out outputWrite, ref attributes, 0))
                {
                    ThrowLastError("CreatePipe(stdout)");
                }

                if (!CreatePipe(out errorRead, out errorWrite, ref attributes, 0))
                {
                    ThrowLastError("CreatePipe(stderr)");
                }

                if (!SetHandleInformation(outputRead, HandleFlagInherit, 0))
                {
                    ThrowLastError("SetHandleInformation(stdout)");
                }

                if (!SetHandleInformation(errorRead, HandleFlagInherit, 0))
                {
                    ThrowLastError("SetHandleInformation(stderr)");
                }

                var startupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfo>(),
                    Flags = StartfUseStdHandles,
                    StandardOutput = outputWrite,
                    StandardError = errorWrite,
                };
                var commandLine = new StringBuilder(Quote(filePath));
                foreach (var argument in arguments)
                {
                    commandLine.Append(' ');
                    commandLine.Append(Quote(argument));
                }

                var flags = CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment;
                if (!CreateProcess(filePath, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, workingDirectory, ref startupInfo, out processInformation))
                {
                    ThrowLastError("CreateProcess");
                }

                CloseHandle(outputWrite);
                outputWrite = IntPtr.Zero;
                CloseHandle(errorWrite);
                errorWrite = IntPtr.Zero;

                if (!AssignProcessToJobObject(job, processInformation.Process))
                {
                    ThrowLastError("AssignProcessToJobObject");
                }

                if (ResumeThread(processInformation.Thread) == uint.MaxValue)
                {
                    ThrowLastError("ResumeThread");
                }

                CloseHandle(processInformation.Thread);
                processInformation.Thread = IntPtr.Zero;
                return new WindowsExternalCommandHandles
                {
                    ProcessHandle = processInformation.Process,
                    JobHandle = job,
                    StandardOutputReadHandle = outputRead,
                    StandardErrorReadHandle = errorRead,
                    ProcessId = processInformation.ProcessId,
                };
            }
            catch
            {
                if (processInformation.Process != IntPtr.Zero)
                {
                    TerminateJobObject(job, 1);
                    CloseHandle(processInformation.Process);
                }

                CloseHandle(processInformation.Thread);
                CloseHandle(outputRead);
                CloseHandle(outputWrite);
                CloseHandle(errorRead);
                CloseHandle(errorWrite);
                CloseHandle(job);
                throw;
            }
        }

        public static bool WaitForExit(IntPtr process, int milliseconds) => WaitForSingleObject(process, (uint)milliseconds) == WaitObject0;

        public static int GetExitCode(IntPtr process)
        {
            if (!GetExitCodeProcess(process, out var exitCode))
            {
                ThrowLastError("GetExitCodeProcess");
            }

            return unchecked((int)exitCode);
        }

        public static int GetActiveProcessCount(IntPtr job)
        {
            var size = Marshal.SizeOf<BasicAccountingInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, pointer, (uint)size, IntPtr.Zero))
                {
                    ThrowLastError("QueryInformationJobObject");
                }

                return checked((int)Marshal.PtrToStructure<BasicAccountingInformation>(pointer).ActiveProcesses);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        public static int GetDescendantProcessCount(IntPtr job, int rootProcessId)
        {
            var size = 8 + (IntPtr.Size * 1024);
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, pointer, (uint)size, IntPtr.Zero))
                {
                    ThrowLastError("QueryInformationJobObject(process list)");
                }

                var assigned = Marshal.ReadInt32(pointer, 0);
                var count = Marshal.ReadInt32(pointer, 4);
                if (assigned < 0 || count < 0 || assigned > 1024 || count > 1024 || count > assigned)
                {
                    throw new InvalidOperationException("The job process list exceeded the bounded inspection buffer.");
                }

                var descendants = 0;
                for (var index = 0; index < count; index++)
                {
                    var processId = IntPtr.Size == 8
                        ? unchecked((int)Marshal.ReadInt64(pointer, 8 + (index * IntPtr.Size)))
                        : Marshal.ReadInt32(pointer, 8 + (index * IntPtr.Size));
                    if (processId != rootProcessId)
                    {
                        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
                        if (process == IntPtr.Zero)
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (error == 87)
                            {
                                continue;
                            }

                            throw new Win32Exception(error, "OpenProcess failed while checking descendants.");
                        }

                        try
                        {
                            if (!GetExitCodeProcess(process, out var exitCode))
                            {
                                ThrowLastError("GetExitCodeProcess(descendant)");
                            }

                            if (exitCode == StillActive)
                            {
                                descendants++;
                            }
                        }
                        finally
                        {
                            CloseHandle(process);
                        }
                    }
                }

                return descendants;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        public static bool TerminateJob(IntPtr job, uint exitCode) => TerminateJobObject(job, exitCode);

        public static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }

        private static void ThrowIfZero(IntPtr handle, string operation)
        {
            if (handle == IntPtr.Zero)
            {
                ThrowLastError(operation);
            }
        }

        private static void ThrowLastError(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");

        private static string Quote(string value)
        {
            var builder = new StringBuilder();
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }
    }

    public static class UnixExternalCommandNative
    {
        private const int NoSuchProcess = 3;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int processId, int signal);

        public static int ProbeProcessGroup(int processGroupId)
        {
            if (kill(-processGroupId, 0) == 0)
            {
                return 1;
            }

            var error = Marshal.GetLastWin32Error();
            return error == NoSuchProcess ? 0 : -error;
        }

        public static int KillProcessGroup(int processGroupId)
        {
            if (kill(-processGroupId, 9) == 0)
            {
                return 1;
            }

            var error = Marshal.GetLastWin32Error();
            return error == NoSuchProcess ? 0 : -error;
        }
    }
}
'@ -Language CSharp
}

function New-ExternalCommandResult {
    param(
        [Nullable[int]]$ExitCode,
        [bool]$TimedOut,
        [string]$Output,
        [string]$Error,
        [bool]$CaptureComplete,
        [bool]$ContainmentEstablished,
        [string]$CaptureError,
        [string]$ContainmentError,
        [string]$DescendantError,
        [string]$KillError,
        [string]$StartError
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($StartError) { [void]$reasons.Add($StartError) }
    if (-not $ContainmentEstablished) { [void]$reasons.Add($ContainmentError ?? 'Process containment was not established.') }
    if ($TimedOut) { [void]$reasons.Add('The command exceeded its deadline.') }
    if ($KillError) { [void]$reasons.Add("Process-tree termination failed: $KillError") }
    if (-not $CaptureComplete) { [void]$reasons.Add($CaptureError ?? 'Standard output or error was not fully captured.') }
    if ($DescendantError) { [void]$reasons.Add($DescendantError) }
    if ($null -eq $ExitCode -and -not $StartError) { [void]$reasons.Add('The command did not produce a final exit code.') }
    if ($null -ne $ExitCode -and $ExitCode -ne 0) { [void]$reasons.Add("The command exited with code $ExitCode.") }

    [pscustomobject]@{
        ExitCode = $ExitCode
        TimedOut = $TimedOut
        Output = $Output
        Error = $Error
        CaptureComplete = $CaptureComplete
        ContainmentEstablished = $ContainmentEstablished
        DescendantsContained = [string]::IsNullOrWhiteSpace($DescendantError)
        KillError = $KillError
        CaptureError = $CaptureError
        ContainmentError = $ContainmentError
        DescendantError = $DescendantError
        StartError = $StartError
        FailureReason = ($reasons -join ' ')
        Succeeded = ($reasons.Count -eq 0)
    }
}

function Resolve-ExternalExecutable {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    if ([IO.Path]::IsPathRooted($FilePath)) {
        if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
            throw "Executable '$FilePath' was not found."
        }

        return (Resolve-Path -LiteralPath $FilePath).Path
    }

    try {
        $command = Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop | Select-Object -First 1
    }
    catch {
        throw "Executable '$FilePath' was not found on PATH."
    }
    if ($null -eq $command -or [string]::IsNullOrWhiteSpace($command.Source)) {
        throw "Executable '$FilePath' was not found on PATH."
    }

    return $command.Source
}

function Wait-ForUnixProcessGroupExit {
    param([Parameter(Mandatory = $true)][int]$ProcessGroupId)

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        $state = [KeelMatrix.UnixExternalCommandNative]::ProbeProcessGroup($ProcessGroupId)
        if ($state -eq 0) {
            return $true
        }

        if ($state -lt 0) {
            throw "Unable to inspect process group $ProcessGroupId (errno $(-$state))."
        }

        Start-Sleep -Milliseconds 50
    }

    return $false
}

function Invoke-ExternalCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $false)][string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][ValidateRange(1, 3600)][int]$TimeoutSeconds
    )

    $output = ''
    $errorText = ''
    $captureError = $null
    $containmentError = $null
    $descendantError = $null
    $killError = $null
    $startError = $null
    $exitCode = $null
    $timedOut = $false
    $completed = $false
    $containmentEstablished = $false
    $captureComplete = $false
    $process = $null
    $native = $null
    $stdoutReader = $null
    $stderrReader = $null
    $stdoutTask = $null
    $stderrTask = $null
    $stdoutMarkerTask = $null
    $savedBuildServerReuse = [Environment]::GetEnvironmentVariable('MSBUILDDISABLENODEREUSE', 'Process')
    $savedDotnetBuildServerDisable = [Environment]::GetEnvironmentVariable('DOTNET_CLI_DISABLE_BUILD_SERVERS', 'Process')
    [Environment]::SetEnvironmentVariable('MSBUILDDISABLENODEREUSE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_DISABLE_BUILD_SERVERS', '1', 'Process')

    try {
        if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
            throw "Working directory '$WorkingDirectory' was not found."
        }

        $resolvedFilePath = Resolve-ExternalExecutable -FilePath $FilePath
        if ($IsWindows) {
            $native = [KeelMatrix.ExternalCommandNative]::StartWindows($resolvedFilePath, $ArgumentList, $WorkingDirectory)
            $containmentEstablished = $true
            $stdoutHandle = [Microsoft.Win32.SafeHandles.SafeFileHandle]::new($native.StandardOutputReadHandle, $true)
            $stderrHandle = [Microsoft.Win32.SafeHandles.SafeFileHandle]::new($native.StandardErrorReadHandle, $true)
            $stdoutReader = [IO.StreamReader]::new([IO.FileStream]::new($stdoutHandle, [IO.FileAccess]::Read, 4096, $false), [Text.Encoding]::UTF8, $false, 4096, $true)
            $stderrReader = [IO.StreamReader]::new([IO.FileStream]::new($stderrHandle, [IO.FileAccess]::Read, 4096, $false), [Text.Encoding]::UTF8, $false, 4096, $true)
            $stdoutTask = $stdoutReader.ReadToEndAsync()
            $stderrTask = $stderrReader.ReadToEndAsync()
            $completed = [KeelMatrix.ExternalCommandNative]::WaitForExit($native.ProcessHandle, [int]([int64]$TimeoutSeconds * 1000))
            $timedOut = -not $completed
        }
        else {
            $pwshPath = Resolve-ExternalExecutable -FilePath ([string]::Join('', @('p', 'w', 's', 'h')))
            $wrapperCommand = 'Add-Type -Name ExternalCommandSession -Namespace KeelMatrix -MemberDefinition ''[DllImport("libc")] public static extern int setsid();''; if ([KeelMatrix.ExternalCommandSession]::setsid() -lt 0) { exit 125 }; [Console]::WriteLine("__KEELMATRIX_EXTERNAL_COMMAND_CONTAINED__"); if ($args.Count -gt 1) { & $args[0] @($args[1..($args.Count - 1)]) } else { & $args[0] }; exit $LASTEXITCODE'
            $startInfo = [Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = $pwshPath
            $startInfo.WorkingDirectory = $WorkingDirectory
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            [void]$startInfo.ArgumentList.Add('-NoProfile')
            [void]$startInfo.ArgumentList.Add('-Command')
            [void]$startInfo.ArgumentList.Add($wrapperCommand)
            [void]$startInfo.ArgumentList.Add($resolvedFilePath)
            foreach ($argument in $ArgumentList) {
                [void]$startInfo.ArgumentList.Add([string]$argument)
            }

            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $startInfo
            if (-not $process.Start()) {
                throw "Unable to start '$FilePath'."
            }

            $stdoutMarkerTask = $process.StandardOutput.ReadLineAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (-not $stdoutMarkerTask.Wait(5000)) {
                throw 'The Unix process-group containment marker was not published.'
            }
            if ($stdoutMarkerTask.GetAwaiter().GetResult() -cne '__KEELMATRIX_EXTERNAL_COMMAND_CONTAINED__') {
                throw 'The Unix process-group containment marker was invalid.'
            }

            $containmentEstablished = $true
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $completed = $process.WaitForExit([int]([int64]$TimeoutSeconds * 1000))
            $timedOut = -not $completed
        }

        if ($timedOut) {
            if ($IsWindows) {
                if (-not [KeelMatrix.ExternalCommandNative]::TerminateJob($native.JobHandle, 1)) {
                    $killError = 'TerminateJobObject returned failure.'
                }

                [void][KeelMatrix.ExternalCommandNative]::WaitForExit($native.ProcessHandle, 5000)
            }
            else {
                $killState = [KeelMatrix.UnixExternalCommandNative]::KillProcessGroup($process.Id)
                if ($killState -lt 0) {
                    $killError = "kill(process-group) failed with errno $(-$killState)."
                }

                if (-not $process.HasExited) {
                    [void]$process.WaitForExit(5000)
                }

                if (-not (Wait-ForUnixProcessGroupExit -ProcessGroupId $process.Id)) {
                    $killError = if ($killError) { $killError } else { 'The process group remained alive after termination.' }
                }
            }
        }
        elseif ($completed) {
            if ($IsWindows) {
                $activeProcesses = 1
                $deadline = [DateTime]::UtcNow.AddSeconds(5)
                do {
                    $activeProcesses = [KeelMatrix.ExternalCommandNative]::GetDescendantProcessCount($native.JobHandle, $native.ProcessId)
                    if ($activeProcesses -eq 0) {
                        break
                    }

                    Start-Sleep -Milliseconds 50
                } while ([DateTime]::UtcNow -lt $deadline)

                if ($activeProcesses -gt 0) {
                    $descendantError = "The command exited but its containment job still owned $activeProcesses running descendant process(es)."
                    if (-not [KeelMatrix.ExternalCommandNative]::TerminateJob($native.JobHandle, 1)) {
                        $killError = 'TerminateJobObject could not terminate surviving descendants.'
                    }
                }
            }
            else {
                if (-not (Wait-ForUnixProcessGroupExit -ProcessGroupId $process.Id)) {
                    $descendantError = 'The command exited but its process group still contained a running descendant.'
                    $killState = [KeelMatrix.UnixExternalCommandNative]::KillProcessGroup($process.Id)
                    if ($killState -lt 0) {
                        $killError = "kill(process-group) failed with errno $(-$killState)."
                    }

                    if (-not (Wait-ForUnixProcessGroupExit -ProcessGroupId $process.Id)) {
                        $killError = if ($killError) { $killError } else { 'The process group remained alive after descendant cleanup.' }
                    }
                }
            }

            if ($IsWindows) {
                $exitCode = [KeelMatrix.ExternalCommandNative]::GetExitCode($native.ProcessHandle)
            }
            else {
                $exitCode = $process.ExitCode
            }
        }
    }
    catch {
        if (-not $containmentEstablished) {
            $startError = $_.Exception.Message
        }
        elseif ($null -eq $containmentError) {
            $containmentError = $_.Exception.Message
        }

        if ($containmentEstablished -and $null -ne $native) {
            if (-not [KeelMatrix.ExternalCommandNative]::TerminateJob($native.JobHandle, 1)) {
                $killError = $_.Exception.Message
            }
        }
        elseif ($null -ne $process) {
            if ($containmentEstablished) {
                $killState = [KeelMatrix.UnixExternalCommandNative]::KillProcessGroup($process.Id)
                if ($killState -lt 0) {
                    $killError = "kill(process-group) failed with errno $(-$killState)."
                }
            }
            else {
                try { $process.Kill() } catch { $killError = "Root-process termination failed: $($_.Exception.Message)" }
            }
        }
    }
    finally {
        if ($null -ne $stdoutMarkerTask) {
            [void]$stdoutMarkerTask.Wait(5000)
        }
        if ($null -ne $stdoutTask) {
            [void]$stdoutTask.Wait(5000)
        }
        if ($null -ne $stderrTask) {
            [void]$stderrTask.Wait(5000)
        }

        if ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) {
            try { $output = $stdoutTask.GetAwaiter().GetResult() } catch { $captureError = "Standard output capture failed: $($_.Exception.Message)" }
        }
        else {
            $output = '[standard output was not fully captured within 5 seconds after process termination]'
            $captureError = 'Standard output was not fully captured after the process tree was terminated.'
        }

        if ($null -ne $stderrTask -and $stderrTask.IsCompleted) {
            try { $errorText = $stderrTask.GetAwaiter().GetResult() } catch { $captureError = if ($captureError) { "$captureError Standard error capture failed: $($_.Exception.Message)" } else { "Standard error capture failed: $($_.Exception.Message)" } }
        }
        else {
            $errorText = '[standard error was not fully captured within 5 seconds after process termination]'
            $captureError = if ($captureError) { "$captureError Standard error was not fully captured after the process tree was terminated." } else { 'Standard error was not fully captured after the process tree was terminated.' }
        }

        $captureComplete = $null -eq $captureError

        if ($null -ne $stdoutReader) { $stdoutReader.Dispose() }
        if ($null -ne $stderrReader) { $stderrReader.Dispose() }
        if ($null -ne $native) {
            [KeelMatrix.ExternalCommandNative]::Close($native.ProcessHandle)
            [KeelMatrix.ExternalCommandNative]::Close($native.JobHandle)
        }
        if ($null -ne $process) { $process.Dispose() }

        [Environment]::SetEnvironmentVariable('MSBUILDDISABLENODEREUSE', $savedBuildServerReuse, 'Process')
        [Environment]::SetEnvironmentVariable('DOTNET_CLI_DISABLE_BUILD_SERVERS', $savedDotnetBuildServerDisable, 'Process')
    }

    New-ExternalCommandResult -ExitCode $exitCode -TimedOut $timedOut -Output $output -Error $errorText `
        -CaptureComplete $captureComplete -ContainmentEstablished $containmentEstablished `
        -CaptureError $captureError -ContainmentError $containmentError -DescendantError $descendantError `
        -KillError $killError -StartError $startError
}
