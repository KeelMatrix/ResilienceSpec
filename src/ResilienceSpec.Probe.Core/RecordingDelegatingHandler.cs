namespace ResilienceSpec.Probe;

/// <summary>
/// Probe-only delegating handler that records when a request enters its position of the application handler
/// chain and when a response, a timeout, a cancellation, or an exception leaves it.
/// </summary>
public sealed class RecordingDelegatingHandler : DelegatingHandler
{
    private readonly AttemptTimeline _timeline;

    public RecordingDelegatingHandler(string observer, AttemptTimeline timeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observer);
        ArgumentNullException.ThrowIfNull(timeline);

        _timeline = timeline;
        Observer = observer;
    }

    /// <summary>The position name reported in timelines, for example <c>outer</c> or <c>inner</c>.</summary>
    public string Observer { get; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _timeline.Record(Observer, request, "request");
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _timeline.Record(Observer, request, $"response {(int)response.StatusCode}");
            return response;
        }
        catch (OperationCanceledException)
        {
            _timeline.Record(Observer, request, "canceled");
            throw;
        }
        catch (Exception exception)
        {
            _timeline.Record(Observer, request, $"exception {exception.GetType().Name}");
            throw;
        }
    }
}
