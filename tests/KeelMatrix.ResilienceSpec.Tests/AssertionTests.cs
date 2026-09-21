using System.Net;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class AssertionTests
{
    [Fact]
    public async Task AttemptCountFailureReportsTheExpectationAndTheTimeline()
    {
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var failure = Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldHaveAttempts(2));

        Assert.Contains("Expected exactly 2 attempt(s) at the scripted downstream", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the downstream served 1 attempt(s)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("#1 GET -> response 503", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtMostAttemptsAssertionPassesAndFailsAsDocumented()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock.TimeProvider,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var report = scenario.Report;
        Assert.Same(report, report.ShouldHaveAtMostAttempts(2));
        var failure = Assert.Throws<ResilienceAssertionException>(() => report.ShouldHaveAtMostAttempts(1));
        Assert.Contains("Expected at most 1 attempt(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MethodSequenceFailureListsTheObservedSequence()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock.TimeProvider,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var failure = Assert.Throws<ResilienceAssertionException>(
            () => scenario.Report.ShouldHaveMethodSequence(HttpMethod.Get, HttpMethod.Post));

        Assert.Contains("Expected the method sequence GET -> POST", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the downstream observed GET -> GET", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsafeMethodAssertionFailsWhenTheRequestWasRepeated()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock.TimeProvider,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Post);
        using var result = await scenario.SendAsync(client, request);

        var failure = Assert.Throws<ResilienceAssertionException>(() => scenario.Report.ShouldNotHaveRetried(HttpMethod.Post));

        Assert.Contains("Expected a POST request to reach the scripted downstream at most once", failure.Message, StringComparison.Ordinal);
        Assert.Contains("served 2 POST attempt(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalExceptionAssertionsDescribeWhatTheRunSurfaced()
    {
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.NetworkError()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        Assert.Same(result, result.ShouldHaveException<HttpRequestException>());
        result.ShouldHaveKind(ResilienceResultKind.DownstreamError);

        var failure = Assert.Throws<ResilienceAssertionException>(() => result.ShouldHaveException<TimeoutException>());
        Assert.Contains("System.TimeoutException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpRequestException", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAssertionOnARequestWithoutResponseDescribesTheEnding()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.NetworkError()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var failure = Assert.Throws<ResilienceAssertionException>(() => result.ShouldHaveStatus(HttpStatusCode.OK));

        Assert.Contains("Expected the final response to have status 200 OK", failure.Message, StringComparison.Ordinal);
        Assert.Contains("DownstreamError", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingAssertionDescribesACompletedRun()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var failure = Assert.Throws<ResilienceAssertionException>(() => result.ShouldBePending());

        Assert.Contains("Expected the request to still be pending", failure.Message, StringComparison.Ordinal);
        Assert.Contains("status 200 OK", failure.Message, StringComparison.Ordinal);
    }
}
