using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace KeelMatrix.ResilienceSpec.Tests;

/// <summary>Keeps telemetry out of the development and validation loop.</summary>
internal static class TelemetrySuppression
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Suppress() =>
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
}

public sealed class SystemDelegatingTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) => TimeProvider.System.CreateTimer(callback, state, dueTime, period);
}

public sealed class FakeTimeProviderSubclass : FakeTimeProvider
{
}

/// <summary>Records the order in which handlers of one chain observed a request.</summary>
internal sealed class ChainObserver
{
    private readonly List<string> _events = new();
    private readonly object _gate = new();

    internal void Add(string value)
    {
        lock (_gate)
        {
            _events.Add(value);
        }
    }

    internal IReadOnlyList<string> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }
}

/// <summary>Records entry and exit of one position in the chain under test.</summary>
internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly ChainObserver _observer;
    private readonly string _name;

    internal RecordingHandler(ChainObserver observer, string name)
    {
        _observer = observer;
        _name = name;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _observer.Add($"{_name}:request");
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _observer.Add($"{_name}:response {(int)response.StatusCode}");
            return response;
        }
        catch (OperationCanceledException)
        {
            _observer.Add($"{_name}:canceled");
            throw;
        }
        catch (Exception exception)
        {
            _observer.Add($"{_name}:exception {exception.GetType().Name}");
            throw;
        }
    }
}

/// <summary>
/// A hand-written retry handler. It exists so the core package is exercised without any resilience library and so
/// retry predicates can be varied independently.
/// </summary>
internal sealed class RetryHandler : DelegatingHandler
{
    private readonly int _maximumRetries;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Func<HttpResponseMessage, bool>? _shouldRetryResponse;
    private readonly bool _retryExceptions;
    private readonly bool _retryUnsafeMethods;
    private readonly bool _honorRetryAfter;
    private readonly bool _yieldBeforeDelay;
    private readonly TaskCompletionSource? _retryStarted;
    private readonly TaskCompletionSource? _retryRelease;

    internal RetryHandler(
        int maximumRetries,
        TimeSpan delay,
        TimeProvider timeProvider,
        Func<HttpResponseMessage, bool>? shouldRetryResponse = null,
        bool retryExceptions = false,
        bool retryUnsafeMethods = true,
        bool honorRetryAfter = false,
        bool yieldBeforeDelay = false,
        TaskCompletionSource? retryStarted = null,
        TaskCompletionSource? retryRelease = null)
    {
        _maximumRetries = maximumRetries;
        _delay = delay;
        _timeProvider = timeProvider;
        _shouldRetryResponse = shouldRetryResponse;
        _retryExceptions = retryExceptions;
        _retryUnsafeMethods = retryUnsafeMethods;
        _honorRetryAfter = honorRetryAfter;
        _yieldBeforeDelay = yieldBeforeDelay;
        _retryStarted = retryStarted;
        _retryRelease = retryRelease;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var retry = attempt < _maximumRetries &&
                    CanRetry(request.Method) &&
                    _shouldRetryResponse is not null &&
                    _shouldRetryResponse(response);
                if (!retry)
                {
                    return response;
                }

                var delay = _honorRetryAfter && response.Headers.RetryAfter?.Delta is { } advertised
                    ? advertised
                    : _delay;
                response.Dispose();
                _retryStarted?.TrySetResult();
                if (_retryRelease is not null)
                {
                    await _retryRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_yieldBeforeDelay)
                {
                    await Task.Yield();
                }

                await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (_retryExceptions && IsTransient(exception) && attempt < _maximumRetries && CanRetry(request.Method))
            {
                await DelayAsync(_delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception is HttpRequestException or TimeoutException;

    private bool CanRetry(HttpMethod method) =>
        _retryUnsafeMethods ||
        HttpMethod.Get.Equals(method) ||
        HttpMethod.Head.Equals(method) ||
        HttpMethod.Options.Equals(method) ||
        HttpMethod.Trace.Equals(method);

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _timeProvider, cancellationToken);
}

/// <summary>
/// A hand-written retry handler whose predicate covers every exception, including harness failures such as
/// <see cref="ScriptExhaustedException"/>. A real catch-all retry configuration can therefore make far more attempts
/// than any script can describe, which is exactly the shape the bounded attempt-state contract must handle.
/// </summary>
internal sealed class RetryEveryExceptionHandler : DelegatingHandler
{
    private readonly int _maximumRetries;

    internal RetryEveryExceptionHandler(int maximumRetries) => _maximumRetries = maximumRetries;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < _maximumRetries)
            {
                await Task.Yield();
            }
        }
    }
}

/// <summary>Starts two terminal attempts inside one logical call to prove overlap remains a distinct failure.</summary>
internal sealed class ConcurrentAttemptHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = base.SendAsync(request, linked.Token);
        try
        {
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(first).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ObserveAsync(Task<HttpResponseMessage> task)
    {
        try
        {
            using var response = await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>A per-attempt timeout, expressed on the injected clock.</summary>
internal sealed class AttemptTimeoutHandler : DelegatingHandler
{
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource? _callerToCancelOnTimeout;

    internal AttemptTimeoutHandler(TimeSpan timeout, TimeProvider timeProvider, CancellationTokenSource? callerToCancelOnTimeout = null)
    {
        _timeout = timeout;
        _timeProvider = timeProvider;
        _callerToCancelOnTimeout = callerToCancelOnTimeout;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = base.SendAsync(request, attempt.Token);
        var expiry = Task.Delay(_timeout, _timeProvider, attempt.Token);
        var completed = await Task.WhenAny(send, expiry).ConfigureAwait(false);
        if (!ReferenceEquals(completed, send) && !cancellationToken.IsCancellationRequested)
        {
            await attempt.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(send).ConfigureAwait(false);

            // A caller that also cancels at this instant must stay observable, so the caller token is cancelled
            // before the timeout is reported.
            _callerToCancelOnTimeout?.Cancel();
            throw new TimeoutException("The attempt exceeded its per-attempt timeout.");
        }

        return await send.ConfigureAwait(false);
    }

    internal static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The abandoned work is observed only so its failure does not surface through an unobserved task.
        }
    }
}

/// <summary>A total-request timeout, expressed on the injected clock.</summary>
internal sealed class TotalTimeoutHandler : DelegatingHandler
{
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    internal TotalTimeoutHandler(TimeSpan timeout, TimeProvider timeProvider)
    {
        _timeout = timeout;
        _timeProvider = timeProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = base.SendAsync(request, total.Token);
        var expiry = Task.Delay(_timeout, _timeProvider, total.Token);
        var completed = await Task.WhenAny(send, expiry).ConfigureAwait(false);
        if (!ReferenceEquals(completed, send) && !cancellationToken.IsCancellationRequested)
        {
            await total.CancelAsync().ConfigureAwait(false);
            await AttemptTimeoutHandler.ObserveAsync(send).ConfigureAwait(false);
            throw new TimeoutException("The request exceeded its total timeout.");
        }

        return await send.ConfigureAwait(false);
    }
}

/// <summary>Turns an abandoned terminal attempt into an unrelated downstream failure.</summary>
internal sealed class AbandonThenThrowHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var abandoned = new CancellationTokenSource();
        var send = base.SendAsync(request, abandoned.Token);
        await abandoned.CancelAsync().ConfigureAwait(false);
        try
        {
            return await send.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("The downstream failed after an abandoned attempt.");
        }
    }
}

/// <summary>Surfaces an unrelated cancellation-shaped downstream failure without caller cancellation.</summary>
internal sealed class UnrelatedCancellationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new OperationCanceledException("The downstream reported an unrelated cancellation."));
}

/// <summary>Ignores cancellation until the test releases a late response.</summary>
internal sealed class IgnoreCancellationHandler : DelegatingHandler
{
    private readonly TaskCompletionSource<HttpResponseMessage> _lateResponse;

    internal IgnoreCancellationHandler(TaskCompletionSource<HttpResponseMessage> lateResponse) => _lateResponse = lateResponse;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await _lateResponse.Task.ConfigureAwait(false);
        }
    }
}

/// <summary>Turns a cancellation after the cleanup deadline into an observed late downstream fault.</summary>
internal sealed class LateFaultHandler : DelegatingHandler
{
    private readonly TaskCompletionSource _releaseFault;
    private readonly TaskCompletionSource _faultObserved;
    private int _calls;

    internal LateFaultHandler(TaskCompletionSource releaseFault, TaskCompletionSource faultObserved)
    {
        _releaseFault = releaseFault;
        _faultObserved = faultObserved;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) > 1)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _releaseFault.Task.ConfigureAwait(false);
            _faultObserved.TrySetResult();
            throw new InvalidOperationException("The downstream faulted after cancellation cleanup was bounded.");
        }
    }
}

/// <summary>Stalls one cancellation callback until the test releases it.</summary>
internal sealed class StalledCancellationHandler : DelegatingHandler
{
    private readonly TaskCompletionSource _cancellationStarted;
    private readonly TaskCompletionSource _releaseCancellation;

    internal StalledCancellationHandler(TaskCompletionSource cancellationStarted, TaskCompletionSource releaseCancellation)
    {
        _cancellationStarted = cancellationStarted;
        _releaseCancellation = releaseCancellation;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            var handler = (StalledCancellationHandler)state!;
            handler._cancellationStarted.TrySetResult();
            handler._releaseCancellation.Task.GetAwaiter().GetResult();
        }, this);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Holds a retry continuation after its injected timer fires until the scenario releases it.</summary>
internal sealed class DelayedPostTimerRetryHandler : DelegatingHandler
{
    private readonly TimeSpan _delay;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _timerFired;
    private readonly TaskCompletionSource _continuationRelease;
    private int _attempt;

    internal DelayedPostTimerRetryHandler(
        TimeSpan delay,
        TimeProvider timeProvider,
        TaskCompletionSource timerFired,
        TaskCompletionSource continuationRelease)
    {
        _delay = delay;
        _timeProvider = timeProvider;
        _timerFired = timerFired;
        _continuationRelease = continuationRelease;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _attempt) == 1)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            await Task.Delay(_delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            _timerFired.TrySetResult();
            await _continuationRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

internal static class Chains
{
    internal static readonly DateTimeOffset ClockStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static Uri RequestUri(string path) => new($"https://orders.invalid{path}");

    internal static ResilienceScenarioClock CreateClock()
    {
        var inner = new FakeTimeProvider(ClockStart);
        return new ResilienceScenarioClock(inner, inner.Advance);
    }

    /// <summary>Assembles a client whose chain is exactly the given handlers above the given terminal handler.</summary>
    internal static HttpClient CreateClient(HttpMessageHandler terminal, params DelegatingHandler[] handlers)
    {
        var pipeline = terminal;
        for (var index = handlers.Length - 1; index >= 0; index--)
        {
            handlers[index].InnerHandler = pipeline;
            pipeline = handlers[index];
        }

        return new HttpClient(pipeline, disposeHandler: false);
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path = "/orders/42")
    {
        var request = new HttpRequestMessage(method, RequestUri(path));
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Content = new StringContent("{\"order\":42}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    internal static HttpRequestMessage SecretRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, RequestUri("/orders/42?token=super-secret-query"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "super-secret-token");
        request.Headers.Add("Cookie", "session=super-secret-cookie");
        request.Content = new StringContent("{\"note\":\"super-secret-body\"}", Encoding.UTF8, "application/json");
        return request;
    }

    internal static bool IsRetryableStatus(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
}

/// <summary>Attaches content to the response so response disposal becomes observable.</summary>
internal sealed class ContentHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = new StringContent("scripted-ok", Encoding.UTF8, "text/plain");
        return response;
    }
}
