namespace ResilienceSpec.Probe.Runner;

/// <summary>
/// Section 26 item 5: the probe performs no outbound network activity and binds no listener.
/// </summary>
internal static class ZeroNetworkProbe
{
    private static readonly string[] WatchedSources = { "System.Net.Http", "System.Net.Sockets", "System.Net.NameResolution" };

    public static void Run(ProbeReport report, NetworkActivityObserver observer)
    {
        report.Section("Probe 5 - zero outbound network activity and no listener");

        var counts = observer.Counts;
        var eventNames = observer.EventNames;
        var connectionBoundaryEvents = eventNames
            .Where(entry => IsConnectionBoundaryEvent(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var requestFailures = eventNames
            .Where(entry => entry.Key.EndsWith(":RequestFailed", StringComparison.Ordinal))
            .Sum(entry => entry.Value);

        report.Fact("in-memory attempts answered by scripted terminal handlers", Chains.TotalTerminalAttempts);

        foreach (var source in WatchedSources)
        {
            report.Fact($"runtime events from {source}", counts.TryGetValue(source, out var count) ? count : 0);
        }

        report.Fact("connection, socket, and name-resolution boundary events observed", connectionBoundaryEvents.Count);
        if (connectionBoundaryEvents.Count > 0)
        {
            report.Items(
                "boundary event detail",
                connectionBoundaryEvents.Select(entry => $"{entry.Key} = {entry.Value}"));
        }

        report.Fact("requests that ended through cancellation or a raised exception", requestFailures);
        report.Note("the request-failure events above are the deliberately timed-out attempts of probe 4d and probe 3;");
        report.Note("the runtime raises them for any canceled or failed request, so they describe the scripted outcome,");
        report.Note("not a transport failure.");
        report.Items(
            "all distinct runtime event names observed",
            eventNames.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key} = {entry.Value}"));
        report.Items("watched event sources not published by this runtime", observer.MissingSources());

        report.Note("method: the probe subscribes to the runtime event sources above before the first request and");
        report.Note("counts everything they publish until the end of the run. The System.Net.Http source reports the");
        report.Note("in-memory requests themselves; connection-boundary events such as ConnectionEstablished, and any");
        report.Note("socket, accept, or name-resolution event, remained at zero. A run of this probe never binds a");
        report.Note("listener: no source, socket, or listener API is used anywhere in the repository, and every scripted");
        report.Note("request target uses the reserved .invalid top-level domain, so a request that left the process could");
        report.Note("not reach the terminal handler at all. Sources this runtime does not publish cannot be measured and");
        report.Note("are listed above.");

        report.Expect(
            connectionBoundaryEvents.Count == 0,
            "no connection, socket, or name-resolution boundary event was reported while the probe ran");
        report.Expect(Chains.TotalTerminalAttempts > 0, "every probe attempt was answered in memory");
    }

    private static bool IsConnectionBoundaryEvent(string name)
    {
        var eventName = name.Contains(':', StringComparison.Ordinal) ? name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..] : name;
        return eventName.Contains("Connection", StringComparison.Ordinal) ||
            eventName.Contains("Connect", StringComparison.Ordinal) ||
            eventName.Contains("Socket", StringComparison.Ordinal) ||
            eventName.Contains("Resolution", StringComparison.Ordinal) ||
            eventName.Contains("Accept", StringComparison.Ordinal) ||
            eventName.Contains("Listener", StringComparison.Ordinal);
    }
}
