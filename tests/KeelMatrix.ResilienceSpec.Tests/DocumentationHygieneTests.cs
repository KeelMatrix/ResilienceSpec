using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
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
    public void NulRichUnicodeAndAuthoredPathVariantsAreScanned()
    {
        using var repository = CreateRepository();
        var runtimeTerm = FromCodePoints(97, 103, 101, 110, 116);
        var utf8 = new UTF8Encoding(false);

        WriteBytes(repository.Path, "fixtures/nul-after.txt", utf8.GetBytes(runtimeTerm + "\0 tail"));
        WriteBytes(repository.Path, "fixtures/nul-before.txt", utf8.GetBytes("head\0" + runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf16-le", new UnicodeEncoding(false, false).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf16-be", new UnicodeEncoding(true, false).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf32-le", new UTF32Encoding(false, false).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf32-be", new UTF32Encoding(true, false).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf16-le-bom", new UnicodeEncoding(false, true).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf16-be-bom", new UnicodeEncoding(true, true).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf32-le-bom", new UTF32Encoding(false, true).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/utf32-be-bom", new UTF32Encoding(true, true).GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "fixtures/generated/mixed-control", [1, 0, .. utf8.GetBytes(runtimeTerm), 0, 31]);
        WriteBytes(repository.Path, "fixtures/odd name/[payload]", utf8.GetBytes(runtimeTerm));
        WriteBytes(repository.Path, ".fixture", utf8.GetBytes(runtimeTerm));
        WriteBytes(repository.Path, ".github/workflows/payload.yml", utf8.GetBytes(runtimeTerm));
        WriteBytes(repository.Path, "docs/payload.xml", utf8.GetBytes(runtimeTerm));
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("runtime vocabulary", result.Output, StringComparison.OrdinalIgnoreCase);
        foreach (var path in new[]
        {
            "fixtures/nul-after.txt",
            "fixtures/nul-before.txt",
            "fixtures/utf16-le",
            "fixtures/utf16-be",
            "fixtures/utf32-le",
            "fixtures/utf32-be",
            "fixtures/utf16-le-bom",
            "fixtures/utf16-be-bom",
            "fixtures/utf32-le-bom",
            "fixtures/utf32-be-bom",
            "fixtures/generated/mixed-control",
            "fixtures/odd name/[payload]",
            ".fixture",
            ".github/workflows/payload.yml",
            "docs/payload.xml",
        })
        {
            Assert.Contains(path, result.Output, StringComparison.Ordinal);
        }
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
    public void LegitimateGeneratedFixtureWorkflowEncodingsAndDeclaredBinaryPass()
    {
        using var repository = CreateRepository();
        var safe = "Product fixture content.\n";
        WriteText(repository.Path, "fixtures/generated/payload.xml", safe, new UTF8Encoding(false));
        WriteText(repository.Path, ".github/workflows/generated.yml", safe, new UTF8Encoding(true));
        WriteText(repository.Path, "fixtures/odd name/[payload].toml", safe, new UnicodeEncoding(false, true));
        WriteText(repository.Path, "fixtures/ascii.csv", safe, new ASCIIEncoding());
        WriteText(repository.Path, "scripts/Validate-DocumentationHygiene.ps1", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Validate-DocumentationHygiene.ps1")), new UTF8Encoding(false));
        WriteBytes(repository.Path, "fixtures/valid.data", CreateValidPng());
        WriteBinaryManifest(repository.Path, "fixtures/valid.data");
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Documentation hygiene passed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredBinaryPathStillScansSuccessfulTextDecodes()
    {
        using var repository = CreateRepository();
        var runtimeTerm = FromCodePoints(97, 103, 101, 110, 116);
        WriteText(repository.Path, "fixtures/declared.data", $"Product {runtimeTerm}\n", new UTF8Encoding(false));
        WriteBinaryManifest(repository.Path, "fixtures/declared.data");
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("fixtures/declared.data", result.Output, StringComparison.Ordinal);
        Assert.Contains("runtime vocabulary", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SpoofedBinaryPayloads))]
    public void SpoofedBinaryPayloadsFailClosed(string relativePath, byte[] bytes)
    {
        using var repository = CreateRepository();
        WriteBytes(repository.Path, relativePath, bytes);
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(relativePath, result.Output, StringComparison.Ordinal);
        Assert.True(
            result.Output.Contains("could not decode", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("runtime vocabulary", StringComparison.OrdinalIgnoreCase),
            result.Output);
    }

    public static IEnumerable<object[]> SpoofedBinaryPayloads()
    {
        var runtimeTerm = FromCodePoints(97, 103, 101, 110, 116);
        var payload = new UTF8Encoding(false).GetBytes($"\n{runtimeTerm}\n");
        var signatures = new (string Name, byte[] Bytes)[]
        {
            ("png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ("jpeg", [0xFF, 0xD8, 0xFF]),
            ("gzip", [0x1F, 0x8B]),
            ("pdf", [0x25, 0x50, 0x44, 0x46]),
            ("zip", [0x50, 0x4B, 0x03, 0x04]),
        };

        foreach (var signature in signatures)
        {
            yield return new object[] { $"fixtures/spoofed-{signature.Name}-after", Combine(signature.Bytes, payload) };
            yield return new object[] { $"fixtures/spoofed-{signature.Name}-before", Combine(payload, signature.Bytes) };
        }

        yield return new object[] { "fixtures/invalid-utf8-controls", Combine([0xFF, 0x01], payload) };
        yield return new object[] { "fixtures/invalid-utf8-controls-after", Combine(payload, [0xFF, 0x01]) };
        yield return new object[] { "fixtures/nul-before", Combine([0x00], payload) };
        yield return new object[] { "fixtures/nul-after", Combine(payload, [0x00]) };
        yield return new object[] { "fixtures/mixed-control-nul", Combine([0x01, 0x00], payload, [0x00, 0x1F]) };
        yield return new object[] { "docs/payload.xml", payload };
        yield return new object[] { ".fixture", payload };
        yield return new object[] { "fixtures/generated/payload", payload };
        yield return new object[] { ".github/workflows/payload.yml", payload };
        yield return new object[] { "fixtures/odd name/[payload]", payload };

        yield return new object[] { "fixtures/utf16-le", new UnicodeEncoding(false, false).GetBytes(runtimeTerm) };
        yield return new object[] { "fixtures/utf16-be", new UnicodeEncoding(true, false).GetBytes(runtimeTerm) };
        yield return new object[] { "fixtures/utf32-le", new UTF32Encoding(false, false).GetBytes(runtimeTerm) };
        yield return new object[] { "fixtures/utf32-be", new UTF32Encoding(true, false).GetBytes(runtimeTerm) };
        yield return new object[] { "fixtures/utf16-le-bom", new UnicodeEncoding(false, true).GetBytes(runtimeTerm) };
        yield return new object[] { "fixtures/utf16-be-bom", new UnicodeEncoding(true, true).GetBytes(runtimeTerm) };
        yield return new object[] { "fixtures/utf32-le-bom", new UTF32Encoding(false, true).GetBytes(runtimeTerm) };
        yield return new object[] { "fixtures/utf32-be-bom", new UTF32Encoding(true, true).GetBytes(runtimeTerm) };
    }

    [Theory]
    [MemberData(nameof(InvalidDeclaredBinaryAssets))]
    public void InvalidOrTruncatedDeclaredBinaryAssetsFailClosed(string relativePath, byte[] bytes)
    {
        using var repository = CreateRepository();
        WriteBytes(repository.Path, relativePath, bytes);
        WriteBinaryManifest(repository.Path, relativePath);
        AddAll(repository.Path);

        var result = RunGuard(repository.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(relativePath, result.Output, StringComparison.Ordinal);
        Assert.Contains("rejected declared binary asset", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidDeclaredBinaryAssets()
    {
        var valid = CreateValidPng();
        yield return new object[] { "fixtures/truncated.data", valid[..^12] };
        yield return new object[] { "fixtures/invalid-signature.data", Combine([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x00]) };

        var invalidCrc = valid.ToArray();
        invalidCrc[invalidCrc.Length - 5] ^= 0x01;
        yield return new object[] { "fixtures/invalid-crc.data", invalidCrc };
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
        Assert.Contains("could not decode", result.Output, StringComparison.OrdinalIgnoreCase);
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
        WriteBinaryManifest(directory.FullName);
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

    private static void WriteBytes(string repositoryPath, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(repositoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteBinaryManifest(string repositoryPath, params string[] paths)
    {
        var assets = string.Join(",\n", paths.Select(path => $"    {{\"path\": \"{path}\", \"format\": \"png\"}}"));
        WriteText(repositoryPath, "scripts/DocumentationBinaryManifest.json", $"{{\n  \"version\": 1,\n  \"assets\": [\n{assets}\n  ]\n}}\n", new UTF8Encoding(false));
    }

    private static byte[] CreateValidPng()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(output, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write([0, 0, 0, 0, 0]);
        }

        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(data);

        var crcInput = Combine(typeBytes, data);
        var crc = ComputeCrc32(crcInput);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputeCrc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static byte[] Combine(params byte[][] parts) => parts.SelectMany(static part => part).ToArray();

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
