using System.Diagnostics;
using System.Text;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

[Collection("Release contract")]
public sealed class DocumentationHygieneTests
{
    [Fact]
    public void CurrentReleaseFacingSurfacePassesTheHygieneGuard()
    {
        var result = RunGuard(FindRepositoryRoot());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Documentation hygiene passed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardRejectsAnAuthoredProcessTermAndExcludesItsOwnNegativeFixtures()
    {
        using var repository = new TemporaryRepository(Directory.CreateTempSubdirectory("resilience-doc-hygiene-"));
        Directory.CreateDirectory(Path.Combine(repository.Path, "scripts"));
        Directory.CreateDirectory(Path.Combine(repository.Path, "tests", "KeelMatrix.ResilienceSpec.Tests"));
        File.Copy(
            Path.Combine(FindRepositoryRoot(), "scripts", "Validate-DocumentationHygiene.ps1"),
            Path.Combine(repository.Path, "scripts", "Validate-DocumentationHygiene.ps1"));
        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "# Product\n\nThis contains an agent term.\n", Encoding.UTF8);

        var rejected = RunGuard(repository.Path);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("agent vocabulary", rejected.Output, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(repository.Path, "README.md"), "# Product\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(repository.Path, "scripts", "Validate-History.ps1"), "Paperclip\n", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(repository.Path, "tests", "KeelMatrix.ResilienceSpec.Tests", "HistoryGuardTests.cs"),
            "Paperclip\n",
            Encoding.UTF8);

        var excluded = RunGuard(repository.Path);
        Assert.Equal(0, excluded.ExitCode);
    }

    private static ProcessResult RunGuard(string repositoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "scripts", "Validate-DocumentationHygiene.ps1"));
        startInfo.ArgumentList.Add("-RepositoryPath");
        startInfo.ArgumentList.Add(repositoryPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start pwsh.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), "Documentation hygiene process did not finish within 30 seconds.");
        return new ProcessResult(process.ExitCode, $"{output.Result}{Environment.NewLine}{error.Result}");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "Validate-DocumentationHygiene.ps1")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the documentation hygiene test.");
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
