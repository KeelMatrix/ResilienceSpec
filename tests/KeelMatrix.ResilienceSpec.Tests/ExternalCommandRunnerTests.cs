using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class ExternalCommandRunnerTests
{
    [Fact]
    public void SuccessfulCommandCapturesBothStreamsAndLeavesNoDescendant()
    {
        var result = RunFixture("Streams");

        Assert.True(result.GetProperty("Succeeded").GetBoolean());
        Assert.Equal(0, result.GetProperty("ExitCode").GetInt32());
        Assert.False(result.GetProperty("TimedOut").GetBoolean());
        Assert.True(result.GetProperty("ContainmentEstablished").GetBoolean());
        Assert.True(result.GetProperty("CaptureComplete").GetBoolean());
        Assert.Contains("stdout-marker", result.GetProperty("Output").GetString(), StringComparison.Ordinal);
        Assert.Contains("stderr-marker", result.GetProperty("Error").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StdoutOnly", "stdout-marker", "")]
    [InlineData("StderrOnly", "", "stderr-marker")]
    public void SingleStreamOutputStillCompletesTheOtherCapture(string scenario, string expectedOutput, string expectedError)
    {
        var result = RunFixture(scenario);

        Assert.True(result.GetProperty("Succeeded").GetBoolean());
        Assert.True(result.GetProperty("CaptureComplete").GetBoolean());
        Assert.Contains(expectedOutput, result.GetProperty("Output").GetString(), StringComparison.Ordinal);
        Assert.Contains(expectedError, result.GetProperty("Error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NonZeroCommandPreservesExitCodeAndDiagnostics()
    {
        var result = RunFixture("NonZero");

        Assert.False(result.GetProperty("Succeeded").GetBoolean());
        Assert.Equal(17, result.GetProperty("ExitCode").GetInt32());
        Assert.True(result.GetProperty("CaptureComplete").GetBoolean());
        Assert.Contains("stdout-marker", result.GetProperty("Output").GetString(), StringComparison.Ordinal);
        Assert.Contains("stderr-marker", result.GetProperty("Error").GetString(), StringComparison.Ordinal);
        Assert.Contains("code 17", result.GetProperty("FailureReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TimeoutIsAFailedResultAndDoesNotLeaveTheProcessAlive()
    {
        var result = RunFixture("Timeout", timeoutSeconds: 1);

        Assert.False(result.GetProperty("Succeeded").GetBoolean());
        Assert.True(result.GetProperty("TimedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("ExitCode").ValueKind);
        Assert.Contains("deadline", result.GetProperty("FailureReason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(result.GetProperty("ContainmentEstablished").GetBoolean());
    }

    [Fact]
    public void RootExitWithAChildHoldingRedirectedHandlesIsReportedAndContained()
    {
        var result = RunFixture("Descendant");
        var output = result.GetProperty("Output").GetString() ?? string.Empty;
        var match = Regex.Match(output, "child=(\\d+)", RegexOptions.CultureInvariant);

        Assert.True(match.Success, output);
        Assert.False(result.GetProperty("Succeeded").GetBoolean());
        Assert.Equal(0, result.GetProperty("ExitCode").GetInt32());
        Assert.True(result.GetProperty("CaptureComplete").GetBoolean());
        Assert.False(result.GetProperty("DescendantsContained").GetBoolean());
        Assert.Contains("descendant", result.GetProperty("FailureReason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(WaitForProcessExit(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void MissingExecutableFailsClosedBeforeReportingSuccess()
    {
        var fixture = Path.Combine(FindRepositoryRoot(), "build", "Invoke-ExternalCommand.ps1");
        var result = RunPowerShell(
            $". '{fixture.Replace("'", "''")}'; Invoke-ExternalCommand -FilePath 'resilience-spec-command-that-does-not-exist' -WorkingDirectory (Get-Location).Path -TimeoutSeconds 1 | ConvertTo-Json -Compress");

        using var document = JsonDocument.Parse(result.Output);
        var value = document.RootElement;
        Assert.False(value.GetProperty("Succeeded").GetBoolean());
        Assert.False(value.GetProperty("ContainmentEstablished").GetBoolean());
        Assert.Contains("not found", value.GetProperty("FailureReason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement RunFixture(string scenario, int timeoutSeconds = 5)
    {
        var fixture = Path.Combine(FindRepositoryRoot(), "tests", "KeelMatrix.ResilienceSpec.Tests", "ExternalCommandFixture.ps1");
        var result = RunProcess("pwsh", [
            "-NoProfile", "-File", fixture,
            "-Scenario", scenario,
            "-TimeoutSeconds", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ], FindRepositoryRoot());

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output.Trim());
        return document.RootElement.Clone();
    }

    private static ProcessResult RunPowerShell(string command) =>
        RunProcess("pwsh", ["-NoProfile", "-Command", command], FindRepositoryRoot());

    private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), $"Process '{fileName}' did not finish within 30 seconds.\n{error.Result}");
        return new ProcessResult(process.ExitCode, output.Result, error.Result);
    }

    private static bool WaitForProcessExit(int processId)
    {
        return SpinWait.SpinUntil(() =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(5));
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "build", "Invoke-ExternalCommand.ps1")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the external-command runner tests.");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
