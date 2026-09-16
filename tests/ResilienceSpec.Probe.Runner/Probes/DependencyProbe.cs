using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;

namespace ResilienceSpec.Probe.Runner;

/// <summary>Probe 6: runtime facts and the versions of the assemblies this run actually loaded.</summary>
internal static class DependencyProbe
{
    private static readonly string[] RelevantPrefixes = { "Polly", "Microsoft.Extensions" };

    public static void Run(ProbeReport report)
    {
        report.Section("Probe 6 - environment and loaded dependency versions");
        report.Fact("runtime", RuntimeInformation.FrameworkDescription);
        report.Fact("operating system", RuntimeInformation.OSDescription);
        report.Fact("process architecture", RuntimeInformation.ProcessArchitecture);
        report.Fact("environment version", Environment.Version);
        report.Fact("time zone of the probe host", TimeZoneInfo.Local.Id);
        report.Items("relevant loaded assemblies", RelevantAssemblies().Select(Describe));
        report.Note("the probe project references Microsoft.Extensions.Http.Resilience and Microsoft.Extensions.TimeProvider.Testing");
        report.Note("as probe-only development dependencies; the in-memory terminal handler itself uses only System.Net.Http.");
        report.Note("no public or internal member is accessed through reflection anywhere in this probe.");
    }

    private static IEnumerable<Assembly> RelevantAssemblies()
    {
        var assemblies = new List<Assembly>
        {
            typeof(HttpStandardResilienceOptions).Assembly,
            typeof(ResiliencePipeline).Assembly,
            typeof(FakeTimeProvider).Assembly,
            typeof(HttpClient).Assembly,
            typeof(ServiceCollection).Assembly,
        };

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            var name = assembly.GetName().Name;
            if (name is null)
            {
                continue;
            }

            if (RelevantPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) ||
                string.Equals(name, "System.Net.Http", StringComparison.Ordinal) ||
                string.Equals(name, "System.Diagnostics.DiagnosticSource", StringComparison.Ordinal))
            {
                assemblies.Add(assembly);
            }
        }

        return assemblies
            .Distinct()
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal);
    }

    private static string Describe(Assembly assembly)
    {
        var name = assembly.GetName();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(none)";
        return $"{name.Name} assembly-version {name.Version} informational-version {informational}";
    }
}
