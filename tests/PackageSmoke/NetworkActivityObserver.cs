using System.Diagnostics.Tracing;

namespace PackageSmoke;

/// <summary>
/// Counts runtime transport events so the smoke run can show whether any connection, socket, or name-resolution
/// boundary was reached. Sources the runtime never created are reported explicitly rather than presented as measured.
/// </summary>
internal sealed class NetworkActivityObserver : EventListener
{
    private static readonly string[] WatchedSources = ["System.Net.Sockets", "System.Net.NameResolution"];

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _events = new(StringComparer.Ordinal);
    private readonly List<string> _published = new();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        var name = eventSource.Name;
        if (name is null || !WatchedSources.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            _published.Add(name);
            _events[name] = 0;
        }

        EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        var name = eventData.EventSource.Name;
        if (name is null)
        {
            return;
        }

        lock (_gate)
        {
            _events[name] = _events.TryGetValue(name, out var count) ? count + 1 : 1;
        }
    }

    internal long TotalEvents
    {
        get
        {
            lock (_gate)
            {
                return _events.Values.Sum();
            }
        }
    }

    internal IReadOnlyList<string> UncreatedSources
    {
        get
        {
            lock (_gate)
            {
                return WatchedSources.Where(name => !_published.Contains(name, StringComparer.Ordinal)).ToArray();
            }
        }
    }

    internal IReadOnlyList<string> DescribeEvents()
    {
        lock (_gate)
        {
            return WatchedSources
                .Select(name => $"{name}: {(_events.TryGetValue(name, out var count) ? count : 0)} event(s)")
                .ToArray();
        }
    }
}
