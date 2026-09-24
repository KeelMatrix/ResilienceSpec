using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

[CollectionDefinition("Release contract", DisableParallelization = true)]
public sealed class ReleaseContractTestGroup
{
}

[Collection("Release contract")]
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
        Assert.Contains("<SourceRoot Include=\"$(_RepositorySourceRoot)\"", buildTargets, StringComparison.Ordinal);

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
                var packageMatches = first.PackageHash == identity.PackageHash;
                var symbolsMatch = first.SymbolsHash == identity.SymbolsHash;
                Assert.True(
                    packageMatches && symbolsMatch,
                    $"Artifact identity differed between '{first.ShapeName}' and '{identity.ShapeName}'. " +
                    string.Join("; ", identities.Select(FormatIdentity)) +
                    $" Package entry differences: {FormatArchiveDifferences(first.PackagePath, identity.PackagePath)}." +
                    $" Symbols entry differences: {FormatArchiveDifferences(first.SymbolsPath, identity.SymbolsPath)}." +
                    $" PDB document differences: {FormatPdbDocumentDifferences(first.SymbolsPath, identity.SymbolsPath)}");
            }
        }
        finally
        {
            DeleteTemporaryTree(temporaryRoot);
        }
    }

    [Fact]
    public void StrictPackRejectsExplicitRevisionThatDiffersFromHeadBeforeWritingArchives()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expectedCommit = RunProcess(
            "git",
            new List<string> { "rev-parse", "HEAD" },
            repositoryRoot).RequireSuccess("resolve the repository commit").Output.Trim();
        var mismatchedCommit = "5a8d5eca75d6ac516892552cbc123df82b319443";
        Assert.NotEqual(expectedCommit, mismatchedCommit, StringComparer.OrdinalIgnoreCase);

        var temporaryRoot = Directory.CreateTempSubdirectory("resilience-pack-revision-contract-");
        try
        {
            var project = Path.Combine(repositoryRoot, "src", "KeelMatrix.ResilienceSpec", "KeelMatrix.ResilienceSpec.csproj");
            var packageDirectory = Path.Combine(temporaryRoot.FullName, "packages");
            Directory.CreateDirectory(packageDirectory);

            var restore = RunProcess(
                "dotnet",
                new[] { "restore", project, "--configfile", Path.Combine(repositoryRoot, "NuGet.config"), "-p:NuGetAudit=false" },
                repositoryRoot).RequireSuccess("restore the revision mismatch shape");

            var packArguments = new List<string>
            {
                "pack", project, "-c", "Release", "-warnaserror", "--no-restore",
                "-p:PackageVersion=0.1.0",
                $"-p:SourceRevisionId={mismatchedCommit}",
                $"-p:RepositoryCommit={mismatchedCommit}",
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
            var pack = RunProcess("dotnet", packArguments, repositoryRoot);
            var packagePath = Path.Combine(packageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.nupkg");
            var symbolsPath = Path.Combine(packageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.snupkg");
            var packageExists = File.Exists(packagePath);
            var symbolsExist = File.Exists(symbolsPath);

            Console.WriteLine(
                $"EXPLICIT_REVISION_MISMATCH HEAD={expectedCommit} EXPECTED={mismatchedCommit} " +
                $"RESTORE_EXIT={restore.ExitCode} PACK_EXIT={pack.ExitCode} " +
                $"PACKAGE_EXISTS={packageExists} SYMBOLS_EXISTS={symbolsExist}");

            Assert.NotEqual(0, pack.ExitCode);
            Assert.Contains("does not match", pack.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(packageExists);
            Assert.False(symbolsExist);
        }
        finally
        {
            DeleteTemporaryTree(temporaryRoot);
        }
    }

    [Fact]
    public void StrictPackMismatchRemovesStaleArchivesFromOutputDirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expectedCommit = RunProcess(
            "git",
            new List<string> { "rev-parse", "HEAD" },
            repositoryRoot).RequireSuccess("resolve the repository commit").Output.Trim();
        var mismatchedCommit = "5a8d5eca75d6ac516892552cbc123df82b319443";
        Assert.NotEqual(expectedCommit, mismatchedCommit, StringComparer.OrdinalIgnoreCase);

        var temporaryRoot = Directory.CreateTempSubdirectory("resilience-stale-archive-contract-");
        try
        {
            var project = Path.Combine(repositoryRoot, "src", "KeelMatrix.ResilienceSpec", "KeelMatrix.ResilienceSpec.csproj");
            var packageDirectory = Path.Combine(temporaryRoot.FullName, "packages");
            Directory.CreateDirectory(packageDirectory);

            var restore = RunProcess(
                "dotnet",
                new[] { "restore", project, "--configfile", Path.Combine(repositoryRoot, "NuGet.config"), "-p:NuGetAudit=false" },
                repositoryRoot).RequireSuccess("restore the stale archive shape");
            var goodPack = RunProcess(
                "dotnet",
                StrictPackArguments(project, expectedCommit, packageDirectory),
                repositoryRoot);
            var unrelatedFile = Path.Combine(packageDirectory, "unrelated.txt");
            File.WriteAllText(unrelatedFile, "keep");

            var badPack = RunProcess(
                "dotnet",
                StrictPackArguments(project, mismatchedCommit, packageDirectory),
                repositoryRoot);
            var nupkgCount = Directory.EnumerateFiles(packageDirectory, "*.nupkg").Count();
            var snupkgCount = Directory.EnumerateFiles(packageDirectory, "*.snupkg").Count();

            Console.WriteLine(
                $"F1_STALE_ARCHIVE RESTORE_EXIT={restore.ExitCode} GOOD_PACK_EXIT={goodPack.ExitCode} " +
                $"BAD_PACK_EXIT={badPack.ExitCode} NUPKG_COUNT={nupkgCount} SNUPKG_COUNT={snupkgCount} " +
                $"UNRELATED_EXISTS={File.Exists(unrelatedFile)}");

            Assert.Equal(0, goodPack.ExitCode);
            Assert.NotEqual(0, badPack.ExitCode);
            Assert.Contains("does not match", badPack.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, nupkgCount);
            Assert.Equal(0, snupkgCount);
            Assert.True(File.Exists(unrelatedFile));
        }
        finally
        {
            DeleteTemporaryTree(temporaryRoot);
        }
    }

    [Fact]
    public void StrictPackIgnoresGitEnvironmentForGitlessSourceRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expectedCommit = RunProcess(
            "git",
            new List<string> { "rev-parse", "HEAD" },
            repositoryRoot).RequireSuccess("resolve the repository commit").Output.Trim();

        var temporaryRoot = Directory.CreateTempSubdirectory("resilience-git-environment-contract-");
        var canonicalGitPath = Path.Combine(repositoryRoot, ".git");
        var savedGitPath = Path.Combine(repositoryRoot, $".git-resilience-pack-test-backup-{Guid.NewGuid():N}");
        var metadataMoved = false;
        try
        {
            var externalClone = Path.Combine(temporaryRoot.FullName, "external-git");
            var normalPackageDirectory = Path.Combine(temporaryRoot.FullName, "normal");
            var gitlessPackageDirectory = Path.Combine(temporaryRoot.FullName, "gitless");
            Directory.CreateDirectory(normalPackageDirectory);
            Directory.CreateDirectory(gitlessPackageDirectory);

            RunProcess(
                "git",
                new[] { "clone", "--no-checkout", "--no-hardlinks", "--quiet", repositoryRoot, externalClone },
                Directory.GetCurrentDirectory()).RequireSuccess("create the external Git probe clone");
            Assert.False(File.Exists(Path.Combine(externalClone, "icon.png")));
            var externalTree = RunProcess(
                "git",
                new[] { "--git-dir", Path.Combine(externalClone, ".git"), "rev-parse", "HEAD^{tree}" },
                repositoryRoot).RequireSuccess("resolve the external Git probe tree").Output.Trim();
            var externalRevision = RunProcess(
                "git",
                new[] { "--git-dir", Path.Combine(externalClone, ".git"), "commit-tree", externalTree, "-m", "external probe revision" },
                repositoryRoot,
                new Dictionary<string, string?>
                {
                    ["GIT_AUTHOR_NAME"] = "Probe",
                    ["GIT_AUTHOR_EMAIL"] = "probe@example.invalid",
                    ["GIT_COMMITTER_NAME"] = "Probe",
                    ["GIT_COMMITTER_EMAIL"] = "probe@example.invalid",
                    ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z",
                    ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
                }).RequireSuccess("create the external Git probe revision").Output.Trim();
            Assert.NotEqual(expectedCommit, externalRevision, StringComparer.OrdinalIgnoreCase);
            RunProcess(
                "git",
                new[] { "--git-dir", Path.Combine(externalClone, ".git"), "update-ref", "refs/heads/probe", externalRevision },
                repositoryRoot).RequireSuccess("point the external Git probe at the mismatched revision");
            RunProcess(
                "git",
                new[] { "--git-dir", Path.Combine(externalClone, ".git"), "symbolic-ref", "HEAD", "refs/heads/probe" },
                repositoryRoot).RequireSuccess("set the external Git probe HEAD");

            var project = Path.Combine(repositoryRoot, "src", "KeelMatrix.ResilienceSpec", "KeelMatrix.ResilienceSpec.csproj");
            var normalPack = RunProcess(
                "dotnet",
                StrictPackArguments(project, expectedCommit, normalPackageDirectory),
                repositoryRoot);

            MoveGitMetadata(canonicalGitPath, savedGitPath);
            metadataMoved = true;
            try
            {
                var gitEnvironment = new Dictionary<string, string?>
                {
                    ["GIT_DIR"] = Path.Combine(externalClone, ".git")
                };
                var gitProbe = RunProcess(
                    "git",
                    new[] { "-C", repositoryRoot, "rev-parse", "--verify", "HEAD" },
                    repositoryRoot,
                    gitEnvironment);
                var gitlessPack = RunProcess(
                    "dotnet",
                    StrictPackArguments(project, expectedCommit, gitlessPackageDirectory),
                    repositoryRoot,
                    gitEnvironment);

                Assert.Equal(0, normalPack.ExitCode);
                Assert.Equal(0, gitProbe.ExitCode);
                Assert.Equal(externalRevision, gitProbe.Output.Trim(), StringComparer.OrdinalIgnoreCase);
                Assert.Equal(0, gitlessPack.ExitCode);
            }
            finally
            {
                MoveGitMetadata(savedGitPath, canonicalGitPath);
                metadataMoved = false;
            }

            var normalizeScript = Path.Combine(repositoryRoot, "scripts", "Normalize-PackageArchive.ps1");
            var archivePaths = new[]
            {
                Path.Combine(normalPackageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.nupkg"),
                Path.Combine(normalPackageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.snupkg"),
                Path.Combine(gitlessPackageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.nupkg"),
                Path.Combine(gitlessPackageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.snupkg")
            };
            foreach (var archivePath in archivePaths)
            {
                RunProcess(
                    "pwsh",
                    new[] { "-NoProfile", "-File", normalizeScript, "-PackagePath", archivePath },
                    repositoryRoot).RequireSuccess($"normalize '{archivePath}'");
            }

            var normalPackageHash = HashFile(archivePaths[0]);
            var normalSymbolsHash = HashFile(archivePaths[1]);
            var gitlessPackageHash = HashFile(archivePaths[2]);
            var gitlessSymbolsHash = HashFile(archivePaths[3]);
            var nupkgCount = Directory.EnumerateFiles(gitlessPackageDirectory, "*.nupkg").Count();
            var snupkgCount = Directory.EnumerateFiles(gitlessPackageDirectory, "*.snupkg").Count();

            Console.WriteLine(
                $"F2_GIT_DIR EXTERNAL_REVISION={externalRevision} NORMAL_PACK_EXIT={normalPack.ExitCode} GIT_PROBE_EXIT=0 GITLESS_PACK_EXIT=0 " +
                $"NORMAL_PACKAGE_SHA256={normalPackageHash} NORMAL_SYMBOLS_SHA256={normalSymbolsHash} " +
                $"GITLESS_PACKAGE_SHA256={gitlessPackageHash} GITLESS_SYMBOLS_SHA256={gitlessSymbolsHash} " +
                $"NUPKG_COUNT={nupkgCount} SNUPKG_COUNT={snupkgCount}");

            Assert.Equal(normalPackageHash, gitlessPackageHash);
            Assert.Equal(normalSymbolsHash, gitlessSymbolsHash);
            Assert.Equal(1, nupkgCount);
            Assert.Equal(1, snupkgCount);
        }
        finally
        {
            if (metadataMoved)
            {
                MoveGitMetadata(savedGitPath, canonicalGitPath);
            }

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

        var restore = RunProcess(
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
        var pack = RunProcess("dotnet", packArguments, repositoryRoot)
            .RequireSuccess($"strict-pack the {shapeName} shape");

        var packagePath = Path.Combine(packageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.nupkg");
        var symbolsPath = Path.Combine(packageDirectory, "KeelMatrix.ResilienceSpec.0.1.0.snupkg");
        var sourceLinkPath = Path.Combine(
            repositoryRoot,
            "src",
            "KeelMatrix.ResilienceSpec",
            "obj",
            "Release",
            "net8.0",
            "KeelMatrix.ResilienceSpec.sourcelink.json");
        var normalizeScript = Path.Combine(sourceRepositoryRoot, "scripts", "Normalize-PackageArchive.ps1");
        RunProcess("pwsh", new[] { "-NoProfile", "-File", normalizeScript, "-PackagePath", packagePath }, repositoryRoot)
            .RequireSuccess($"normalize the {shapeName} package");
        RunProcess("pwsh", new[] { "-NoProfile", "-File", normalizeScript, "-PackagePath", symbolsPath }, repositoryRoot)
            .RequireSuccess($"normalize the {shapeName} symbols");

        Console.WriteLine($"ORIGIN_SHAPE={shapeName} RESTORE_EXIT={restore.ExitCode} PACK_EXIT={pack.ExitCode}");

        return new ArtifactIdentity(
            shapeName,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(symbolsPath))),
            packagePath,
            symbolsPath,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceLinkPath))));
    }

    private static List<string> StrictPackArguments(string project, string revision, string packageDirectory) =>
        new()
        {
            "pack", project, "-c", "Release", "-warnaserror", "--no-restore",
            "-p:PackageVersion=0.1.0",
            $"-p:SourceRevisionId={revision}",
            $"-p:RepositoryCommit={revision}",
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

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void MoveGitMetadata(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static string FormatIdentity(ArtifactIdentity identity) =>
        $"{identity.ShapeName}:package={identity.PackageHash},symbols={identity.SymbolsHash},sourcelink={identity.SourceLinkHash}";

    private static string FormatArchiveDifferences(string expectedPath, string actualPath)
    {
        using var expected = ZipFile.OpenRead(expectedPath);
        using var actual = ZipFile.OpenRead(actualPath);
        var expectedEntries = GetArchiveEntryHashes(expected);
        var actualEntries = GetArchiveEntryHashes(actual);
        return string.Join(
            ", ",
            expectedEntries.Keys
                .Union(actualEntries.Keys, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Where(name => !expectedEntries.TryGetValue(name, out var expectedHash) ||
                    !actualEntries.TryGetValue(name, out var actualHash) ||
                    !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                .Select(name => $"{name}={expectedEntries.GetValueOrDefault(name, "missing")}->{actualEntries.GetValueOrDefault(name, "missing")}"));
    }

    private static Dictionary<string, string> GetArchiveEntryHashes(ZipArchive archive) =>
        archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry => Convert.ToHexString(SHA256.HashData(ReadArchiveEntry(entry))),
            StringComparer.Ordinal);

    private static byte[] ReadArchiveEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string FormatPdbDocumentDifferences(string expectedPath, string actualPath)
    {
        var expected = ReadPdbDocuments(expectedPath);
        var actual = ReadPdbDocuments(actualPath);
        return string.Join(
            ", ",
            expected.Keys
                .Union(actual.Keys, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Where(name => !expected.TryGetValue(name, out var expectedRecord) ||
                    !actual.TryGetValue(name, out var actualRecord) ||
                    !string.Equals(expectedRecord, actualRecord, StringComparison.Ordinal))
                .Select(name => $"{name}={expected.GetValueOrDefault(name, "missing")}->{actual.GetValueOrDefault(name, "missing")}"));
    }

    private static Dictionary<string, string> ReadPdbDocuments(string symbolsPath)
    {
        using var archive = ZipFile.OpenRead(symbolsPath);
        var pdbEntry = archive.Entries.Single(entry => entry.FullName.EndsWith(".pdb", StringComparison.Ordinal));
        using var stream = new MemoryStream(ReadArchiveEntry(pdbEntry));
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        return reader.Documents.ToDictionary(
            handle => DecodeDocumentName(reader, reader.GetDocument(handle).Name),
            handle =>
            {
                var document = reader.GetDocument(handle);
                return $"algorithm={document.HashAlgorithm},hash={Convert.ToHexString(reader.GetBlobBytes(document.Hash))}";
            },
            StringComparer.Ordinal);
    }

    private static string DecodeDocumentName(MetadataReader reader, BlobHandle nameHandle)
    {
        var blob = reader.GetBlobReader(nameHandle);
        var separator = (char)blob.ReadByte();
        var segments = new List<string>();
        while (blob.RemainingBytes > 0)
        {
            segments.Add(Encoding.UTF8.GetString(reader.GetBlobBytes(blob.ReadBlobHandle())));
        }

        return string.Join(separator, segments);
    }

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

    private static ProcessResult RunProcess(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
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
        if (environmentVariables is not null)
        {
            foreach (var variable in environmentVariables)
            {
                startInfo.Environment[variable.Key] = variable.Value ?? string.Empty;
            }
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

    private sealed record ArtifactIdentity(
        string ShapeName,
        string PackageHash,
        string SymbolsHash,
        string PackagePath,
        string SymbolsPath,
        string SourceLinkHash);

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
