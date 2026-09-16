using System.Net;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class ScriptedDownstreamTests
{
    [Fact]
    public void NothingSentMeansNoAttempts()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));

        scenario.Report.ShouldHaveAttempts(0);
        Assert.Empty(scenario.Report.Timeline);
        Assert.Null(scenario.Report.LastAttempt);
    }

    [Fact]
    public async Task SingleCallIsAnsweredInMemoryWithoutContent()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content!.ReadAsStringAsync());
        Assert.Empty(response.Headers);
        scenario.Report.ShouldHaveAttempts(1).ShouldHaveMethodSequence(HttpMethod.Get);
        Assert.Equal(HttpAttemptOutcome.Response, scenario.Report.LastAttempt!.Outcome);
    }

    [Fact]
    public async Task ScriptedRetryAfterIsAttachedToTheResponseHeader()
    {
        var script = HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)));
        using var scenario = new ResilienceScenario(script);
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter!.Delta);
        Assert.Equal(TimeSpan.FromSeconds(2), scenario.Report.LastAttempt!.RetryAfter);
    }

    [Fact]
    public async Task ExhaustedScriptFailsWithAnActionableDiagnostic()
    {
        var clock = Chains.CreateClock();
        var script = HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable));
        using var scenario = new ResilienceScenario(script, clock, clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.ScriptExhausted).ShouldHaveException<ScriptExhaustedException>();
        scenario.Report.ShouldHaveAttempts(2);
        Assert.Equal(HttpAttemptOutcome.ScriptExhausted, scenario.Report.LastAttempt!.Outcome);
        Assert.Contains("provides only 1 step(s)", result.Exception!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleConsumerScriptFailsClearlyOnConcurrentUse()
    {
        var script = HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success());
        using var scenario = new ResilienceScenario(script);
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var caller = new CancellationTokenSource();
        using var first = Chains.Request(HttpMethod.Get, "/orders/1");
        using var second = Chains.Request(HttpMethod.Get, "/orders/2");

        var firstCall = invoker.SendAsync(first, caller.Token);
        var failure = await Assert.ThrowsAsync<ConcurrentScriptUseException>(
            () => invoker.SendAsync(second, CancellationToken.None));

        Assert.Contains("one scenario per logical call", failure.Message, StringComparison.Ordinal);

        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCall);

        // The rejected caller never consumed a script step, so the timeline still describes one request.
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task ConcurrencyCanBeEnabledExplicitly()
    {
        var script = HttpFaultScript.Sequence(HttpFault.Success(), HttpFault.Success());
        var options = new ResilienceScenarioOptions { Concurrency = ScriptConcurrency.AllowConcurrent };
        using var scenario = new ResilienceScenario(script, options: options);
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var first = Chains.Request(HttpMethod.Get, "/orders/1");
        using var second = Chains.Request(HttpMethod.Get, "/orders/2");

        var responses = await Task.WhenAll(
            invoker.SendAsync(first, CancellationToken.None),
            invoker.SendAsync(second, CancellationToken.None));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.All(responses, response => response.Dispose());
        scenario.Report.ShouldHaveAtMostAttempts(2);
        Assert.Equal(2, scenario.Report.AttemptCount);
    }

    [Fact]
    public async Task RequestContentSurvivesRetriesWithoutBeingRecorded()
    {
        var clock = Chains.CreateClock();
        var script = HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable),
            HttpFault.Success());
        using var scenario = new ResilienceScenario(script, clock, clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Post);
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.Equal("{\"order\":42}", await request.Content!.ReadAsStringAsync());
        scenario.Report.ShouldHaveAttempts(2).ShouldHaveMethodSequence(HttpMethod.Post, HttpMethod.Post);
        Assert.DoesNotContain("order", scenario.Report.DescribeTimeline(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingTheResultDisposesTheFinalResponse()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler, new ContentHandler());
        using var request = Chains.Request(HttpMethod.Get);

        var result = await scenario.SendAsync(client, request);
        var content = result.Response!.Content!;
        Assert.Equal("scripted-ok", await content.ReadAsStringAsync());

        result.Dispose();
        result.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReportIsAnImmutableSnapshot()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Repeat(HttpFault.Success(), 2));
        var before = scenario.Report;

        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(0, before.AttemptCount);
        Assert.Equal(1, scenario.Report.AttemptCount);
    }
}
