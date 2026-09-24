using System.Net;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class HttpFaultTests
{
    [Fact]
    public void ResponseRejectsNegativeRetryAfter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void DelayRejectsNonPositiveDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpFault.Delay(TimeSpan.Zero, HttpFault.Success()));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpFault.Delay(TimeSpan.FromSeconds(-1), HttpFault.Success()));
    }

    [Fact]
    public void DelayRejectsNestedDelay()
    {
        var nested = HttpFault.Delay(TimeSpan.FromSeconds(1), HttpFault.Success());
        Assert.Throws<ArgumentException>(() => HttpFault.Delay(TimeSpan.FromSeconds(1), nested));
    }

    [Fact]
    public void FaultsDescribeThemselvesWithoutRequestData()
    {
        Assert.Equal("response 503 (retry-after 2 s)", HttpFault.Response(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(2)).ToString());
        Assert.Equal("response 200", HttpFault.Success().ToString());
        Assert.Equal("network error", HttpFault.NetworkError().ToString());
        Assert.Equal("no response", HttpFault.Timeout().ToString());
        Assert.Equal("delay 500 ms then response 200", HttpFault.Delay(TimeSpan.FromMilliseconds(500), HttpFault.Success()).ToString());
    }
}

public sealed class HttpFaultScriptTests
{
    [Fact]
    public void SequenceRejectsEmptyScript()
    {
        Assert.Throws<ArgumentException>(() => HttpFaultScript.Sequence());
    }

    [Fact]
    public void SequenceRejectsNullSteps()
    {
        Assert.Throws<ArgumentException>(() => HttpFaultScript.Sequence(HttpFault.Success(), null!));
    }

    [Fact]
    public void SequenceRejectsScriptsBeyondTheBound()
    {
        var faults = new HttpFault[HttpFaultScript.MaximumSteps + 1];
        Array.Fill(faults, HttpFault.Success());
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpFaultScript.Sequence(faults));
    }

    [Fact]
    public void SequenceTakesOwnershipOfItsSteps()
    {
        var faults = new[] { HttpFault.Success(), HttpFault.Success() };
        var script = HttpFaultScript.Sequence(faults);

        faults[0] = HttpFault.Response(HttpStatusCode.ServiceUnavailable);

        Assert.Equal(2, script.StepCount);
        Assert.Equal("2 step(s): response 200 -> response 200", script.ToString());
    }

    [Fact]
    public void RepeatCoversTheRequestedAttempts()
    {
        var script = HttpFaultScript.Repeat(HttpFault.Response(HttpStatusCode.ServiceUnavailable), 3);

        Assert.Equal(3, script.StepCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpFaultScript.Repeat(HttpFault.Success(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpFaultScript.Repeat(HttpFault.Success(), HttpFaultScript.MaximumSteps + 1));
    }

    [Fact]
    public void AlwaysUsesTheBoundedStepLimit()
    {
        Assert.Equal(HttpFaultScript.MaximumSteps, HttpFaultScript.Always(HttpFault.Success()).StepCount);
    }
}

public sealed class ResilienceScenarioConfigurationTests
{
    private static HttpFaultScript Script() => HttpFaultScript.Sequence(HttpFault.Success());

    [Fact]
    public void TimingScenarioIsBoundToTheClockObject()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(Script(), clock);

        Assert.Same(clock.TimeProvider, scenario.TimeProvider);
    }

    [Fact]
    public void TimingScenarioWithoutAClockHasNoTimingProvider()
    {
        using var scenario = new ResilienceScenario(Script());

        Assert.Null(scenario.TimeProvider);
        Assert.False(scenario.SupportsTiming);
    }

    [Fact]
    public void DelayStepsRequireAControllableClock()
    {
        var script = HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromSeconds(1), HttpFault.Success()));
        Assert.Throws<MissingTimeProviderException>(() => new ResilienceScenario(script));
        Assert.Throws<MissingTimeProviderException>(() => new ScriptedHttpMessageHandler(script));
    }

    [Fact]
    public void DirectHandlerRejectsAnUntrackedProvider()
    {
        Assert.Throws<MissingTimeProviderException>(() =>
            new ScriptedHttpMessageHandler(Script(), TimeProvider.System));
    }

    [Fact]
    public async Task DirectHandlerAcceptsTheSupportedTrackingProvider()
    {
        var clock = Chains.CreateClock();
        using var handler = new ScriptedHttpMessageHandler(Script(), clock.TimeProvider);
        using var client = new HttpClient(handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        handler.Report.ShouldHaveAttempts(1).ShouldHaveAttemptDuration(1, TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AdvanceStepMustBePositive(int milliseconds)
    {
        var clock = Chains.CreateClock();
        var options = new ResilienceScenarioOptions { AdvanceStep = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceScenario(Script(), clock, options));
    }

    [Fact]
    public void ObservationWindowsMustBePositive()
    {
        var clock = Chains.CreateClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceScenario(
            Script(),
            clock,
            new ResilienceScenarioOptions { ObservationWindow = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceScenario(
            Script(),
            clock,
            new ResilienceScenarioOptions { PendingObservation = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceScenario(
            Script(),
            clock,
            new ResilienceScenarioOptions { VirtualBudget = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public void ObservationWindowsRejectTaskDelayUnsupportedValuesBeforeARequestStarts()
    {
        var clock = Chains.CreateClock();
        var unsupported = TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromMilliseconds(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilienceScenario(
            Script(),
            clock,
            new ResilienceScenarioOptions { ObservationWindow = unsupported }));
    }

    [Fact]
    public async Task DisposedScenarioRejectsFurtherRuns()
    {
        var scenario = new ResilienceScenario(Script());
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        scenario.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => scenario.SendAsync(client, request));
    }
}
