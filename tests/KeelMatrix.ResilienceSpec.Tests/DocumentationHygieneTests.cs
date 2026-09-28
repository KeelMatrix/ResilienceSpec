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
    public void GuardScansEveryAuthoredFileWithoutExemptions()
    {
        using var repository = CreateRepository();
        var runtimeTerm = FromCodePoints(97, 103, 101, 110, 116);
        WriteText(repository.Path, "README.md", $"# Product\n\nThis contains a {runtimeTerm} term.\n", new UTF8Encoding(false));
        AddAll(repository.Path);

        var rejected = RunGuard(repository.Path);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("runtime vocabulary", rejected.Output, StringComparison.OrdinalIgnoreCase);

        WriteText(repository.Path, "README.md", "# Product\n", new UTF8Encoding(false));
        var processName = FromCodePoints(112, 97, 112, 101, 114, 99, 108, 105, 112);
        WriteText(repository.Path, "scripts/Validate-History.ps1", processName + "\n", new UTF8Encoding(false));
        WriteText(repository.Path, "tests/KeelMatrix.ResilienceSpec.Tests/HistoryGuardTests.cs", processName + "\n", new UTF8Encoding(false));
        AddAll(repository.Path);

        var everyFileIsScanned = RunGuard(repository.Path);
        Assert.NotEqual(0, everyFileIsScanned.ExitCode);
        Assert.Contains("non-product name", everyFileIsScanned.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ProhibitedTextEncodings))]
    public void ProhibitedTextFailsClosedAcrossRepresentativeEncodings(Encoding encoding, string extension)
    {
        using var repository = CreateRepository();
        var runtimeTerm = FromCodePoints(97, 103, 101, 110, 116);
        WriteText(repository.Path, $"fixtures/payload{extension}", runtimeTerm + "\n", encoding);
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runtime vocabulary", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> ProhibitedTextEncodings()
    {
        yield return new object[] { new UTF8Encoding(false), ".xml" };
        yield return new object[] { new UTF8Encoding(true), ".cmd" };
        yield return new object[] { new UnicodeEncoding(false, true), ".toml" };
        yield return new object[] { new UnicodeEncoding(true, true), ".html" };
        yield return new object[] { new ASCIIEncoding(), ".csv" };
        yield return new object[] { new UTF8Encoding(false), ".bat" };
        yield return new object[] { new ASCIIEncoding(), ".ini" };
    }

    [Fact]
    public void PathsAndFileNamesFailClosedWithoutReadingAnExtensionAllowlist()
    {
        using var repository = CreateRepository();
        var runtimeTerm = FromCodePoints(97, 103, 101, 110, 116);
        var pathTerm = FromCodePoints(97, 103, 101, 110, 116) + "-notes";
        WriteText(repository.Path, "fixtures/generated/payload.xml", runtimeTerm + "\n", new UTF8Encoding(false));
        WriteText(repository.Path, "fixtures/generated/payload", runtimeTerm + "\n", new UTF8Encoding(false));
        WriteText(repository.Path, $"docs/{pathTerm}.md", "# Product\n", new UTF8Encoding(false));
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runtime vocabulary", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path or file name", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegitimateGeneratedFixtureWorkflowEncodingsAndBinaryPass()
    {
        using var repository = CreateRepository();
        var safe = "Product fixture content.\n";
        WriteText(repository.Path, "fixtures/generated/payload.xml", safe, new UTF8Encoding(false));
        WriteText(repository.Path, ".github/workflows/generated.yml", safe, new UTF8Encoding(true));
        WriteText(repository.Path, "fixtures/odd name/[payload].toml", safe, new UnicodeEncoding(false, true));
        WriteText(repository.Path, "fixtures/ascii.csv", safe, new ASCIIEncoding());
        WriteText(repository.Path, "scripts/Validate-DocumentationHygiene.ps1", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Validate-DocumentationHygiene.ps1")), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(repository.Path, "fixtures", "binary.data"), new byte[] { 0x00, 0x01, 0xFF, 0x7F });
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Documentation hygiene passed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnclassifiableAuthoredContentFailsClosed()
    {
        using var repository = CreateRepository();
        Directory.CreateDirectory(Path.Combine(repository.Path, "fixtures"));
        File.WriteAllBytes(Path.Combine(repository.Path, "fixtures", "invalid.text"), new byte[] { 0xC3, 0x28 });
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("could not classify", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static string FromCodePoints(params int[] codePoints) =>
        new(codePoints.Select(static codePoint => (char)codePoint).ToArray());

    private static TemporaryRepository CreateRepository()
    {
        var directory = Directory.CreateTempSubdirectory("resilience-doc-hygiene-");
        Directory.CreateDirectory(Path.Combine(directory.FullName, "scripts"));
        Directory.CreateDirectory(Path.Combine(directory.FullName, "tests", "KeelMatrix.ResilienceSpec.Tests"));
        File.Copy(
            Path.Combine(FindRepositoryRoot(), "scripts", "Validate-DocumentationHygiene.ps1"),
            Path.Combine(directory.FullName, "scripts", "Validate-DocumentationHygiene.ps1"));
        WriteText(directory.FullName, "README.md", "# Product\n", new UTF8Encoding(false));
        InitializeGit(directory.FullName);
        AddAll(directory.FullName);
        return new TemporaryRepository(directory);
    }

    private static void WriteText(string repositoryPath, string relativePath, string text, Encoding encoding)
    {
        var path = Path.Combine(repositoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, encoding);
    }

    private static void InitializeGit(string repositoryPath)
    {
        RunProcess("git", ["init", "--quiet", "-b", "main", repositoryPath], repositoryPath).RequireSuccess("initialize the hygiene fixture");
        RunProcess("git", ["config", "user.name", "Fixture"], repositoryPath).RequireSuccess("configure the hygiene fixture name");
        RunProcess("git", ["config", "user.email", "fixture@example.invalid"], repositoryPath).RequireSuccess("configure the hygiene fixture email");
    }

    private static void AddAll(string repositoryPath) =>
        RunProcess("git", ["add", "--all"], repositoryPath).RequireSuccess("stage the hygiene fixture");

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

    private sealed record ProcessResult(int ExitCode, string Output)
    {
        public ProcessResult RequireSuccess(string operation)
        {
            Assert.Equal(0, ExitCode);
            return this;
        }
    }

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
