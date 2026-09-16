using System.Diagnostics.Tracing;

namespace ResilienceSpec.Probe;

/// <summary>
/// Counts runtime network events while the probe runs. The probe is expected never to resolve a host, open a
/// socket, or connect to anything; this observer is the in-process measurement that supports that claim.
/// </summary>
public sealed class NetworkActivityObserver : EventListener
{
    private static readonly string[] WatchedSources =
    {
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.NameResolution",
    };

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabledSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _eventNames = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Distinct event names observed so far, with their counts. Bounded to keep the observer small.</summary>
    public IReadOnlyDictionary<string, int> EventNames
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_eventNames, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Event counts per runtime event source, keyed by source name.</summary>
    public IReadOnlyDictionary<string, int> Counts
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_counts, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Watched event sources that this runtime did not publish.</summary>
    public IReadOnlyList<string> MissingSources()
    {
        var missing = new List<string>();
        lock (_gate)
        {
            foreach (var source in WatchedSources)
            {
                if (!_enabledSources.Contains(source))
                {
                    missing.Add(source);
                }
            }
        }

        return missing;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);

        var watched = false;
        foreach (var source in WatchedSources)
        {
            if (string.Equals(source, eventSource.Name, StringComparison.Ordinal))
            {
                watched = true;
                break;
            }
        }

        if (!watched)
        {
            return;
        }

        lock (_gate)
        {
            _enabledSources.Add(eventSource.Name);
        }

        EnableEvents(
            eventSource,
            EventLevel.Verbose,
            EventKeywords.All,
            new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var source = eventData.EventSource.Name;
        lock (_gate)
        {
            _counts.TryGetValue(source, out var count);
            _counts[source] = count + 1;
            var key = $"{source}:{eventData.EventName ?? "event"}";
            if (_eventNames.TryGetValue(key, out var nameCount))
            {
                _eventNames[key] = nameCount + 1;
            }
            else if (_eventNames.Count < 64)
            {
                _eventNames[key] = 1;
            }
        }
    }
}
