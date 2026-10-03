using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class TelemetryDocumentationTests
{
    private const string ExceptionFaultPolicy = ScenarioTelemetry.Policy;

    [Fact]
    public void ActivationPolicyIsRestatedByShippedXmlAndDocumentation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var policy = Normalize(ScenarioTelemetry.ActivationPolicy);

        var xmlPath = Path.Combine(AppContext.BaseDirectory, "KeelMatrix.ResilienceSpec.xml");
        var xml = XDocument.Load(xmlPath);
        foreach (var typeName in new[]
        {
            "T:KeelMatrix.ResilienceSpec.ResilienceScenario",
            "T:KeelMatrix.ResilienceSpec.ResilienceScenarioOptions",
            "T:KeelMatrix.ResilienceSpec.ScenarioTelemetry",
        })
        {
            var member = xml.Root!
                .Element("members")!
                .Elements("member")
                .Single(element => string.Equals(
                    (string?)element.Attribute("name"),
                    typeName,
                    StringComparison.Ordinal));
            Assert.Contains(policy, Normalize(member.Value), StringComparison.Ordinal);
        }

        foreach (var relativePath in new[]
        {
            "PRIVACY.md",
            "README.md",
            "src/KeelMatrix.ResilienceSpec/README.md",
            "docs/DEV.md",
            "AGENTS.md",
            "CHANGELOG.md",
        })
        {
            var text = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            Assert.Contains(policy, Normalize(text), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExceptionFaultPolicyIsRestatedByEveryTelemetryXmlMemberAndDocument()
    {
        var repositoryRoot = FindRepositoryRoot();
        var policy = Normalize(ExceptionFaultPolicy);
        var xmlPath = Path.Combine(AppContext.BaseDirectory, "KeelMatrix.ResilienceSpec.xml");
        var xml = XDocument.Load(xmlPath);

        foreach (var memberName in TelemetryXmlMembers)
        {
            var member = xml.Root!
                .Element("members")!
                .Elements("member")
                .Single(element => string.Equals(
                    (string?)element.Attribute("name"),
                    memberName,
                    StringComparison.Ordinal));
            Assert.Contains(policy, Normalize(member.Value), StringComparison.Ordinal);
        }

        foreach (var relativePath in DocumentationPaths)
        {
            var text = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            Assert.Contains(policy, Normalize(text), StringComparison.Ordinal);
        }

        var changelog = File.ReadAllText(Path.Combine(repositoryRoot, "CHANGELOG.md"));
        var releaseHeading = Regex.Match(
            changelog,
            @"(?m)^## \[0\.1\.0\] - (?:Planned|\d{4}-\d{2}-\d{2})\s*$");
        Assert.True(releaseHeading.Success, "The changelog must contain the planned or finalized first-release heading.");
        Assert.Contains(policy, Normalize(changelog[releaseHeading.Index..]), StringComparison.Ordinal);
    }

    [Fact]
    public void AlteredExceptionFaultPolicyDoesNotPassTheDocumentationCheck()
    {
        var repositoryRoot = FindRepositoryRoot();
        var alteredPolicy = Normalize(ExceptionFaultPolicy.Replace(
            "positively recognized native HttpClient.Timeout outcome",
            "recognized native HttpClient.Timeout outcome",
            StringComparison.Ordinal));

        var xmlPath = Path.Combine(AppContext.BaseDirectory, "KeelMatrix.ResilienceSpec.xml");
        var xml = XDocument.Load(xmlPath);
        var xmlMembers = xml.Root!.Element("members")!.Elements("member");
        var xmlMatches = TelemetryXmlMembers.All(memberName => xmlMembers.Any(member =>
            string.Equals((string?)member.Attribute("name"), memberName, StringComparison.Ordinal) &&
            Normalize(member.Value).Contains(alteredPolicy, StringComparison.Ordinal)));

        var documentMatches = DocumentationPaths.All(relativePath =>
            Normalize(File.ReadAllText(Path.Combine(repositoryRoot, relativePath)))
                .Contains(alteredPolicy, StringComparison.Ordinal));

        Assert.False(xmlMatches && documentMatches);
    }

    private static readonly string[] DocumentationPaths =
    {
        "PRIVACY.md",
        "README.md",
        "src/KeelMatrix.ResilienceSpec/README.md",
        "docs/DEV.md",
        "AGENTS.md",
        "CHANGELOG.md",
        "SECURITY.md",
        "samples/KeelMatrix.ResilienceSpec.Sample/Program.cs",
        "tests/PackageSmoke/Program.cs",
    };

    private static readonly string[] TelemetryXmlMembers =
    {
        "T:KeelMatrix.ResilienceSpec.ResilienceScenario",
        "T:KeelMatrix.ResilienceSpec.ResilienceScenarioOptions",
        "T:KeelMatrix.ResilienceSpec.ScenarioTelemetry",
        "T:KeelMatrix.ResilienceSpec.ScenarioTelemetrySignal",
        "P:KeelMatrix.ResilienceSpec.ScenarioTelemetrySignal.ExceptionFault",
    };

    private static string Normalize(string value) =>
        string.Join(
            ' ',
            value
                .Replace("///", " ", StringComparison.Ordinal)
                .Replace("//", " ", StringComparison.Ordinal)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeelMatrix.ResilienceSpec.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the telemetry documentation test.");
    }
}
