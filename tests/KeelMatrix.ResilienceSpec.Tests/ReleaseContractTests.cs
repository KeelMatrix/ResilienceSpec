using System.Diagnostics;
using System.Security.Cryptography;
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

    [Fact]
    public void EveryPackPathPinsRepositoryMetadata()
    {
        var repositoryRoot = FindRepositoryRoot();
        var buildTargets = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Build.targets"));

        Assert.Contains("<Target Name=\"PinSourceControlInformation\"", buildTargets, StringComparison.Ordinal);
        Assert.Contains("<ScmRepositoryUrl>$(RepositoryUrl)</ScmRepositoryUrl>", buildTargets, StringComparison.Ordinal);
        Assert.Contains("<PrivateRepositoryUrl>$(RepositoryUrl)</PrivateRepositoryUrl>", buildTargets, StringComparison.Ordinal);
        Assert.Contains("<SourceRoot Update=\"@(SourceRoot)\"", buildTargets, StringComparison.Ordinal);
        Assert.Contains("<SourceRoot Include=\"$(MSBuildThisFileDirectory)\"", buildTargets, StringComparison.Ordinal);

        foreach (var relativePath in new[]
        {
            Path.Combine("scripts", "Invoke-PackageSmoke.ps1"),
            Path.Combine("scripts", "Run-Sample.ps1"),
            Path.Combine(".github", "workflows", "release.yml")
        })
        {
            var packPath = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            Assert.Contains("RepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec", packPath, StringComparison.Ordinal);
            Assert.Contains("PrivateRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec", packPath, StringComparison.Ordinal);
            Assert.Contains("ScmRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec", packPath, StringComparison.Ordinal);
            Assert.Contains("GitRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec.git", packPath, StringComparison.Ordinal);
            Assert.Contains("GitRepositoryRemoteName=origin", packPath, StringComparison.Ordinal);
            Assert.Contains("PublishRepositoryUrl=true", packPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StrictPackIsIndependentOfRepositoryOriginShape()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expectedCommit = RunProcess(
            "git",
            new List<string> { "rev-parse", "HEAD" },
            repositoryRoot).RequireSuccess("resolve the repository commit").Output.Trim();
        var temporaryRoot = Directory.CreateTempSubdirectory("resilience-origin-contract-");
        try
        {
            var localPath = CloneRepository(repositoryRoot, Path.Combine(temporaryRoot.FullName, "local-path"));
            SyncTrackedFiles(repositoryRoot, localPath);
            var noOrigin = CloneRepository(repositoryRoot, Path.Combine(temporaryRoot.FullName, "no-origin"));
            SyncTrackedFiles(repositoryRoot, noOrigin);
            RunProcess("git", new List<string> { "remote", "remove", "origin" }, noOrigin)
                .RequireSuccess("remove the no-origin remote");

            var fileOrigin = CloneRepository(
                new Uri(repositoryRoot).AbsoluteUri,
                Path.Combine(temporaryRoot.FullName, "file-origin"));
            SyncTrackedFiles(repositoryRoot, fileOrigin);
            var gitless = CopyTrackedTree(repositoryRoot, Path.Combine(temporaryRoot.FullName, "gitless"));

            var identities = new List<ArtifactIdentity>();
            foreach (var shape in new[]
            {
                (Name: "local-path", Root: localPath),
                (Name: "no-origin", Root: noOrigin),
                (Name: "file-origin", Root: fileOrigin),
                (Name: "gitless", Root: gitless)
            })
            {
                var identity = PackShape(shape.Root, shape.Name, expectedCommit, repositoryRoot);
                Console.WriteLine($"ORIGIN_SHAPE={shape.Name} PACKAGE_SHA256={identity.PackageHash} SYMBOLS_SHA256={identity.SymbolsHash}");
                identities.Add(identity);
            }

            var first = identities[0];
            foreach (var identity in identities.Skip(1))
            {
                Assert.True(
                    first.PackageHash == identity.PackageHash,
                    $"Package identity differed between '{first.ShapeName}' and '{identity.ShapeName}'. " +
                    string.Join("; ", identities.Select(FormatIdentity)));
                Assert.True(
                    first.SymbolsHash == identity.SymbolsHash,
                    $"Symbols identity differed between '{first.ShapeName}' and '{identity.ShapeName}'. " +
                    string.Join("; ", identities.Select(FormatIdentity)));
            }
        }
        finally
        {
            DeleteTemporaryTree(temporaryRoot);
        }
    }

    private static ArtifactIdentity PackShape(
        string repositoryRoot,
        string shapeName,
        string expectedCommit,
        string sourceRepositoryRoot)
    {
        var project = Path.Combine(repositoryRoot, "src", "KeelMatrix.ResilienceSpec", "KeelMatrix.ResilienceSpec.csproj");
        var packageDirectory = Path.Combine(repositoryRoot, "origin-contract-artifacts");
        Directory.CreateDirectory(packageDirectory);

        RunProcess(
            "dotnet",
            new[] { "restore", project, "--configfile", Path.Combine(repositoryRoot, "NuGet.config"), "-p:NuGetAudit=false" },
            repositoryRoot).RequireSuccess($"restore the {shapeName} shape");

        var packArguments = new List<string>
        {
            "pack", project, "-c", "Release", "-warnaserror", "--no-restore",
            "-p:PackageVersion=0.1.0",
            $"-p:SourceRevisionId={expectedCommit}",
            $"-p:RepositoryCommit={expectedCommit}",
            "-p:RepositoryBranch=refs/heads/main",
            "-p:RepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec",
            "-p:PrivateRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec",
            "-p:ScmRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec",
            "-p:GitRepositoryUrl=https://github.com/KeelMatrix/ResilienceSpec.git",
            "-p:GitRepositoryRemoteName=origin",
            "-p:PublishRepositoryUrl=true",
            "-p:NuGetAudit=false",
            "-o", packageDirectory
        };
        RunProcess("dotnet", packArguments, repositoryRoot)
            .RequireSuccess($"strict-pack the {shapeName} shape");

        var packagePath = Path.Combine(packageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.nupkg");
        var symbolsPath = Path.Combine(packageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.snupkg");
        var normalizeScript = Path.Combine(sourceRepositoryRoot, "scripts", "Normalize-PackageArchive.ps1");
        RunProcess("pwsh", new[] { "-NoProfile", "-File", normalizeScript, "-PackagePath", packagePath }, repositoryRoot)
            .RequireSuccess($"normalize the {shapeName} package");
        RunProcess("pwsh", new[] { "-NoProfile", "-File", normalizeScript, "-PackagePath", symbolsPath }, repositoryRoot)
            .RequireSuccess($"normalize the {shapeName} symbols");

        return new ArtifactIdentity(
            shapeName,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(symbolsPath))));
    }

    private static string FormatIdentity(ArtifactIdentity identity) =>
        $"{identity.ShapeName}:package={identity.PackageHash},symbols={identity.SymbolsHash}";

    private static string CloneRepository(string source, string destination)
    {
        RunProcess("git", new[] { "clone", "--no-hardlinks", "--quiet", source, destination }, Directory.GetCurrentDirectory())
            .RequireSuccess($"clone '{source}'");
        return destination;
    }

    private static string CopyTrackedTree(string repositoryRoot, string destination)
    {
        Directory.CreateDirectory(destination);
        SyncTrackedFiles(repositoryRoot, destination);
        return destination;
    }

    private static void SyncTrackedFiles(string repositoryRoot, string destination)
    {
        var files = RunProcess("git", new List<string> { "ls-files", "-z" }, repositoryRoot)
            .RequireSuccess("list tracked files")
            .Output.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var relativePath in files)
        {
            var destinationPath = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(Path.Combine(repositoryRoot, relativePath), destinationPath, overwrite: true);
        }
    }

    private static void DeleteTemporaryTree(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return;
        }

        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        directory.Delete(recursive: true);
    }

    private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(180_000), $"Process '{fileName}' did not finish within 180 seconds.");
        return new ProcessResult(process.ExitCode, $"{output.Result}{Environment.NewLine}{error.Result}");
    }

    private sealed record ProcessResult(int ExitCode, string Output)
    {
        public ProcessResult RequireSuccess(string operation)
        {
            Assert.True(ExitCode == 0, $"Failed to {operation}.\n{Output}");
            return this;
        }
    }

    private sealed record ArtifactIdentity(string ShapeName, string PackageHash, string SymbolsHash);

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
