using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Time.Testing;
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
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromMilliseconds(100),
                timeProvider: clock.TimeProvider,
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
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(1),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                honorRetryAfter: false));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldRespectRetryAfter());
        scenario.Report.ShouldHaveRetryDelay(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EarlyRetryScheduleDoesNotPassRetryAfterMinimumWithDefaultOptions()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(2)),
                HttpFault.Success()),
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromMilliseconds(1_950),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                honorRetryAfter: false));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldRespectRetryAfter());
        Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveRetryDelay(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromMilliseconds(1_950),
            scenario.Report.Attempts[1].StartedAfter!.Value -
            (scenario.Report.Attempts[0].StartedAfter!.Value + scenario.Report.Attempts[0].Duration!.Value));
    }

    [Fact]
    public async Task RetryAfterLongerWaitStillRespectsTheMinimum()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(2)),
                HttpFault.Success()),
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(3),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldRespectRetryAfter().ShouldHaveRetryDelay(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(TimeSpan.TicksPerMillisecond, false)]
    [InlineData(TimeSpan.TicksPerMillisecond * 100, false)]
    public void ExactRetryDelayRejectsEveryPositiveDifference(long additionalTicks, bool shouldPass)
    {
        var expected = TimeSpan.FromMilliseconds(10);
        var observed = expected + TimeSpan.FromTicks(additionalTicks);
        var report = ExactTimingReport(
            [
                TimingAttempt(1, TimeSpan.Zero, TimeSpan.Zero),
                TimingAttempt(2, observed, TimeSpan.Zero),
            ],
            observed);

        if (shouldPass)
        {
            report.ShouldHaveRetryDelay(expected);
        }
        else
        {
            Assert.Throws<ResilienceAssertionException>(() => report.ShouldHaveRetryDelay(expected));
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(TimeSpan.TicksPerMillisecond, false)]
    [InlineData(TimeSpan.TicksPerMillisecond * 100, false)]
    public void ExactAttemptDurationRejectsEveryPositiveDifference(long additionalTicks, bool shouldPass)
    {
        var expected = TimeSpan.FromMilliseconds(10);
        var observed = expected + TimeSpan.FromTicks(additionalTicks);
        var report = ExactTimingReport([TimingAttempt(1, TimeSpan.Zero, observed)], observed);

        if (shouldPass)
        {
            report.ShouldHaveAttemptDuration(1, expected);
        }
        else
        {
            Assert.Throws<ResilienceAssertionException>(() => report.ShouldHaveAttemptDuration(1, expected));
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(TimeSpan.TicksPerMillisecond, false)]
    [InlineData(TimeSpan.TicksPerMillisecond * 100, false)]
    public void ExactSettlementTimeRejectsEveryPositiveDifference(long additionalTicks, bool shouldPass)
    {
        var expected = TimeSpan.FromMilliseconds(10);
        var observed = expected + TimeSpan.FromTicks(additionalTicks);
        var report = ExactTimingReport([TimingAttempt(1, TimeSpan.Zero, TimeSpan.Zero)], observed);

        if (shouldPass)
        {
            report.ShouldHaveSettledAtVirtualTime(expected);
        }
        else
        {
            Assert.Throws<ResilienceAssertionException>(() => report.ShouldHaveSettledAtVirtualTime(expected));
        }
    }

    [Fact]
    public async Task SamplingOnlyRetryDelayCannotMasqueradeAsExactEvidence()
    {
        var release = new TaskCompletionSource();
        var step = TimeSpan.FromMilliseconds(100);
        var provider = new FakeTimeProvider(Chains.ClockStart);
        var clock = new ResilienceScenarioClock(provider, amount =>
        {
            provider.Advance(amount);
            release.TrySetResult();
        });
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success(), HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions { AdvanceStep = step });
        using var client = Chains.CreateClient(scenario.Handler, new SamplingRetryHandler(release.Task));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        var failure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveRetryDelay(step));
        Assert.Contains("exact timing evidence", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SamplingOnlyAttemptDurationCannotMasqueradeAsExactEvidence()
    {
        var release = new TaskCompletionSource();
        var provider = new FakeTimeProvider(Chains.ClockStart);
        var clock = new ResilienceScenarioClock(provider, amount =>
        {
            provider.Advance(amount);
            release.TrySetResult();
        });
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            clock);
        using var client = Chains.CreateClient(scenario.Handler, new SamplingBeforeAttemptHandler(release.Task));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        var failure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveAttemptDuration(1, TimeSpan.Zero));
        Assert.Contains("exact timing evidence", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SamplingOnlySettlementCannotMasqueradeAsExactEvidence()
    {
        var release = new TaskCompletionSource();
        var step = TimeSpan.FromMilliseconds(100);
        var provider = new FakeTimeProvider(Chains.ClockStart);
        var clock = new ResilienceScenarioClock(provider, amount =>
        {
            provider.Advance(amount);
            release.TrySetResult();
        });
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions { AdvanceStep = step });
        using var client = Chains.CreateClient(scenario.Handler, new SamplingBeforeAttemptHandler(release.Task));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        var failure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveSettledAtVirtualTime(step));
        Assert.Contains("exact timing evidence", failure.Message, StringComparison.OrdinalIgnoreCase);
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
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay,
                clock.TimeProvider,
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
            new ResilienceScenarioOptions
            {
                VirtualBudget = TimeSpan.FromSeconds(2),
                ObservationWindow = TimeSpan.FromMilliseconds(25),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new DelayedPostTimerRetryHandler(delay, clock.TimeProvider, timerFired, continuationRelease));
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
            new ResilienceScenarioOptions
            {
                VirtualBudget = TimeSpan.FromSeconds(2),
                ObservationWindow = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new DelayedPostTimerRetryHandler(delay, clock.TimeProvider, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), continuationRelease));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.Equal(1, result.VirtualElapsed.TotalSeconds);
        Assert.True(scenario.Report.IsObservationCutoff);
        Assert.Equal(1, scenario.Report.AttemptCount);
    }

    [Fact]
    public async Task StalledDueTimerCallbackIsBoundedAndLateReleaseKeepsScenarioConsumed()
    {
        var clock = Chains.CreateClock();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success(),
                HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions
            {
                VirtualBudget = TimeSpan.FromSeconds(2),
                ObservationWindow = TimeSpan.FromMilliseconds(25),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new StalledTimerCallbackHandler(
                clock.TimeProvider,
                callbackStarted,
                callbackCompleted,
                releaseCallback,
                TimeSpan.FromMilliseconds(1)));
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request);
        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.Same(run, completed);

            using var result = await run;
            result.ShouldBePending();
            Assert.True(scenario.Report.IsObservationCutoff);

            releaseCallback.TrySetResult();
            await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<ScenarioConsumedException>(
                () => scenario.SendAsync(client, Chains.Request(HttpMethod.Get)));
        }
        finally
        {
            releaseCallback.TrySetResult();
            if (!run.IsCompleted)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task RetryAfterWithoutAFollowingRetryHasATruthfulDiagnostic()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2))),
            clock);
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
            clock);
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
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.FromMilliseconds(100), clock.TimeProvider, Chains.IsRetryableStatus, retryExceptions: true),
            new AttemptTimeoutHandler(attemptTimeout, clock.TimeProvider));
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
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new TotalTimeoutHandler(total, clock.TimeProvider),
            new RetryHandler(1, TimeSpan.FromSeconds(1), clock.TimeProvider, Chains.IsRetryableStatus, retryExceptions: true),
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider));
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
    public async Task VirtualBudgetCleanupDoesNotCreatePerAttemptTimeoutEvidence()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
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
        Assert.Null(scenario.Report.Attempts[0].Duration);
        var failure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveAttemptDuration(1, TimeSpan.FromSeconds(1)));
        Assert.Contains("incomplete timing evidence", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoClockAdvanceCleanupDoesNotCreateZeroDurationEvidence()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromSeconds(1), HttpFault.Success())),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(10),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.Null(scenario.Report.Attempts[0].Duration);
        var failure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveAttemptDuration(1, TimeSpan.Zero));
        Assert.Contains("incomplete timing evidence", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompletedEarlierAttemptRemainsAssertableWhenCleanupAbandonsLaterAttempt()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Timeout()),
            clock,
            new ResilienceScenarioOptions
            {
                VirtualBudget = TimeSpan.FromSeconds(1),
                ObservationWindow = TimeSpan.FromMilliseconds(10),
                CleanupTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        scenario.Report.ShouldHaveAttemptDuration(1, TimeSpan.Zero);
        Assert.Null(scenario.Report.Attempts[1].Duration);
    }

    [Fact]
    public async Task FinalAdvanceIsCappedAtTheVirtualBudget()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
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
    public void SystemClockCannotBeWrappedAsADeterministicClock()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => new ResilienceScenarioClock(TimeProvider.System, static _ => { }));

        Assert.Contains("controllable", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicSystemClockWrapperCannotBeWrappedAsADeterministicClock()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => new ResilienceScenarioClock(new SystemDelegatingTimeProvider(), static _ => { }));

        Assert.Contains("deterministic timing", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupportedProviderSubclassCannotSpoofTimingAdmission()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => new ResilienceScenarioClock(new FakeTimeProviderSubclass(), static _ => { }));

        Assert.Contains("exact", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsumerAuthoredProviderWithSpoofedFrameworkIdentityCannotObtainTimingAdmission()
    {
        var provider = SpoofedTimeProviderFactory.Create();

        Assert.Equal("Microsoft.Extensions.Time.Testing.FakeTimeProvider", provider.GetType().FullName);
        Assert.Equal("Microsoft.Extensions.TimeProvider.Testing", provider.GetType().Assembly.GetName().Name);

        var failure = Assert.Throws<ArgumentException>(
            () => new ResilienceScenarioClock(provider, static _ => { }));

        Assert.Contains("runtime identity", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtherConsumerAuthoredTimeProviderCannotObtainTimingAdmission()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => new ResilienceScenarioClock(new ConsumerAuthoredTimeProvider(), static _ => { }));

        Assert.Contains("consumer-authored", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupportedControllableClockRemainsTimingEligible()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            clock);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.True(scenario.Report.HasTiming);
        scenario.Report.ShouldHaveAttemptDuration(1, TimeSpan.Zero);
    }

    [Fact]
    public void NoOpAdvanceDelegateIsRejectedWhenUsed()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, static _ => { });

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => clock.Advance(TimeSpan.FromSeconds(1)));

        Assert.Contains("exactly", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 s", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdvanceOfAnotherProviderIsRejectedWhenUsed()
    {
        var provider = new FakeTimeProvider();
        var other = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, other.Advance);

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => clock.Advance(TimeSpan.FromSeconds(1)));

        Assert.Contains("wrapped", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnderAdvanceDelegateIsRejectedWhenUsed()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, amount => provider.Advance(amount / 2));

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => clock.Advance(TimeSpan.FromSeconds(2)));

        Assert.Contains("1 s", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 s", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoubleAdvanceDelegateIsRejectedWhenUsed()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, amount => provider.Advance(amount + amount));

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => clock.Advance(TimeSpan.FromSeconds(1)));

        Assert.Contains("2 s", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OffsetAdvanceDelegateIsRejectedWhenUsed()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(
            provider,
            amount => provider.Advance(amount + TimeSpan.FromTicks(1)));

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => clock.Advance(TimeSpan.FromSeconds(1)));

        Assert.Contains("requested", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorrectAdvanceDelegateMovesTheAdmittedProviderExactly()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, provider.Advance);
        var before = provider.GetTimestamp();

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), provider.GetElapsedTime(before));
    }

    [Fact]
    public void MovementOutsideTheWrapperAdvanceIsRejectedOnTheNextObservation()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, provider.Advance);
        provider.Advance(TimeSpan.FromSeconds(1));

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => clock.TimeProvider.GetTimestamp());

        Assert.Contains("outside", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scenario-controlled", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverAdvanceCannotProduceFalseSettlementEvidence()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, amount => provider.Advance(amount + amount));
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromSeconds(1), HttpFault.Success())),
            clock);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => scenario.SendAsync(client, request));

        Assert.Contains("exactly", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(scenario.Report.IsSettled);
        Assert.Null(scenario.Report.SettledVirtualElapsed);
    }

    [Fact]
    public void NonZeroAutoAdvanceIsRejectedAtConstruction()
    {
        var provider = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromTicks(1) };

        var failure = Assert.Throws<ArgumentException>(
            () => new ResilienceScenarioClock(provider, provider.Advance));

        Assert.Contains("AutoAdvanceAmount", failure.Message, StringComparison.Ordinal);
        Assert.Contains("zero", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoAdvanceMutationDuringARunFailsClosed()
    {
        var provider = new FakeTimeProvider();
        var clock = new ResilienceScenarioClock(provider, provider.Advance);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            clock);
        provider.AutoAdvanceAmount = TimeSpan.FromTicks(1);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => scenario.SendAsync(client, request));

        Assert.Contains("AutoAdvanceAmount", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedTimingProviderVersionIsPinnedToTheAdmissionContract()
    {
        Assert.Equal(new Version(10, 10, 0, 0), typeof(FakeTimeProvider).Assembly.GetName().Version);
    }

    private static HttpAttempt TimingAttempt(int ordinal, TimeSpan startedAfter, TimeSpan duration) =>
        new(
            ordinal,
            HttpMethod.Get,
            HttpAttemptOutcome.Response,
            HttpStatusCode.OK,
            null,
            startedAfter,
            duration)
        {
            StartedAfterIsExact = true,
            DurationIsExact = true,
        };

    private static HttpAttemptReport ExactTimingReport(
        HttpAttempt[] attempts,
        TimeSpan settledVirtualElapsed,
        bool settledVirtualElapsedIsExact = true) =>
        new(
            attempts,
            attempts.Length,
            overflowed: false,
            settled: true,
            observationCutoff: false,
            settledVirtualElapsed,
            TimeSpan.FromMilliseconds(100),
            settledVirtualElapsedIsExact,
            new ScenarioTelemetry(
                new RecordingTelemetrySink(),
                HttpFaultScript.Sequence(HttpFault.Success()),
                timingAssertionsAvailable: true));

    [Fact]
    public async Task LongVirtualWaitsStayWallClockCheap()
    {
        var clock = Chains.CreateClock();
        var options = new ResilienceScenarioOptions { AdvanceStep = TimeSpan.FromMilliseconds(500) };
        var virtualWait = TimeSpan.FromSeconds(10);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(virtualWait, HttpFault.Success())),
            clock,
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

    [Fact]
    public async Task DefaultOptionsKeepLongVirtualWaitsCheapAndRepeatable()
    {
        var elapsed = new List<TimeSpan>();
        for (var run = 0; run < 2; run++)
        {
            var clock = Chains.CreateClock();
            var virtualWait = TimeSpan.FromSeconds(10);
            using var scenario = new ResilienceScenario(
                HttpFaultScript.Sequence(HttpFault.Delay(virtualWait, HttpFault.Success())),
                clock);
            using var client = Chains.CreateClient(scenario.Handler);
            using var request = Chains.Request(HttpMethod.Get);

            var stopwatch = Stopwatch.StartNew();
            using var result = await scenario.SendAsync(client, request);
            stopwatch.Stop();

            result.ShouldHaveStatus(HttpStatusCode.OK);
            scenario.Report.ShouldHaveSettledAtVirtualTime(virtualWait);
            elapsed.Add(stopwatch.Elapsed);
        }

        Assert.All(elapsed, duration => Assert.True(
            duration < TimeSpan.FromSeconds(3),
            $"a virtual wait took {duration} of wall-clock time"));
    }

    [Fact]
    public async Task ThrowingAdvanceIsAHarnessFailureAndCleansUpPendingRequest()
    {
        var provider = new FakeTimeProvider(Chains.ClockStart);
        var clock = new ResilienceScenarioClock(
            provider,
            _ => throw new InvalidOperationException("advance failed"));
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromHours(1), HttpFault.Success())),
            clock);
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.SendAsync(client, request));

        Assert.Equal("advance failed", failure.Message);
        Assert.False(scenario.Report.IsSettled);
        Assert.True(scenario.Report.IsObservationCutoff);
        Assert.Equal(HttpAttemptOutcome.Abandoned, scenario.Report.Attempts[0].Outcome);
    }
}

internal sealed class ConsumerAuthoredTimeProvider : TimeProvider
{
}

internal sealed class SamplingBeforeAttemptHandler : DelegatingHandler
{
    private readonly Task _release;

    internal SamplingBeforeAttemptHandler(Task release) => _release = release;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        _release.ContinueWith(
            _ => base.SendAsync(request, cancellationToken),
            cancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
}

internal sealed class SamplingRetryHandler : DelegatingHandler
{
    private readonly Task _release;

    internal SamplingRetryHandler(Task release) => _release = release;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var first = base.SendAsync(request, cancellationToken);
        return first.ContinueWith(
            completed =>
            {
                using var response = completed.GetAwaiter().GetResult();
                return _release.ContinueWith(
                    _ =>
                    {
                        using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
                        return base.SendAsync(retry, cancellationToken);
                    },
                    cancellationToken,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Unwrap();
            },
            cancellationToken,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }
}
