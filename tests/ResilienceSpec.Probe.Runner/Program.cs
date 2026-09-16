namespace ResilienceSpec.Probe.Runner;

internal static class Program
{
    private const string ChainProbe = "chain";
    private const string TruthProbe = "truth";
    private const string UnsafeProbe = "unsafe";
    private const string VirtualTimeProbeKey = "virtualtime";
    private const string RetryAfterProbeKey = "retryafter";
    private const string TimeoutProbeKey = "timeouts";
    private const string NetworkProbe = "network";
    private const string DependencyProbeKey = "versions";

    private static readonly string[] KnownProbes =
    {
        ChainProbe, TruthProbe, UnsafeProbe, VirtualTimeProbeKey, RetryAfterProbeKey, TimeoutProbeKey, NetworkProbe, DependencyProbeKey,
    };

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("ResilienceSpec Phase 0 feasibility probe");
        Console.WriteLine("Question: can a scripted in-memory terminal handler sit behind a real HttpClient handler chain, and can");
        Console.WriteLine("retry, Retry-After, and timeout timing be made deterministic through supported public APIs only?");
        Console.WriteLine("All requests are answered in memory; no socket, listener, DNS lookup, or wall-clock timing assertion is used.");
        Console.WriteLine();

        var unknown = args.Where(argument => !KnownProbes.Contains(argument, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine($"Unknown probe: {string.Join(", ", unknown)}");
            Console.Error.WriteLine($"Known probes: {string.Join(", ", KnownProbes)}");
            return 2;
        }

        var selected = args.Length == 0 ? null : new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        var report = new ProbeReport();
        using var observer = new NetworkActivityObserver();
        var cancellation = new CancellationTokenSource();

        try
        {
            if (IsSelected(selected, ChainProbe))
            {
                await HandlerChainProbe.RunAsync(report, cancellation.Token).ConfigureAwait(false);
            }

            if (IsSelected(selected, TruthProbe))
            {
                await AttemptTruthProbe.RunAsync(report, cancellation.Token).ConfigureAwait(false);
            }

            if (IsSelected(selected, UnsafeProbe))
            {
                await UnsafeMethodProbe.RunAsync(report, cancellation.Token).ConfigureAwait(false);
            }

            if (IsSelected(selected, VirtualTimeProbeKey))
            {
                await VirtualTimeProbe.RunAsync(report, cancellation.Token).ConfigureAwait(false);
            }

            if (IsSelected(selected, RetryAfterProbeKey))
            {
                await RetryAfterProbe.RunAsync(report, cancellation.Token).ConfigureAwait(false);
            }

            if (IsSelected(selected, TimeoutProbeKey))
            {
                await TimeoutProbe.RunAsync(report, cancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        if (IsSelected(selected, NetworkProbe))
        {
            ZeroNetworkProbe.Run(report, observer);
        }

        if (IsSelected(selected, DependencyProbeKey))
        {
            DependencyProbe.Run(report);
        }

        report.PrintSummary();
        return report.ExitCode;
    }

    private static bool IsSelected(HashSet<string>? selected, string probe) =>
        selected is null || selected.Contains(probe);
}
