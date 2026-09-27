using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

internal sealed class RecordingTelemetrySink : ITelemetrySink
{
    private readonly List<ScenarioTelemetrySignal> _signals = new();
    private readonly object _gate = new();

    public void TrackActivation(ScenarioTelemetrySignal signal)
    {
        lock (_gate)
        {
            _signals.Add(signal);
        }
    }

    internal IReadOnlyList<ScenarioTelemetrySignal> Signals
    {
        get
        {
            lock (_gate)
            {
                return _signals.ToArray();
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

        Assert.Empty(sink.Signals);
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

        Assert.Empty(sink.Signals);
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
        Assert.Empty(sink.Signals);
    }

    [Fact]
    public async Task AnInjectedFailureWithAnEvaluatedAssertionActivatesExactlyOnce()
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

        scenario.Report.ShouldHaveAttempts(2);
        result.ShouldHaveStatus(HttpStatusCode.OK);

        var signal = Assert.Single(sink.Signals);
        Assert.True(signal.ResponseFault);
        Assert.False(signal.ExceptionFault);
        Assert.True(signal.TimingAssertion);
        Assert.Equal(AttemptCountBucket.TwoToThree, signal.AttemptBucket);
        Assert.Equal(AssertionOutcome.Passed, signal.Assertion);
        Assert.Equal(IntegrationPath.ScriptedDownstream, signal.Integration);
        Assert.Equal(ScenarioTelemetry.SupportedTargetFramework, signal.TargetFramework);
        Assert.Equal(typeof(TelemetryHost).Assembly.GetName().Version!.ToString(), signal.PackageVersion);
    }

    [Fact]
    public async Task TelemetryCategoriesDescribeOnlyFailuresThatReachedTheDownstream()
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
        var signal = Assert.Single(sink.Signals);
        Assert.True(signal.ResponseFault);
        Assert.False(signal.ExceptionFault);
    }

    [Fact]
    public async Task UnusedLaterResponseAndTimeoutFaultsDoNotBecomeTelemetryCategories()
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
            var responseSignal = Assert.Single(responseSink.Signals);
            Assert.False(responseSignal.ResponseFault);
            Assert.True(responseSignal.ExceptionFault);
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
        var timeoutSignal = Assert.Single(timeoutSink.Signals);
        Assert.True(timeoutSignal.ResponseFault);
        Assert.False(timeoutSignal.ExceptionFault);
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
        var signal = Assert.Single(sink.Signals);
        Assert.True(signal.ResponseFault);
        Assert.False(signal.ExceptionFault);
    }

    [Fact]
    public async Task ExecutedMixedFailureChainsAccumulateOnlyTheirPublishedCategories()
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
        Assert.True(responseChain.ResponseFault);
        Assert.False(responseChain.ExceptionFault);

        var responseThenException = await RunTelemetryScenarioAsync(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.NetworkError()),
            retryExceptions: true,
            expectResponse: false);
        Assert.True(responseThenException.ResponseFault);
        Assert.True(responseThenException.ExceptionFault);

        var exceptionThenResponse = await RunTelemetryScenarioAsync(
            HttpFaultScript.Sequence(
                HttpFault.NetworkError(),
                HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            retryExceptions: true,
            expectResponse: true,
            expectedStatus: HttpStatusCode.ServiceUnavailable);
        Assert.True(exceptionThenResponse.ResponseFault);
        Assert.True(exceptionThenResponse.ExceptionFault);
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
        scenario.Report.ShouldHaveAttempts(1);

        var signal = Assert.Single(sink.Signals);
        Assert.False(signal.ResponseFault);
        Assert.True(signal.ExceptionFault);
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
        Assert.Empty(sink.Signals);

        retryRelease.TrySetResult();
        using var result = await run;
        result.ShouldBePending();
        Assert.Empty(sink.Signals);
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
        Assert.Empty(sink.Signals);

        retryRelease.TrySetResult();
        using var result = await run;

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Empty(sink.Signals);
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
        scenario.Report.ShouldHaveAttempts(2);
        Assert.Equal(AssertionOutcome.Passed, Assert.Single(sink.Signals).Assertion);
    }

    [Fact]
    public async Task AFailingAssertionIsStillAnActivation()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveAttempts(2));

        var signal = Assert.Single(sink.Signals);
        Assert.Equal(AssertionOutcome.Failed, signal.Assertion);
        Assert.Equal(AttemptCountBucket.Single, signal.AttemptBucket);
    }

    [Fact]
    public async Task TheAdapterIsReportedAsTheIntegrationPath()
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
        Assert.Equal(IntegrationPath.HttpClientFactory, Assert.Single(sink.Signals).Integration);
    }

    [Fact]
    public void SignalFieldsAreAnExactCoarseAllowlist()
    {
        var expected = new[]
        {
            "PackageVersion",
            "TargetFramework",
            "ResponseFault",
            "ExceptionFault",
            "TimingAssertion",
            "AttemptBucket",
            "Assertion",
            "Integration",
        };

        var properties = typeof(ScenarioTelemetrySignal).GetProperties();
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(properties, property => Assert.True(
            property.PropertyType == typeof(string) || property.PropertyType == typeof(bool) || property.PropertyType.IsEnum,
            $"{property.Name} must be a coarse scalar field"));
    }

    [Fact]
    public async Task SignalTextCarriesNoRequestIdentifyingData()
    {
        var sink = new RecordingTelemetrySink();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.NetworkError()),
            options: Options(sink));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.SecretRequest();
        using var result = await scenario.SendAsync(client, request);

        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveAttempts(2));

        var signal = Assert.Single(sink.Signals);
        var text = signal.Describe();
        foreach (var marker in new[] { "super-secret", "orders.invalid", "Bearer", "session=" })
        {
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        }
    }

    private static ResilienceScenarioOptions Options(ITelemetrySink sink) => new() { TelemetrySink = sink };

    private static async Task<ScenarioTelemetrySignal> RunTelemetryScenarioAsync(
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

        return Assert.Single(sink.Signals);
    }
}
