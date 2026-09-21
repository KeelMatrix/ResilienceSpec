using System.Diagnostics;
using System.Text;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void PlannedTargetFailsClosed()
    {
        var result = RunContract("v0.1.0", "0.1.0", "Planned");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("planned", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionMismatchFailsClosed()
    {
        var result = RunContract("v0.1.0", "0.1.1", "Finalized");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemediationMarkerFailsClosed()
    {
        var result = RunContract("v0.1.0", "0.1.0", "Finalized", "- Fixed the initial implementation.");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("marker", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstReleaseMustBeAddedOnly()
    {
        var result = RunContract("v0.1.0", "0.1.0", "Finalized", "- Provides the verifier.", "### Changed", "- Changes defaults.");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Added", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinalizedConsistentFirstReleasePasses()
    {
        var result = RunContract("v0.1.0", "0.1.0", "Finalized", "- Provides deterministic scripted HTTP failure verification.");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Release contract passed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizedChangelogAllowsPendingOutcomeLanguage()
    {
        var changelog = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "CHANGELOG.md"));
        changelog = changelog.Replace(
            "## [0.1.0] - Planned",
            "## [0.1.0] - 2026-09-16",
            StringComparison.Ordinal);

        var result = RunContractWithChangelog("v0.1.0", "0.1.0", changelog);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Release contract passed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowPublishesTheSymbolArtifactExactlyOnce()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));
        var packageStart = workflow.IndexOf("name: Publish package", StringComparison.Ordinal);
        var symbolsStart = workflow.IndexOf("name: Publish symbols", StringComparison.Ordinal);

        Assert.True(packageStart >= 0 && symbolsStart > packageStart, "The release workflow publish steps were not found.");
        var packageStep = workflow[packageStart..symbolsStart];
        var symbolsStep = workflow[symbolsStart..];

        Assert.Contains("--no-symbols", packageStep, StringComparison.Ordinal);
        Assert.DoesNotContain(".snupkg", packageStep, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(symbolsStep, ".snupkg"));
    }

    private static ContractResult RunContract(
        string tag,
        string packageVersion,
        string releaseState,
        params string[] additionalEntryLines)
    {
        var releaseHeading = releaseState == "Planned"
            ? "## [0.1.0] - Planned"
            : "## [0.1.0] - 2026-09-16";
        var entry = new List<string>
        {
            "# Changelog",
            "",
            "## [Unreleased]",
            "",
            releaseHeading,
            "",
            "### Added",
            "- Provides the initial package contract."
        };
        entry.AddRange(additionalEntryLines);
        return RunContractWithChangelog(tag, packageVersion, string.Join(Environment.NewLine, entry));
    }

    private static ContractResult RunContractWithChangelog(
        string tag,
        string packageVersion,
        string changelog)
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("resilience-release-contract-");
        try
        {
            var projectPath = Path.Combine(tempDirectory.FullName, "Package.csproj");
            var changelogPath = Path.Combine(tempDirectory.FullName, "CHANGELOG.md");
            File.WriteAllText(
                projectPath,
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.ResilienceSpec</PackageId><Version>{packageVersion}</Version><IsPackable>true</IsPackable></PropertyGroup></Project>",
                new UTF8Encoding(false));

            File.WriteAllText(changelogPath, changelog, new UTF8Encoding(false));

            var scriptPath = Path.Combine(repositoryRoot, "scripts", "Validate-ReleaseContract.ps1");
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-Tag");
            startInfo.ArgumentList.Add(tag);
            startInfo.ArgumentList.Add("-PackageProject");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("-ChangelogPath");
            startInfo.ArgumentList.Add(changelogPath);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start pwsh.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(30_000), "Release contract process did not finish within 30 seconds.");
            return new ContractResult(process.ExitCode, $"{output.Result}{Environment.NewLine}{error.Result}");
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "Validate-ReleaseContract.ps1")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the release contract test.");
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
        {
            count++;
        }

        return count;
    }

    private sealed record ContractResult(int ExitCode, string Output);
}
