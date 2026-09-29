using System.Xml.Linq;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class TelemetryDocumentationTests
{
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

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
