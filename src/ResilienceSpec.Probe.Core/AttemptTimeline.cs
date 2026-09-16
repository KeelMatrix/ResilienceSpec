namespace ResilienceSpec.Probe;

/// <summary>One handler observation, in the order the probe's instrumented handlers reported it.</summary>
public sealed record TimelineEntry(int Sequence, string Observer, string Method, string Detail);

/// <summary>Ordered observation log shared by every instrumented handler in one probe chain.</summary>
public sealed class AttemptTimeline
{
    private readonly List<TimelineEntry> _entries = new();
    private readonly object _gate = new();

    /// <summary>Appends an observation. The sequence number is the observation order across all handlers.</summary>
    public void Record(string observer, HttpRequestMessage request, string detail)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            _entries.Add(new TimelineEntry(_entries.Count + 1, observer, request.Method.Method, detail));
        }
    }

    public IReadOnlyList<TimelineEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>Counts the observations reported by one handler position.</summary>
    public int CountOf(string observer)
    {
        var count = 0;
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Observer, observer, StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Renders the observations as one line each, for probe output.</summary>
    public IReadOnlyList<string> Render()
    {
        var rendered = new List<string>();
        foreach (var entry in Snapshot())
        {
            rendered.Add($"#{entry.Sequence} {entry.Observer} {entry.Method} {entry.Detail}");
        }

        return rendered;
    }
}
