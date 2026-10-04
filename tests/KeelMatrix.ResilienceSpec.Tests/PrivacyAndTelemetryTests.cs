using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

internal sealed class RecordingTelemetrySink : ITelemetrySink
{
    private readonly List<string> _requests = new();
    private readonly object _gate = new();

    public void TrackActivation()
    {
        lock (_gate)
        {
            _requests.Add("activation");
        }
    }

    public void TrackHeartbeat()
    {
        lock (_gate)
        {
            _requests.Add("heartbeat");
        }
    }

    internal IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }
}

public sealed class PrivacyTests
{
    private static readonly string[] SecretMarkers =
    [
        "super-secret-query",
        "super-secret-token",
        "super-secret-cookie",
        "super-secret-body",
        "orders.invalid",
        "/orders/42",
        "Bearer",
        "session=",
    ];

    [Fact]
    public async Task AttemptRecordsAndTimelinesCarryNoRequestIdentifyingData()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.SecretRequest();
        using var result = await scenario.SendAsync(client, request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        var observed = new List<string> { scenario.Report.DescribeTimeline() };
        foreach (var attempt in scenario.Report.Attempts)
        {
            foreach (var property in typeof(HttpAttempt).GetProperties())
            {
                observed.Add($"{property.Name}={property.GetValue(attempt)}");
            }
        }

        foreach (var line in observed)
        {
            AssertNothingSecret(line);
        }
    }

    [Fact]
    public async Task FailureMessagesCarryNoRequestIdentifyingData()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            clock);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.SecretRequest();
        using var result = await scenario.SendAsync(client, request);

        var attemptFailure = Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveAttempts(2));
        var statusFailure = Assert.Throws<ResilienceAssertionException>(() => result.ShouldHaveStatus(HttpStatusCode.OK));
        var sequenceFailure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveMethodSequence(HttpMethod.Post));

        AssertNothingSecret(attemptFailure.Message);
        AssertNothingSecret(statusFailure.Message);
        AssertNothingSecret(sequenceFailure.Message);
    }

    [Fact]
    public async Task ScriptedNetworkFailuresCarryAFixedMessage()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.NetworkError()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.SecretRequest();
        using var result = await scenario.SendAsync(client, request);

        var exception = Assert.IsType<HttpRequestException>(result.Exception);
        Assert.Equal("The scripted downstream reported a network failure.", exception.Message);
        AssertNothingSecret(exception.ToString());
    }

    private static void AssertNothingSecret(string value)
    {
        foreach (var marker in SecretMarkers)
        {
            Assert.DoesNotContain(marker, value, StringComparison.Ordinal);
        }
    }
}

public sealed class TelemetryTests
{
    private static readonly string[] SharedSignalRequest = ["activation", "heartbeat"];
    private static readonly string[] TelemetrySinkMethods = ["TrackActivation", "TrackHeartbeat"];

    [Fact]
    public void LocalValidationSuppressesTelemetry()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY"));
        Assert.Equal("ResilienceSpec", TelemetryHost.ToolName);
    }

    [Fact]
    public async Task ConstructingAndRunningAScenarioIsNotAnActivation()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task ASuccessfulScenarioIsNotAnActivation()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()), options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        scenario.Report.ShouldHaveAttempts(1);
        result.ShouldHaveStatus(HttpStatusCode.OK);

        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task UpstreamCustomTimeoutBeforeAnyAttemptDoesNotActivateTelemetry()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler, new TimeoutBeforeTerminalHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.IsType<TimeoutException>(result.Exception);
        Assert.Equal(0, result.Report.AttemptCount);
        Assert.True(result.Report.IsSettled);
        Assert.False(result.Report.IsObservationCutoff);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task UpstreamTimeoutDoesNotUsePlannedNonTimeoutStepsAsEvidence()
    {
        var scripts = new[]
        {
            HttpFaultScript.Sequence(HttpFault.Success()),
            HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromMilliseconds(1), HttpFault.Success())),
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            HttpFaultScript.Sequence(HttpFault.NetworkError()),
        };

        foreach (var script in scripts)
        {
            var sink = new RecordingTelemetrySink();
            using var scenario = new ResilienceScenario(script, Chains.CreateClock(), Options(sink));
            using var client = Chains.CreateClient(scenario.Handler, new TimeoutBeforeTerminalHandler());
            using var request = Chains.Request(HttpMethod.Get);

            using var result = await scenario.SendAsync(client, request);

            result.ShouldHaveKind(ResilienceResultKind.Timeout);
            Assert.Equal(0, result.Report.AttemptCount);
            Assert.Empty(result.Report.Attempts);
            Assert.Empty(sink.Requests);
        }
    }

    [Fact]
    public async Task CustomTimeoutAfterAResponseAttemptRetainsFailureEligibility()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler, new TimeoutAfterTerminalHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.Equal(1, result.Report.AttemptCount);
        Assert.Equal(HttpAttemptOutcome.Response, Assert.Single(result.Report.Attempts).Outcome);
        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task CustomTimeoutAfterADelayedSuccessDoesNotQualify()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromMilliseconds(1), HttpFault.Success())),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(scenario.Handler, new TimeoutAfterTerminalHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.Equal(1, result.Report.AttemptCount);
        Assert.Equal(HttpAttemptOutcome.Response, Assert.Single(result.Report.Attempts).Outcome);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task CustomTimeoutAfterANetworkErrorRetainsFailureEligibility()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.NetworkError()),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler, new TimeoutAfterTerminalHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.Equal(1, result.Report.AttemptCount);
        Assert.Equal(HttpAttemptOutcome.NetworkError, Assert.Single(result.Report.Attempts).Outcome);
        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task ForgedNativeShapedTimeoutAfterASuccessfulAttemptDoesNotQualify()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler, new NativeTimeoutAfterTerminalHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.Equal(1, result.Report.AttemptCount);
        Assert.Equal(HttpAttemptOutcome.Response, Assert.Single(result.Report.Attempts).Outcome);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task NativeShapedTimeoutWithoutAnAttemptDoesNotQualify()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler, new NativeTimeoutBeforeTerminalHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.Equal(0, result.Report.AttemptCount);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task CancelingBeforeADelayedFailureDoesNotActivateTelemetry()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromSeconds(2), HttpFault.NetworkError())),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(20),
                CleanupTimeout = TimeSpan.FromMilliseconds(40),
                TelemetrySink = sink,
            });
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.Equal(HttpAttemptOutcome.Abandoned, scenario.Report.Attempts[0].Outcome);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task CallerCancellationOfAnInjectedTimeoutDoesNotQualify()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(200),
                CleanupTimeout = TimeSpan.FromMilliseconds(200),
                TelemetrySink = sink,
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider));
        using var caller = new CancellationTokenSource();
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request, caller.Token);
        await scenario.Handler.Observer.WaitForFirstAttemptAsync();
        await caller.CancelAsync();

        using var result = await run;

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
        scenario.Report.ShouldHaveAttempts(1);
        Assert.Equal(HttpAttemptOutcome.Abandoned, Assert.Single(scenario.Report.Attempts).Outcome);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task AnInjectedFailureWithAnEvaluatedAssertionRequestsSharedSignals()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);

        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task RepeatedEligibleAssertionsAreForwardedToSharedSuppression()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        scenario.Report.ShouldHaveAttempts(1);
        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);

        AssertSharedSignalRequests(sink, 2);
    }

    [Theory]
    [InlineData(399, 0)]
    [InlineData(400, 1)]
    [InlineData(599, 1)]
    public async Task OnlyExecutedErrorResponsesQualifyForActivation(int statusCode, int expectedRequestPairs)
    {
        var sink = new RecordingTelemetrySink();
        var status = (HttpStatusCode)statusCode;
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(status)),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        Assert.Equal(1, result.Report.AttemptCount);
        result.ShouldHaveStatus(status);

        AssertSharedSignalRequests(sink, expectedRequestPairs);
    }

    [Fact]
    public async Task AnExecutedResponseFailureKeepsTheScenarioEligible()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success(),
                HttpFault.NetworkError()),
            options: Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, TimeProvider.System, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task UnusedLaterResponseAndTimeoutFaultsDoNotChangeActivationEligibility()
    {
        var responseSink = new RecordingTelemetrySink();
        using (var responseScenario = new ResilienceScenario(
                   HttpFaultScript.Sequence(
                       HttpFault.NetworkError(),
                       HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
                   options: Options(responseSink)))
        using (var responseClient = Chains.CreateClient(responseScenario.Handler))
        using (var responseRequest = Chains.Request(HttpMethod.Get))
        using (var responseResult = await responseScenario.SendAsync(responseClient, responseRequest))
        {
            responseResult.ShouldHaveException<HttpRequestException>();
            AssertSharedSignalRequests(responseSink, 1);
        }

        var timeoutSink = new RecordingTelemetrySink();
        using var timeoutScenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success(),
                HttpFault.Timeout()),
            options: Options(timeoutSink));
        using var timeoutClient = Chains.CreateClient(
            timeoutScenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, TimeProvider.System, Chains.IsRetryableStatus));
        using var timeoutRequest = Chains.Request(HttpMethod.Get);
        using var timeoutResult = await timeoutScenario.SendAsync(timeoutClient, timeoutRequest);

        timeoutResult.ShouldHaveStatus(HttpStatusCode.OK);
        AssertSharedSignalRequests(timeoutSink, 1);
    }

    [Fact]
    public async Task ADelayedFaultCanceledBeforeExecutionDoesNotHideARealResponseFailure()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Delay(TimeSpan.FromSeconds(2), HttpFault.NetworkError()),
                HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.Zero,
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                retryExceptions: true),
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task ExecutedMixedFailureChainsRequestSharedSignals()
    {
        var responseChain = await RunTelemetryScenarioAsync(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Response(HttpStatusCode.TooManyRequests),
                HttpFault.Success()),
            retryExceptions: false,
            expectResponse: true,
            maximumRetries: 2,
            expectedStatus: HttpStatusCode.OK);
        Assert.Equal(SharedSignalRequest, responseChain);

        var responseThenException = await RunTelemetryScenarioAsync(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.NetworkError()),
            retryExceptions: true,
            expectResponse: false);
        Assert.Equal(SharedSignalRequest, responseThenException);

        var exceptionThenResponse = await RunTelemetryScenarioAsync(
            HttpFaultScript.Sequence(
                HttpFault.NetworkError(),
                HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            retryExceptions: true,
            expectResponse: true,
            expectedStatus: HttpStatusCode.ServiceUnavailable);
        Assert.Equal(SharedSignalRequest, exceptionThenResponse);
    }

    [Fact]
    public async Task ARealTimeoutFaultActivatesAfterSettlementAndAssertion()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.Equal(1, result.Report.AttemptCount);
        Assert.Equal(HttpAttemptOutcome.Abandoned, Assert.Single(scenario.Report.Attempts).Outcome);

        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task CallerCancellationWinsTheStrategyTimeoutRaceWithoutQualifying()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var caller = new CancellationTokenSource();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider, caller));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request, caller.Token);

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
        scenario.Report.ShouldHaveAttempts(1);
        Assert.Equal(HttpAttemptOutcome.Abandoned, Assert.Single(scenario.Report.Attempts).Outcome);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task NativeHttpClientTimeoutQualifiesWhenLinkedToAnExecutedAttempt()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        client.Timeout = TimeSpan.FromMilliseconds(100);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        Assert.Equal(1, result.Report.AttemptCount);
        Assert.Equal(HttpAttemptOutcome.Abandoned, Assert.Single(scenario.Report.Attempts).Outcome);
        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task AnInjectedTimeoutRetriedToSuccessDoesNotActivateTelemetry()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.Zero,
                timeProvider: clock.TimeProvider,
                retryExceptions: true),
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.Equal(2, result.Report.AttemptCount);
        Assert.Equal(HttpAttemptOutcome.Abandoned, result.Report.Attempts[0].Outcome);
        Assert.Equal(HttpAttemptOutcome.Response, result.Report.Attempts[1].Outcome);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task ObservationCleanupDoesNotActivateTelemetryForAnInjectedTimeout()
    {
        var lateResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            Chains.CreateClock(),
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(20),
                CleanupTimeout = TimeSpan.FromMilliseconds(40),
                TelemetrySink = sink,
            });
        using var stubborn = new IgnoreCancellationHandler(lateResponse);
        using var client = Chains.CreateClient(scenario.Handler, stubborn);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.True(result.Report.IsObservationCutoff);
        Assert.Equal(HttpAttemptOutcome.Abandoned, Assert.Single(result.Report.Attempts).Outcome);
        Assert.Empty(sink.Requests);

        lateResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Fact]
    public async Task AnInterimAssertionDoesNotActivateBeforeTheScenarioSettles()
    {
        var clock = Chains.CreateClock();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(40),
                CleanupTimeout = TimeSpan.FromMilliseconds(40),
                TelemetrySink = sink,
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(1),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                retryStarted: retryStarted,
                retryRelease: retryRelease));
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        scenario.Report.ShouldHaveAttempts(1);
        Assert.Empty(sink.Requests);

        retryRelease.TrySetResult();
        using var result = await run;
        result.ShouldBePending();
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task AnInterimAssertionIsIgnoredIfNoAssertionRunsAfterSettlement()
    {
        var clock = Chains.CreateClock();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(1),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                retryStarted: retryStarted,
                retryRelease: retryRelease));
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        scenario.Report.ShouldHaveAttempts(1);
        Assert.Empty(sink.Requests);

        retryRelease.TrySetResult();
        using var result = await run;

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task ACompletedAssertionReplacesEarlierInterimResults()
    {
        var clock = Chains.CreateClock();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(1),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                retryStarted: retryStarted,
                retryRelease: retryRelease));
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveAttempts(2));

        retryRelease.TrySetResult();
        using var result = await run;

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.Equal(2, result.Report.AttemptCount);
        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task AFailingAssertionStillRequestsSharedSignals()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveAttempts(2));

        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public async Task FactoryAdapterRequestsSharedSignalsForAnEligibleScenario()
    {
        var clock = Chains.CreateClock();
        var sink = new RecordingTelemetrySink();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock.TimeProvider);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            Options(sink));
        services.AddHttpClient("orders")
            .AddHttpMessageHandler(() => new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus))
            .UseResilienceSpecDownstream(scenario);

        using var provider = services.BuildServiceProvider();
        using var result = await scenario.SendAsync(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders"),
            Chains.Request(HttpMethod.Get));

        result.ShouldHaveStatus(HttpStatusCode.OK);
        AssertSharedSignalRequests(sink, 1);
    }

    [Fact]
    public void TelemetrySinkMethodsAcceptNoProductPayload()
    {
        var methods = typeof(ITelemetrySink).GetMethods();
        Assert.Equal(
            TelemetrySinkMethods,
            methods.Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.All(methods, method => Assert.Empty(method.GetParameters()));
    }

    private static ResilienceScenarioOptions Options(ITelemetrySink sink) => new() { TelemetrySink = sink };

    private static async Task<IReadOnlyList<string>> RunTelemetryScenarioAsync(
        HttpFaultScript script,
        bool retryExceptions,
        bool expectResponse,
        int maximumRetries = 1,
        HttpStatusCode expectedStatus = HttpStatusCode.ServiceUnavailable)
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(script, options: Options(sink));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries,
                delay: TimeSpan.Zero,
                timeProvider: TimeProvider.System,
                shouldRetryResponse: Chains.IsRetryableStatus,
                retryExceptions: retryExceptions));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        if (expectResponse)
        {
            result.ShouldHaveStatus(expectedStatus);
        }
        else
        {
            result.ShouldHaveException<HttpRequestException>();
        }

        return sink.Requests;
    }

    private static void AssertSharedSignalRequests(RecordingTelemetrySink sink, int eligibleAssertions)
    {
        var expected = Enumerable.Range(0, eligibleAssertions)
            .SelectMany(_ => SharedSignalRequest);
        Assert.Equal(expected, sink.Requests);
    }
}
