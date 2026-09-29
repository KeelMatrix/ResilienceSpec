$nativeTypeName = 'KeelMatrix.ExternalCommandNative'
if (-not ($nativeTypeName -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Collections.Generic;
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

    public sealed class ExternalCommandCleanupException : Exception
    {
        public string CleanupError { get; }

        public ExternalCommandCleanupException(string message) : base(message)
        {
            CleanupError = message;
        }
    }

    public sealed class ExternalCommandStartException : Exception
    {
        public string CleanupError { get; }

        public ExternalCommandStartException(string message, string cleanupError) : base(message)
        {
            CleanupError = cleanupError;
        }
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
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        private static readonly HashSet<string> InjectedCloseFailures = new(StringComparer.OrdinalIgnoreCase);

        public static WindowsExternalCommandHandles StartWindows(string filePath, string[] arguments, string workingDirectory)
        {
            IntPtr job = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            IntPtr errorRead = IntPtr.Zero;
            IntPtr errorWrite = IntPtr.Zero;
            ProcessInformation processInformation = default;
            var processAssignedToJob = false;

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

                CloseOrThrow(outputWrite, "stdout-write");
                outputWrite = IntPtr.Zero;
                CloseOrThrow(errorWrite, "stderr-write");
                errorWrite = IntPtr.Zero;

                if (!AssignProcessToJobObject(job, processInformation.Process))
                {
                    ThrowLastError("AssignProcessToJobObject");
                }
                processAssignedToJob = true;

                if (ResumeThread(processInformation.Thread) == uint.MaxValue)
                {
                    ThrowLastError("ResumeThread");
                }

                CloseOrThrow(processInformation.Thread, "thread");
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
            catch (Exception exception)
            {
                var cleanupErrors = new List<string>();
                if (processInformation.Process != IntPtr.Zero)
                {
                    var terminated = processAssignedToJob
                        ? TerminateJobObject(job, 1)
                        : TerminateProcess(processInformation.Process, 1);
                    if (!terminated)
                    {
                        cleanupErrors.Add(processAssignedToJob
                            ? "TerminateJobObject failed while handling a startup failure."
                            : "TerminateProcess failed while handling a startup failure.");
                    }
                    AddCloseError(cleanupErrors, processInformation.Process, "process");
                }

                AddCloseError(cleanupErrors, processInformation.Thread, "thread");
                AddCloseError(cleanupErrors, outputRead, "stdout-read");
                AddCloseError(cleanupErrors, outputWrite, "stdout-write");
                AddCloseError(cleanupErrors, errorRead, "stderr-read");
                AddCloseError(cleanupErrors, errorWrite, "stderr-write");
                AddCloseError(cleanupErrors, job, "job");
                if (cleanupErrors.Count > 0)
                {
                    throw new ExternalCommandStartException(exception.Message, string.Join(" ", cleanupErrors));
                }
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
                            CloseOrThrow(process, "descendant");
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

        public static string Close(IntPtr handle, string label)
        {
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var injectedFailure = ShouldInjectCloseFailure(label);
            if (!CloseHandle(handle))
            {
                return new Win32Exception(Marshal.GetLastWin32Error(), $"CloseHandle({label}) failed.").Message;
            }

            return injectedFailure ? $"CloseHandle({label}) fault was injected." : null;
        }

        private static void CloseOrThrow(IntPtr handle, string label)
        {
            var error = Close(handle, label);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new ExternalCommandCleanupException(error);
            }
        }

        private static void AddCloseError(List<string> errors, IntPtr handle, string label)
        {
            var error = Close(handle, label);
            if (!string.IsNullOrWhiteSpace(error))
            {
                errors.Add(error);
            }
        }

        private static bool ShouldInjectCloseFailure(string label)
        {
            var configured = Environment.GetEnvironmentVariable("KEELMATRIX_EXTERNAL_COMMAND_CLOSE_FAILURES");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return false;
            }

            foreach (var candidate in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if ((candidate == "*" || string.Equals(candidate, label, StringComparison.OrdinalIgnoreCase)) && InjectedCloseFailures.Add(label))
                {
                    return true;
                }
            }

            return false;
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

        public static int KillProcess(int processId)
        {
            if (kill(processId, 9) == 0)
            {
                return 1;
            }

            var error = Marshal.GetLastWin32Error();
            return error == NoSuchProcess ? 0 : -error;
        }

        public static int KillProcessGroup(int processGroupId)
        {
            if (processGroupId <= 0)
            {
                return -22;
            }

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

function Add-CleanupErrorText {
    param(
        [string]$Existing,
        [Parameter(Mandatory = $true)][string]$NewError
    )

    if ([string]::IsNullOrWhiteSpace($Existing)) {
        return $NewError
    }

    return "$Existing $NewError"
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
        [string]$StartError,
        [string]$CleanupError,
        [string]$ContainmentKind,
        [string]$ContainmentLimitation
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($StartError) { [void]$reasons.Add($StartError) }
    if ($ContainmentError) { [void]$reasons.Add($ContainmentError) }
    elseif (-not $ContainmentEstablished) { [void]$reasons.Add('Process containment was not established.') }
    if ($TimedOut) { [void]$reasons.Add('The command exceeded its deadline.') }
    if ($KillError) { [void]$reasons.Add("Process-tree termination failed: $KillError") }
    if (-not $CaptureComplete) { [void]$reasons.Add($CaptureError ?? 'Standard output or error was not fully captured.') }
    if ($DescendantError) { [void]$reasons.Add($DescendantError) }
    if ($CleanupError) { [void]$reasons.Add("Process containment cleanup failed: $CleanupError") }
    if ($null -eq $ExitCode -and -not $StartError) { [void]$reasons.Add('The command did not produce a final exit code.') }
    if ($null -ne $ExitCode -and $ExitCode -ne 0) { [void]$reasons.Add("The command exited with code $ExitCode.") }

    $containmentProven = $ContainmentEstablished -and
        [string]::IsNullOrWhiteSpace($ContainmentError) -and
        [string]::IsNullOrWhiteSpace($DescendantError) -and
        [string]::IsNullOrWhiteSpace($KillError) -and
        [string]::IsNullOrWhiteSpace($CleanupError) -and
        [string]::IsNullOrWhiteSpace($StartError)

    [pscustomobject]@{
        ExitCode = $ExitCode
        TimedOut = $TimedOut
        Output = $Output
        Error = $Error
        CaptureComplete = $CaptureComplete
        ContainmentEstablished = $ContainmentEstablished
        DescendantsContained = $containmentProven
        ContainmentKind = $ContainmentKind
        ContainmentLimitation = $ContainmentLimitation
        KillError = $KillError
        CaptureError = $CaptureError
        ContainmentError = $ContainmentError
        DescendantError = $DescendantError
        StartError = $StartError
        CleanupError = $CleanupError
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

function New-UnixProcessCgroup {
    if ($IsWindows) {
        return $null
    }

    $root = '/sys/fs/cgroup'
    $parents = [System.Collections.Generic.List[string]]::new()
    [void]$parents.Add($root)
    $currentCgroupFile = '/proc/self/cgroup'
    if (Test-Path -LiteralPath $currentCgroupFile -PathType Leaf) {
        foreach ($line in [IO.File]::ReadAllLines($currentCgroupFile)) {
            $match = [regex]::Match($line, '^0::(?<path>/.*)$')
            if ($match.Success) {
                $relative = $match.Groups['path'].Value.TrimStart('/')
                if (-not [string]::IsNullOrWhiteSpace($relative)) {
                    $candidate = Join-Path $root $relative
                    if (-not $parents.Contains($candidate)) {
                        [void]$parents.Add($candidate)
                    }
                }

                break
            }
        }
    }

    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($parent in $parents) {
        if (-not (Test-Path -LiteralPath (Join-Path $parent 'cgroup.controllers') -PathType Leaf)) {
            continue
        }

        $name = "keelmatrix-external-$PID-$([Guid]::NewGuid().ToString('N'))"
        $path = Join-Path $parent $name
        try {
            [IO.Directory]::CreateDirectory($path) | Out-Null
            foreach ($required in @('cgroup.procs', 'cgroup.events')) {
                if (-not (Test-Path -LiteralPath (Join-Path $path $required) -PathType Leaf)) {
                    throw "The cgroup v2 containment directory did not expose '$required'."
                }
            }

            return $path
        }
        catch {
            [void]$errors.Add("${parent}: $($_.Exception.Message)")
            if (Test-Path -LiteralPath $path -PathType Container) {
                try { [IO.Directory]::Delete($path) } catch { }
            }
        }
    }

    $detail = if ($errors.Count -gt 0) { " Attempts: $($errors -join ' | ')" } else { '' }
    throw "Unable to establish Unix cgroup v2 containment in a writable hierarchy.$detail"
}

function Add-UnixProcessToCgroup {
    param(
        [Parameter(Mandatory = $true)][string]$CgroupPath,
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    [IO.File]::WriteAllText((Join-Path $CgroupPath 'cgroup.procs'), "$ProcessId`n")
}

function Get-UnixCgroupProcessIds {
    param([Parameter(Mandatory = $true)][string]$CgroupPath)

    $contents = [IO.File]::ReadAllText((Join-Path $CgroupPath 'cgroup.procs'))
    return @($contents -split '\s+' | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
}

function Wait-ForUnixCgroupExit {
    param([Parameter(Mandatory = $true)][string]$CgroupPath)

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-UnixCgroupProcessIds -CgroupPath $CgroupPath).Count -eq 0) {
            return $true
        }

        Start-Sleep -Milliseconds 50
    }

    return $false
}

function Stop-UnixCgroup {
    param([Parameter(Mandatory = $true)][string]$CgroupPath)

    $killFile = Join-Path $CgroupPath 'cgroup.kill'
    if (Test-Path -LiteralPath $killFile -PathType Leaf) {
        [IO.File]::WriteAllText($killFile, "1`n")
        return
    }

    foreach ($processId in @(Get-UnixCgroupProcessIds -CgroupPath $CgroupPath)) {
        $state = [KeelMatrix.UnixExternalCommandNative]::KillProcess($processId)
        if ($state -lt 0) {
            throw "Unable to terminate Unix cgroup process $processId (errno $(-$state))."
        }
    }
}

function Get-UnixDescendantProcessIds {
    param([Parameter(Mandatory = $true)][int]$RootProcessId)

    $children = @{}
    foreach ($directory in [IO.Directory]::EnumerateDirectories('/proc')) {
        $name = [IO.Path]::GetFileName($directory)
        if ($name -notmatch '^\d+$') {
            continue
        }

        try {
            $stat = [IO.File]::ReadAllText((Join-Path $directory 'stat'))
            $closeParenthesis = $stat.LastIndexOf(')')
            if ($closeParenthesis -lt 0) {
                throw 'The Linux process stat record had no closing command-name delimiter.'
            }

            $fields = $stat.Substring($closeParenthesis + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
            if ($fields.Count -lt 2) {
                throw 'The Linux process stat record was incomplete.'
            }

            $state = $fields[0]
            if ($state -eq 'Z') {
                continue
            }

            $parentProcessId = [int]$fields[1]
            if (-not $children.ContainsKey($parentProcessId)) {
                $children[$parentProcessId] = [System.Collections.Generic.List[int]]::new()
            }

            [void]$children[$parentProcessId].Add([int]$name)
        }
        catch [IO.FileNotFoundException] {
            continue
        }
        catch [IO.DirectoryNotFoundException] {
            continue
        }
        catch {
            throw "Unable to inspect Unix process '$name': $($_.Exception.Message)"
        }
    }

    $descendants = [System.Collections.Generic.List[int]]::new()
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    while ($pending.Count -gt 0) {
        $parentProcessId = $pending.Dequeue()
        if (-not $children.ContainsKey($parentProcessId)) {
            continue
        }

        foreach ($childProcessId in $children[$parentProcessId]) {
            [void]$descendants.Add($childProcessId)
            $pending.Enqueue($childProcessId)
        }
    }

    return @($descendants)
}

function Wait-ForUnixDescendantExit {
    param(
        [Parameter(Mandatory = $true)][int]$RootProcessId,
        [int]$TimeoutSeconds = 5
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-UnixDescendantProcessIds -RootProcessId $RootProcessId).Count -eq 0) {
            return $true
        }

        Start-Sleep -Milliseconds 50
    }

    return $false
}

function Stop-UnixDescendants {
    param(
        [Parameter(Mandatory = $true)][int]$RootProcessId,
        [int]$TimeoutSeconds = 5
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $descendants = @(Get-UnixDescendantProcessIds -RootProcessId $RootProcessId)
        if ($descendants.Count -eq 0) {
            return
        }

        foreach ($processId in $descendants) {
            $state = [KeelMatrix.UnixExternalCommandNative]::KillProcess($processId)
            if ($state -lt 0) {
                throw "Unable to terminate Unix descendant process $processId (errno $(-$state))."
            }
        }

        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    if (@(Get-UnixDescendantProcessIds -RootProcessId $RootProcessId).Count -gt 0) {
        throw 'The Unix subreaper still owned a running descendant after termination.'
    }
}

function Get-MacProcessSnapshot {
    if (-not $IsMacOS) {
        throw 'macOS process inspection was requested on a non-macOS host.'
    }

    $psPath = Resolve-ExternalExecutable -FilePath 'ps'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $psPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void]$startInfo.ArgumentList.Add('-axo')
    [void]$startInfo.ArgumentList.Add('pid=,ppid=,pgid=,stat=')

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Unable to start macOS process inspection.'
    }

    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorText = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(5000)) {
            try { $process.Kill() } catch { }
            throw 'macOS process inspection exceeded its bounded deadline.'
        }

        $inspectionError = $errorText.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            throw "macOS process inspection failed with exit code $($process.ExitCode): $inspectionError"
        }

        $snapshot = [System.Collections.Generic.List[object]]::new()
        foreach ($line in ($output.GetAwaiter().GetResult() -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $fields = $line.Trim() -split '\s+'
            if ($fields.Count -lt 4 -or $fields[0] -notmatch '^\d+$' -or $fields[1] -notmatch '^\d+$' -or $fields[2] -notmatch '^\d+$') {
                throw "macOS process inspection returned an invalid record: '$line'."
            }

            [void]$snapshot.Add([pscustomobject]@{
                ProcessId = [int]$fields[0]
                ParentProcessId = [int]$fields[1]
                ProcessGroupId = [int]$fields[2]
                State = [string]$fields[3]
            })
        }

        return @($snapshot)
    }
    finally {
        $process.Dispose()
    }
}

function Get-MacDescendantProcesses {
    param([Parameter(Mandatory = $true)][int]$RootProcessId)

    $snapshot = @(Get-MacProcessSnapshot)
    $children = @{}
    foreach ($record in $snapshot) {
        if (-not $children.ContainsKey($record.ParentProcessId)) {
            $children[$record.ParentProcessId] = [System.Collections.Generic.List[object]]::new()
        }
        [void]$children[$record.ParentProcessId].Add($record)
    }

    $descendants = [System.Collections.Generic.List[object]]::new()
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    while ($pending.Count -gt 0) {
        $parentProcessId = $pending.Dequeue()
        if (-not $children.ContainsKey($parentProcessId)) {
            continue
        }

        foreach ($child in $children[$parentProcessId]) {
            if ($child.State -notmatch '^Z') {
                [void]$descendants.Add($child)
                $pending.Enqueue($child.ProcessId)
            }
        }
    }

    return @($descendants)
}

function Wait-ForMacDescendantExit {
    param(
        [Parameter(Mandatory = $true)][int]$RootProcessId,
        [int]$TimeoutSeconds = 5
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-MacDescendantProcesses -RootProcessId $RootProcessId).Count -eq 0) {
            return $true
        }

        Start-Sleep -Milliseconds 50
    }

    return $false
}

function Stop-MacDescendants {
    param(
        [Parameter(Mandatory = $true)][int]$RootProcessId,
        [int]$TimeoutSeconds = 5
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $descendants = @(Get-MacDescendantProcesses -RootProcessId $RootProcessId)
        if ($descendants.Count -eq 0) {
            return
        }

        foreach ($record in ($descendants | Sort-Object ProcessId -Descending)) {
            $state = [KeelMatrix.UnixExternalCommandNative]::KillProcess($record.ProcessId)
            if ($state -lt 0) {
                throw "Unable to terminate macOS descendant process $($record.ProcessId) (errno $(-$state))."
            }
        }

        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    if (@(Get-MacDescendantProcesses -RootProcessId $RootProcessId).Count -gt 0) {
        throw 'The macOS session/process-group inspection still found a running descendant after termination.'
    }
}

function Stop-MacProcessGroup {
    param([Parameter(Mandatory = $true)][int]$ProcessGroupId)

    $state = [KeelMatrix.UnixExternalCommandNative]::KillProcessGroup($ProcessGroupId)
    if ($state -lt 0) {
        throw "Unable to terminate macOS process group $ProcessGroupId (errno $(-$state))."
    }
}

function Wait-ForUnixExitCode {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $contents = [IO.File]::ReadAllText($Path).Trim()
            if ($contents -match '^-?\d+$') {
                return [int]$contents
            }
        }

        Start-Sleep -Milliseconds 25
    }

    return $null
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
    $cleanupError = $null
    $containmentKind = $null
    $containmentLimitation = $null
    $exitCode = $null
    $timedOut = $false
    $completed = $false
    $containmentEstablished = $false
    $captureComplete = $false
    $process = $null
    $native = $null
    $stdoutHandle = $null
    $stderrHandle = $null
    $stdoutReader = $null
    $stderrReader = $null
    $stdoutTask = $null
    $stderrTask = $null
    $stdoutMarkerTask = $null
    $unixCgroupPath = $null
    $unixContainmentMode = $null
    $unixRootProcessId = $null
    $unixGatePath = $null
    $unixExitPath = $null
    $unixReleasePath = $null
    $macProcessGroupId = $null
    $macSessionId = $null
    $savedBuildServerReuse = [Environment]::GetEnvironmentVariable('MSBUILDDISABLENODEREUSE', 'Process')
    $savedDotnetBuildServerDisable = [Environment]::GetEnvironmentVariable('DOTNET_CLI_DISABLE_BUILD_SERVERS', 'Process')
    $savedSharedCompilation = [Environment]::GetEnvironmentVariable('UseSharedCompilation', 'Process')
    $savedMsBuildNodeReuse = [Environment]::GetEnvironmentVariable('MSBuildNodeReuse', 'Process')
    [Environment]::SetEnvironmentVariable('MSBUILDDISABLENODEREUSE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_DISABLE_BUILD_SERVERS', '1', 'Process')
    [Environment]::SetEnvironmentVariable('UseSharedCompilation', 'false', 'Process')
    [Environment]::SetEnvironmentVariable('MSBuildNodeReuse', 'false', 'Process')

    try {
        if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
            throw "Working directory '$WorkingDirectory' was not found."
        }

        $resolvedFilePath = Resolve-ExternalExecutable -FilePath $FilePath
        if ($IsWindows) {
            $native = [KeelMatrix.ExternalCommandNative]::StartWindows($resolvedFilePath, $ArgumentList, $WorkingDirectory)
            $containmentEstablished = $true
            $containmentKind = 'windows-job-object-whole-tree'
            [void][KeelMatrix.ExternalCommandNative]::GetActiveProcessCount($native.JobHandle)
            $stdoutHandle = [Microsoft.Win32.SafeHandles.SafeFileHandle]::new($native.StandardOutputReadHandle, $false)
            $stderrHandle = [Microsoft.Win32.SafeHandles.SafeFileHandle]::new($native.StandardErrorReadHandle, $false)
            $stdoutReader = [IO.StreamReader]::new([IO.FileStream]::new($stdoutHandle, [IO.FileAccess]::Read, 4096, $false), [Text.Encoding]::UTF8, $false, 4096, $true)
            $stderrReader = [IO.StreamReader]::new([IO.FileStream]::new($stderrHandle, [IO.FileAccess]::Read, 4096, $false), [Text.Encoding]::UTF8, $false, 4096, $true)
            $stdoutTask = $stdoutReader.ReadToEndAsync()
            $stderrTask = $stderrReader.ReadToEndAsync()
            $completed = [KeelMatrix.ExternalCommandNative]::WaitForExit($native.ProcessHandle, [int]([int64]$TimeoutSeconds * 1000))
            $timedOut = -not $completed
        }
        else {
            if (-not $IsLinux -and -not $IsMacOS) {
                throw 'This Unix host does not expose a supported external-command containment mechanism.'
            }

            if ($IsLinux) {
                try {
                    $unixCgroupPath = New-UnixProcessCgroup
                    $unixContainmentMode = 'cgroup'
                    $containmentKind = 'linux-cgroup-v2-whole-tree'
                }
                catch {
                    $unixContainmentMode = 'subreaper'
                    $containmentKind = 'linux-child-subreaper-process-tree'
                }
            }
            else {
                $unixContainmentMode = 'macos-session-process-group'
                $containmentKind = 'macos-session-process-group'
                $containmentLimitation = 'A process that deliberately creates a new session can escape the session/process-group boundary.'
            }

            $unixGatePath = Join-Path ([IO.Path]::GetTempPath()) "keelmatrix-external-gate-$([Guid]::NewGuid().ToString('N'))"
            $unixExitPath = Join-Path ([IO.Path]::GetTempPath()) "keelmatrix-external-exit-$([Guid]::NewGuid().ToString('N'))"
            $unixReleasePath = Join-Path ([IO.Path]::GetTempPath()) "keelmatrix-external-release-$([Guid]::NewGuid().ToString('N'))"
            $pwshPath = Resolve-ExternalExecutable -FilePath ([string]::Join('', @('p', 'w', 's', 'h')))
            $payload = [ordered]@{
                FilePath  = $resolvedFilePath
                Arguments = @($ArgumentList | ForEach-Object { [string]$_ })
            } | ConvertTo-Json -Compress
            $payloadBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
            $sessionSource = 'using System; using System.Runtime.InteropServices; public static class ExternalCommandSession { [DllImport("libc", SetLastError=true)] public static extern int setsid(); [DllImport("libc", SetLastError=true)] public static extern int getpgrp(); [DllImport("libc", SetLastError=true)] public static extern int getsid(int processId); [DllImport("libc", SetLastError=true)] private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5); [DllImport("libc", SetLastError=true)] private static extern int waitpid(int processId, IntPtr status, int options); public static int SetChildSubreaper(int unused) => prctl(36, 1, 0, 0, 0); public static void ReapChildren(int unused) { while (waitpid(-1, IntPtr.Zero, 1) > 0) { } } }'
            $sessionSourceBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sessionSource))
            $gatePathBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($unixGatePath))
            $exitPathBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($unixExitPath))
            $releasePathBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($unixReleasePath))
            $subreaperLiteral = if ($unixContainmentMode -eq 'subreaper') { '$true' } else { '$false' }
            $heldWrapperLiteral = if ($unixContainmentMode -eq 'subreaper' -or $unixContainmentMode -eq 'macos-session-process-group') { '$true' } else { '$false' }
            $wrapperCommand = @'
$source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__SESSION_SOURCE__'))
Add-Type -TypeDefinition $source
if ([ExternalCommandSession]::setsid() -lt 0) { exit 125 }
if (__ENABLE_SUBREAPER__ -and [ExternalCommandSession]::SetChildSubreaper(0) -ne 0) { exit 126 }
[Console]::WriteLine(('__KEELMATRIX_EXTERNAL_COMMAND_PID__' + [Environment]::ProcessId + ';' + [ExternalCommandSession]::getpgrp() + ';' + [ExternalCommandSession]::getsid(0)))
$gatePath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__GATE_PATH__'))
while (-not [IO.File]::Exists($gatePath)) { Start-Sleep -Milliseconds 10 }
$payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__ARGUMENT_PAYLOAD__'))
$invocation = $payload | ConvertFrom-Json
$target = [string]$invocation.FilePath
$arguments = @($invocation.Arguments | ForEach-Object { [string]$_ })
& $target @arguments
$commandExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
if (__HOLD_WRAPPER__) {
    $exitPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__EXIT_PATH__'))
    $releasePath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__RELEASE_PATH__'))
    [IO.File]::WriteAllText($exitPath, [string]$commandExitCode)
    while (-not [IO.File]::Exists($releasePath)) { Start-Sleep -Milliseconds 10 }
    [ExternalCommandSession]::ReapChildren(0)
}
exit $commandExitCode
'@.Replace('__SESSION_SOURCE__', $sessionSourceBase64).Replace('__ARGUMENT_PAYLOAD__', $payloadBase64).Replace('__GATE_PATH__', $gatePathBase64).Replace('__EXIT_PATH__', $exitPathBase64).Replace('__RELEASE_PATH__', $releasePathBase64).Replace('__ENABLE_SUBREAPER__', $subreaperLiteral).Replace('__HOLD_WRAPPER__', $heldWrapperLiteral)
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

            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $startInfo
            if (-not $process.Start()) {
                throw "Unable to start '$FilePath'."
            }

            $stdoutMarkerTask = $process.StandardOutput.ReadLineAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (-not $stdoutMarkerTask.Wait(5000)) {
                throw 'The Unix containment marker was not published.'
            }
            $marker = $stdoutMarkerTask.GetAwaiter().GetResult()
            $markerMatch = [regex]::Match($marker, '^__KEELMATRIX_EXTERNAL_COMMAND_PID__(?<pid>\d+);(?<pgid>\d+);(?<sid>\d+)$')
            if (-not $markerMatch.Success) {
                throw 'The Unix cgroup containment marker was invalid.'
            }

            $unixRootProcessId = [int]$markerMatch.Groups['pid'].Value
            if ($unixContainmentMode -eq 'macos-session-process-group') {
                $macProcessGroupId = [int]$markerMatch.Groups['pgid'].Value
                $macSessionId = [int]$markerMatch.Groups['sid'].Value
                if ($macProcessGroupId -le 0 -or $macSessionId -le 0) {
                    throw 'The macOS session/process-group containment marker was invalid.'
                }
            }
            if ($unixContainmentMode -eq 'cgroup') {
                Add-UnixProcessToCgroup -CgroupPath $unixCgroupPath -ProcessId $unixRootProcessId
            }
            $containmentEstablished = $true
            [IO.File]::WriteAllText($unixGatePath, "ready`n")
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            if ($unixContainmentMode -eq 'subreaper') {
                $exitCode = Wait-ForUnixExitCode -Path $unixExitPath -TimeoutSeconds $TimeoutSeconds
                $completed = $null -ne $exitCode
                $timedOut = -not $completed
                if ($completed) {
                    if (-not (Wait-ForUnixDescendantExit -RootProcessId $unixRootProcessId)) {
                        $descendantError = 'The command exited but its Unix subreaper still owned a running descendant.'
                        try {
                            Stop-UnixDescendants -RootProcessId $unixRootProcessId
                        }
                        catch {
                            $killError = $_.Exception.Message
                        }
                    }

                    [IO.File]::WriteAllText($unixReleasePath, "release`n")
                    if (-not $process.WaitForExit(5000)) {
                        $cleanupError = 'The Unix subreaper wrapper did not exit after descendant inspection.'
                        try { $process.Kill() } catch { $killError = "Root-process termination failed: $($_.Exception.Message)" }
                    }
                }
            }
            elseif ($unixContainmentMode -eq 'macos-session-process-group') {
                $exitCode = Wait-ForUnixExitCode -Path $unixExitPath -TimeoutSeconds $TimeoutSeconds
                $completed = $null -ne $exitCode
                $timedOut = -not $completed
                if ($completed) {
                    try {
                        if (-not (Wait-ForMacDescendantExit -RootProcessId $unixRootProcessId)) {
                            $descendantError = 'The command exited but macOS session/process-group inspection found a running descendant.'
                            try {
                                Stop-MacDescendants -RootProcessId $unixRootProcessId
                            }
                            catch {
                                $killError = $_.Exception.Message
                                try { Stop-MacProcessGroup -ProcessGroupId $macProcessGroupId } catch { $killError = "$killError $($_.Exception.Message)" }
                            }
                        }
                    }
                    catch {
                        $descendantError = "macOS descendant inspection failed: $($_.Exception.Message)"
                        try {
                            Stop-MacDescendants -RootProcessId $unixRootProcessId
                        }
                        catch {
                            $killError = $_.Exception.Message
                            try { Stop-MacProcessGroup -ProcessGroupId $macProcessGroupId } catch { $killError = "$killError $($_.Exception.Message)" }
                        }
                    }

                    try { [IO.File]::WriteAllText($unixReleasePath, "release`n") } catch { $cleanupError = $_.Exception.Message }
                    if (-not $process.WaitForExit(5000)) {
                        $cleanupError = if ($cleanupError) { "$cleanupError The macOS wrapper did not exit after descendant inspection." } else { 'The macOS wrapper did not exit after descendant inspection.' }
                        try { $process.Kill() } catch { $killError = "Root-process termination failed: $($_.Exception.Message)" }
                    }
                }
            }
            else {
                $completed = $process.WaitForExit([int]([int64]$TimeoutSeconds * 1000))
                $timedOut = -not $completed
                if ($completed) {
                    $exitCode = $process.ExitCode
                }
            }
        }

        if ($timedOut) {
            if ($IsWindows) {
                if (-not [KeelMatrix.ExternalCommandNative]::TerminateJob($native.JobHandle, 1)) {
                    $killError = 'TerminateJobObject returned failure.'
                }

                [void][KeelMatrix.ExternalCommandNative]::WaitForExit($native.ProcessHandle, 5000)
            }
            else {
                try {
                    if ($unixContainmentMode -eq 'cgroup') {
                        Stop-UnixCgroup -CgroupPath $unixCgroupPath
                        if (-not (Wait-ForUnixCgroupExit -CgroupPath $unixCgroupPath)) {
                            $killError = 'The Unix cgroup remained populated after termination.'
                        }
                    }
                    elseif ($unixContainmentMode -eq 'macos-session-process-group') {
                        Stop-MacDescendants -RootProcessId $unixRootProcessId
                    }
                    else {
                        Stop-UnixDescendants -RootProcessId $unixRootProcessId
                    }
                }
                catch {
                    $killError = $_.Exception.Message
                    if ($unixContainmentMode -eq 'macos-session-process-group') {
                        try { Stop-MacProcessGroup -ProcessGroupId $macProcessGroupId } catch { $killError = "$killError $($_.Exception.Message)" }
                    }
                }

                if ($unixContainmentMode -eq 'subreaper' -or $unixContainmentMode -eq 'macos-session-process-group') {
                    try { [IO.File]::WriteAllText($unixReleasePath, "release`n") } catch { $cleanupError = $_.Exception.Message }
                    if (-not $process.WaitForExit(5000)) {
                        $wrapperKind = if ($unixContainmentMode -eq 'subreaper') { 'Unix subreaper' } else { 'macOS' }
                        $cleanupError = if ($cleanupError) { "$cleanupError The $wrapperKind wrapper did not exit." } else { "The $wrapperKind wrapper did not exit." }
                        try { $process.Kill() } catch { $killError = "Root-process termination failed: $($_.Exception.Message)" }
                    }
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
                if ($unixContainmentMode -eq 'cgroup') {
                    if (-not (Wait-ForUnixCgroupExit -CgroupPath $unixCgroupPath)) {
                        $descendantError = 'The command exited but its Unix containment cgroup still contained a running descendant.'
                        try {
                            Stop-UnixCgroup -CgroupPath $unixCgroupPath
                            if (-not (Wait-ForUnixCgroupExit -CgroupPath $unixCgroupPath)) {
                                $killError = 'The Unix cgroup remained populated after descendant cleanup.'
                            }
                        }
                        catch {
                            $killError = $_.Exception.Message
                        }
                    }
                }
            }

            if ($IsWindows) {
                $exitCode = [KeelMatrix.ExternalCommandNative]::GetExitCode($native.ProcessHandle)
            }
            elseif ($unixContainmentMode -eq 'subreaper' -or $unixContainmentMode -eq 'macos-session-process-group') {
                try { [IO.File]::WriteAllText($unixReleasePath, "release`n") } catch { $cleanupError = $_.Exception.Message }
            }
        }
    }
    catch {
        $cleanupException = $_.Exception
        while ($null -ne $cleanupException) {
            $exceptionCleanupProperty = $cleanupException.PSObject.Properties['CleanupError']
            if ($null -ne $exceptionCleanupProperty -and -not [string]::IsNullOrWhiteSpace([string]$exceptionCleanupProperty.Value)) {
                $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError ([string]$exceptionCleanupProperty.Value)
                break
            }

            $cleanupException = $cleanupException.InnerException
        }

        if (-not $containmentEstablished) {
            $startError = $_.Exception.Message
        }
        elseif ($null -eq $containmentError) {
            $containmentError = $_.Exception.Message
        }

        if ($containmentEstablished -and $null -ne $native) {
            if (-not [KeelMatrix.ExternalCommandNative]::TerminateJob($native.JobHandle, 1)) {
                $killError = 'TerminateJobObject returned failure while handling another containment error.'
            }
        }
        elseif ($null -ne $process) {
            if ($containmentEstablished) {
                try {
                    if ($unixContainmentMode -eq 'cgroup') {
                        Stop-UnixCgroup -CgroupPath $unixCgroupPath
                    }
                    elseif ($unixContainmentMode -eq 'macos-session-process-group') {
                        Stop-MacDescendants -RootProcessId $unixRootProcessId
                    }
                    else {
                        Stop-UnixDescendants -RootProcessId $unixRootProcessId
                    }
                }
                catch {
                    $killError = $_.Exception.Message
                }

                if ($unixContainmentMode -eq 'subreaper' -or $unixContainmentMode -eq 'macos-session-process-group') {
                    try { [IO.File]::WriteAllText($unixReleasePath, "release`n") } catch { $cleanupError = $_.Exception.Message }
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

        if ($null -ne $unixGatePath -and (Test-Path -LiteralPath $unixGatePath -PathType Leaf)) {
            try { Remove-Item -LiteralPath $unixGatePath -Force -ErrorAction Stop } catch { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError $_.Exception.Message }
        }

        foreach ($temporaryPath in @($unixExitPath, $unixReleasePath)) {
            if ($null -ne $temporaryPath -and (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
                try { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop } catch { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError $_.Exception.Message }
            }
        }

        if ($null -ne $unixCgroupPath) {
            try {
                if (-not (Wait-ForUnixCgroupExit -CgroupPath $unixCgroupPath)) {
                    $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError 'The Unix cgroup remained populated during cleanup.'
                }
                if (Test-Path -LiteralPath $unixCgroupPath -PathType Container) {
                    [IO.Directory]::Delete($unixCgroupPath)
                }
            }
            catch {
                $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError $_.Exception.Message
            }
        }

        if ($null -ne $stdoutReader) {
            try { $stdoutReader.Dispose() } catch { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError "Standard output reader cleanup failed: $($_.Exception.Message)" }
        }
        if ($null -ne $stderrReader) {
            try { $stderrReader.Dispose() } catch { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError "Standard error reader cleanup failed: $($_.Exception.Message)" }
        }
        if ($null -ne $native) {
            $closeFailure = [KeelMatrix.ExternalCommandNative]::Close($native.StandardOutputReadHandle, 'stdout-read')
            if (-not [string]::IsNullOrWhiteSpace($closeFailure)) { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError $closeFailure }
            $closeFailure = [KeelMatrix.ExternalCommandNative]::Close($native.StandardErrorReadHandle, 'stderr-read')
            if (-not [string]::IsNullOrWhiteSpace($closeFailure)) { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError $closeFailure }
        }
        if ($null -ne $native) {
            $closeFailure = [KeelMatrix.ExternalCommandNative]::Close($native.ProcessHandle, 'process')
            if (-not [string]::IsNullOrWhiteSpace($closeFailure)) { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError $closeFailure }
            $closeFailure = [KeelMatrix.ExternalCommandNative]::Close($native.JobHandle, 'job')
            if (-not [string]::IsNullOrWhiteSpace($closeFailure)) { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError $closeFailure }
        }
        if ($null -ne $process) {
            try { $process.Dispose() } catch { $cleanupError = Add-CleanupErrorText -Existing $cleanupError -NewError "Unix process cleanup failed: $($_.Exception.Message)" }
        }

        [Environment]::SetEnvironmentVariable('MSBUILDDISABLENODEREUSE', $savedBuildServerReuse, 'Process')
        [Environment]::SetEnvironmentVariable('DOTNET_CLI_DISABLE_BUILD_SERVERS', $savedDotnetBuildServerDisable, 'Process')
        [Environment]::SetEnvironmentVariable('UseSharedCompilation', $savedSharedCompilation, 'Process')
        [Environment]::SetEnvironmentVariable('MSBuildNodeReuse', $savedMsBuildNodeReuse, 'Process')
    }

    New-ExternalCommandResult -ExitCode $exitCode -TimedOut $timedOut -Output $output -Error $errorText `
        -CaptureComplete $captureComplete -ContainmentEstablished $containmentEstablished `
        -CaptureError $captureError -ContainmentError $containmentError -DescendantError $descendantError `
        -KillError $killError -StartError $startError -CleanupError $cleanupError `
        -ContainmentKind $containmentKind -ContainmentLimitation $containmentLimitation
}
