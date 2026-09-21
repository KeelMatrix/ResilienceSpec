using System.Diagnostics;
using System.Net;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class DeterministicTimingTests
{
    [Fact]
    public async Task RetryAfterDeltaDrivesTheWaitOnTheInjectedClock()
    {
        var clock = Chains.CreateClock();
        var advertised = TimeSpan.FromSeconds(2);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: advertised),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromMilliseconds(100),
                timeProvider: clock,
                shouldRetryResponse: Chains.IsRetryableStatus,
                honorRetryAfter: true));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report
            .ShouldHaveAttempts(2)
            .ShouldRespectRetryAfter()
            .ShouldHaveRetryDelay(advertised)
            .ShouldHaveSettledAtVirtualTime(advertised);
        Assert.True(scenario.Report.IsSettled);
        Assert.False(scenario.Report.IsObservationCutoff);
    }

    [Fact]
    public async Task RetryAfterDeltaIsNotSatisfiedByAShorterWait()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(2)),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(1),
                timeProvider: clock,
                shouldRetryResponse: Chains.IsRetryableStatus,
                honorRetryAfter: false));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldRespectRetryAfter());
        scenario.Report.ShouldHaveRetryDelay(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RetryAfterLongerWaitStillRespectsTheMinimum()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(2)),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(3),
                timeProvider: clock,
                shouldRetryResponse: Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldRespectRetryAfter().ShouldHaveRetryDelay(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DelayedRetryContinuationDoesNotCauseAnExtraClockStep()
    {
        var clock = Chains.CreateClock();
        var delay = TimeSpan.FromSeconds(1);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay,
                clock,
                Chains.IsRetryableStatus,
                yieldBeforeDelay: true));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveRetryDelay(delay).ShouldHaveSettledAtVirtualTime(delay);
    }

    [Fact]
    public async Task PostTimerContinuationUsesTheQuiescenceContractBeforeTheNextAdvance()
    {
        var clock = Chains.CreateClock();
        var delay = TimeSpan.FromSeconds(1);
        var timerFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance,
            new ResilienceScenarioOptions
            {
                VirtualBudget = TimeSpan.FromSeconds(2),
                ObservationWindow = TimeSpan.FromMilliseconds(25),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new DelayedPostTimerRetryHandler(delay, clock, timerFired, continuationRelease));
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request);
        await timerFired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        continuationRelease.SetResult();

        using var result = await run;

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2).ShouldHaveRetryDelay(delay).ShouldHaveSettledAtVirtualTime(delay);
    }

    [Fact]
    public async Task UncompletedQuiescenceContractProducesPendingWithoutAnExtraAdvance()
    {
        var clock = Chains.CreateClock();
        var delay = TimeSpan.FromSeconds(1);
        var continuationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance,
            new ResilienceScenarioOptions
            {
                VirtualBudget = TimeSpan.FromSeconds(2),
                ObservationWindow = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new DelayedPostTimerRetryHandler(delay, clock, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), continuationRelease));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.Equal(1, result.VirtualElapsed.TotalSeconds);
        Assert.True(scenario.Report.IsObservationCutoff);
        Assert.Equal(1, scenario.Report.AttemptCount);
    }

    [Fact]
    public async Task RetryAfterWithoutAFollowingRetryHasATruthfulDiagnostic()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2))),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        var failure = Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldRespectRetryAfter());
        Assert.Contains("no following attempt was observed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no recorded attempt carried", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClockThatIsNotAdvancedKeepsTheRequestPending()
    {
        var clock = Chains.CreateClock();
        var options = new ResilienceScenarioOptions
        {
            AdvanceClock = false,
            PendingObservation = TimeSpan.FromMilliseconds(200),
        };
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromSeconds(2), HttpFault.Success())),
            clock,
            clock.Advance,
            options);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        scenario.Report.ShouldHaveAttempts(1);
        Assert.True(scenario.Report.HasTiming);
        Assert.False(scenario.Report.IsSettled);
        Assert.True(scenario.Report.IsObservationCutoff);
        Assert.Equal(TimeSpan.Zero, result.VirtualElapsed);
        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveSettledAtVirtualTime(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DelayStepsCompleteWhenTheClockAdvances()
    {
        var clock = Chains.CreateClock();
        var delay = TimeSpan.FromSeconds(2);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(delay, HttpFault.Success())),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report
            .ShouldHaveAttempts(1)
            .ShouldHaveAttemptDuration(1, delay)
            .ShouldHaveSettledAtVirtualTime(delay);
        Assert.True(scenario.Report.IsSettled);
        Assert.False(scenario.Report.IsObservationCutoff);
    }

    [Fact]
    public async Task PerAttemptTimeoutFiresOnTheInjectedClock()
    {
        var clock = Chains.CreateClock();
        var attemptTimeout = TimeSpan.FromSeconds(1);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.FromMilliseconds(100), clock, Chains.IsRetryableStatus, retryExceptions: true),
            new AttemptTimeoutHandler(attemptTimeout, clock));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report
            .ShouldHaveAttempts(2)
            .ShouldHaveAttemptDuration(1, attemptTimeout)
            .ShouldHaveSettledAtVirtualTime(attemptTimeout + TimeSpan.FromMilliseconds(100));
        Assert.Equal(HttpAttemptOutcome.Abandoned, scenario.Report.Attempts[0].Outcome);
    }

    [Fact]
    public async Task TotalRequestTimeoutSettlesOnTheInjectedClock()
    {
        var clock = Chains.CreateClock();
        var total = TimeSpan.FromSeconds(3);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Always(HttpFault.Timeout()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new TotalTimeoutHandler(total, clock),
            new RetryHandler(1, TimeSpan.FromSeconds(1), clock, Chains.IsRetryableStatus, retryExceptions: true),
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        scenario.Report.ShouldHaveSettledAtVirtualTime(total);
        Assert.True(scenario.Report.IsSettled);
        Assert.False(scenario.Report.IsObservationCutoff);
        Assert.True(scenario.Report.AttemptCount >= 2);
    }

    [Fact]
    public async Task VirtualBudgetExhaustionIsAnObservationCutoffNotSettlement()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            clock.Advance,
            new ResilienceScenarioOptions
            {
                VirtualBudget = TimeSpan.FromSeconds(1),
                ObservationWindow = TimeSpan.FromMilliseconds(10),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.False(scenario.Report.IsSettled);
        Assert.True(scenario.Report.IsObservationCutoff);
        var failure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveSettledAtVirtualTime(TimeSpan.FromSeconds(1)));
        Assert.Contains("observation stopped", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalAdvanceIsCappedAtTheVirtualBudget()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            clock.Advance,
            new ResilienceScenarioOptions
            {
                AdvanceStep = TimeSpan.FromMilliseconds(600),
                VirtualBudget = TimeSpan.FromSeconds(1),
                ObservationWindow = TimeSpan.FromMilliseconds(10),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.Equal(TimeSpan.FromSeconds(1), result.VirtualElapsed);
        Assert.True(scenario.Report.IsObservationCutoff);
    }

    [Fact]
    public async Task TimingAssertionsWithoutAClockAreUnavailable()
    {
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable), HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        scenario.Report.ShouldHaveAttempts(1);
        Assert.False(scenario.Report.HasTiming);
        Assert.Throws<MissingTimeProviderException>(() => scenario.Report.ShouldHaveRetryDelay(TimeSpan.FromSeconds(1)));
        Assert.Throws<MissingTimeProviderException>(() => scenario.Report.ShouldRespectRetryAfter());
        Assert.Throws<MissingTimeProviderException>(() => scenario.Report.ShouldHaveSettledAtVirtualTime(TimeSpan.FromSeconds(1)));
        Assert.Throws<MissingTimeProviderException>(() => scenario.Report.ShouldHaveAttemptDuration(1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task LongVirtualWaitsStayWallClockCheap()
    {
        var clock = Chains.CreateClock();
        var options = new ResilienceScenarioOptions { AdvanceStep = TimeSpan.FromMilliseconds(500) };
        var virtualWait = TimeSpan.FromSeconds(10);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(virtualWait, HttpFault.Success())),
            clock,
            clock.Advance,
            options);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        var stopwatch = Stopwatch.StartNew();
        using var result = await scenario.SendAsync(client, request);
        stopwatch.Stop();

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveSettledAtVirtualTime(virtualWait);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"a virtual wait of {virtualWait} took {stopwatch.Elapsed} of wall-clock time");
    }
}
