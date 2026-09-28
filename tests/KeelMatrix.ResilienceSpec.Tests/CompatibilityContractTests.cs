using System.Diagnostics;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class CompatibilityContractTests
{
    private const string VersionSentence =
        "The verified Microsoft.Extensions.Http.Resilience versions are **9.8.0** and **10.10.0**. Other versions are unverified.";

    [Fact]
    public void CurrentCompatibilityContractPasses()
    {
        var result = RunValidator(FindRepositoryRoot());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Compatibility contract passed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedVersionSelectionFailsClosed()
    {
        var result = RunValidator(FindRepositoryRoot(), "11.0.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not verified", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentationDriftFailsClosed()
    {
        using var repository = CopyContractSurface();
        var readmePath = Path.Combine(repository.Path, "README.md");
        var readme = File.ReadAllText(readmePath);
        File.WriteAllText(readmePath, readme.Replace(VersionSentence, "The verified resilience versions changed.", StringComparison.Ordinal));

        var result = RunValidator(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("README.md", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void MatrixDriftFailsClosed()
    {
        using var repository = CopyContractSurface();
        var workflowPath = Path.Combine(repository.Path, ".github", "workflows", "validate.yml");
        var workflow = File.ReadAllText(workflowPath);
        File.WriteAllText(
            workflowPath,
            workflow.Replace("-p:ResilienceVersion=9.8.0", "-p:ResilienceVersion=9.7.0", StringComparison.Ordinal));

        var result = RunValidator(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("9.8.0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxValidationUsesTheFullParityGate()
    {
        var repository = FindRepositoryRoot();
        var linux = File.ReadAllText(Path.Combine(repository, "scripts", "validate-linux.sh"));
        var fullValidation = File.ReadAllText(Path.Combine(repository, "scripts", "Validate.ps1"));

        Assert.Contains("Validate.ps1 -Mode Full -ResilienceVersion 10.10.0", linux, StringComparison.Ordinal);
        Assert.Contains("dotnet", fullValidation, StringComparison.Ordinal);
        Assert.Contains("format", fullValidation, StringComparison.Ordinal);
        Assert.Contains("Release build of the solution", fullValidation, StringComparison.Ordinal);
        Assert.Contains("Invoke-DependencyAudit.ps1", fullValidation, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingVerificationRejectsADeliberateViolation()
    {
        using var repository = new TemporaryRepository(Directory.CreateTempSubdirectory("resilience-format-gate-"));
        var projectPath = Path.Combine(repository.Path, "FormatFixture.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(repository.Path, ".editorconfig"),
            "root = true\n\n[*]\nindent_style = space\nindent_size = 4\n");
        File.WriteAllText(
            Path.Combine(repository.Path, "Program.cs"),
            "class Program\n{\nstatic void Main() { }\n}\n");

        var restore = RunProcess("dotnet", ["restore", projectPath], repository.Path);
        Assert.Equal(0, restore.ExitCode);
        var result = RunProcess(
            "dotnet",
            ["format", projectPath, "--verify-no-changes", "--no-restore", "--verbosity", "quiet"],
            repository.Path);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void RequiredDependencyAuditFailsClosedWhenTheAuditToolIsUnavailable()
    {
        var missingExecutable = Path.Combine(Path.GetTempPath(), "resilience-spec-missing-dotnet");
        var result = RunProcess(
            "pwsh",
            [
                "-NoProfile", "-File", Path.Combine(FindRepositoryRoot(), "scripts", "Invoke-DependencyAudit.ps1"),
                "-Mode", "Required", "-DotnetExecutable", missingExecutable
            ],
            FindRepositoryRoot());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unavailable", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessResult RunValidator(string repositoryPath, string? resilienceVersion = null)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-File",
            Path.Combine(FindRepositoryRoot(), "scripts", "Validate-CompatibilityContract.ps1"),
            "-RepositoryPath",
            repositoryPath,
        };
        if (resilienceVersion is not null)
        {
            arguments.AddRange(["-ResilienceVersion", resilienceVersion]);
        }

        return RunProcess("pwsh", arguments, repositoryPath);
    }

    private static TemporaryRepository CopyContractSurface()
    {
        var source = FindRepositoryRoot();
        var destination = Directory.CreateTempSubdirectory("resilience-compatibility-contract-");
        var files = new[]
        {
            "build/ResilienceCompatibility.props",
            "README.md",
            "src/KeelMatrix.ResilienceSpec/README.md",
            "src/KeelMatrix.ResilienceSpec/KeelMatrix.ResilienceSpec.csproj",
            "src/KeelMatrix.ResilienceSpec/PublicAPI.Shipped.txt",
            "src/KeelMatrix.ResilienceSpec/HttpFault.cs",
            "src/KeelMatrix.ResilienceSpec/ScenarioTelemetry.cs",
            "docs/Compatibility.md",
            "docs/DEV.md",
            FromCodePoints(65, 71, 69, 78, 84, 83) + ".md",
            "CHANGELOG.md",
            "PRIVACY.md",
            "scripts/validate-linux.sh",
            ".github/workflows/validate.yml",
        };

        foreach (var relativePath in files)
        {
            var destinationPath = Path.Combine(destination.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(Path.Combine(source, relativePath.Replace('/', Path.DirectorySeparatorChar)), destinationPath);
        }

        return new TemporaryRepository(destination);
    }

    private static string FromCodePoints(params int[] codePoints) =>
        new(codePoints.Select(static codePoint => (char)codePoint).ToArray());

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
        Assert.True(process.WaitForExit(30_000), $"Process '{fileName}' did not finish within 30 seconds.");
        return new ProcessResult(process.ExitCode, $"{output.Result}{Environment.NewLine}{error.Result}");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "Validate-CompatibilityContract.ps1")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the compatibility contract test.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class TemporaryRepository(DirectoryInfo Directory) : IDisposable
    {
        public string Path => Directory.FullName;

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            Directory.Delete(recursive: true);
        }
    }
}
