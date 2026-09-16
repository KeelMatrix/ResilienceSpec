using System.Net;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class RetryBehaviourTests
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ServiceUnavailableThenSuccessTakesExactlyTwoAttempts()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, RetryDelay, clock, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK).ShouldHaveKind(ResilienceResultKind.Response);
        scenario.Report
            .ShouldHaveAttempts(2)
            .ShouldHaveMethodSequence(HttpMethod.Get, HttpMethod.Get)
            .ShouldHaveRetryDelay(RetryDelay)
            .ShouldHaveSettledAtVirtualTime(RetryDelay);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    public async Task EveryMethodIsRecordedInAttemptOrder(string methodName)
    {
        var method = new HttpMethod(methodName);
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus));
        using var request = Chains.Request(method);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2).ShouldHaveMethodSequence(method, method);
        Assert.Equal(1, scenario.Report.Attempts[0].Ordinal);
        Assert.Equal(2, scenario.Report.Attempts[1].Ordinal);
    }

    [Fact]
    public async Task AClientWithoutResilienceMakesOneAttempt()
    {
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable)));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task ExhaustedRetryBudgetReturnsTheFinalScriptedFailure()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(2, TimeSpan.FromSeconds(1), clock, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        scenario.Report.ShouldHaveAttempts(3).ShouldHaveAtMostAttempts(3);
    }

    [Fact]
    public async Task MultipleRetriesReachTheFinalSuccess()
    {
        var clock = Chains.CreateClock();
        using (var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance))
        {
            using var client = Chains.CreateClient(
                scenario.Handler,
                new RetryHandler(3, TimeSpan.FromSeconds(1), clock, Chains.IsRetryableStatus));
            using var request = Chains.Request(HttpMethod.Get);

            using var result = await scenario.SendAsync(client, request);

            result.ShouldHaveStatus(HttpStatusCode.OK);
            scenario.Report.ShouldHaveAttempts(4).ShouldHaveAtMostAttempts(4);
        }
    }

    [Fact]
    public async Task ResponseStatusPredicateDecidesWhatIsRetried()
    {
        var clock = Chains.CreateClock();

        using (var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.InternalServerError),
                HttpFault.Success()),
            clock,
            clock.Advance))
        {
            using var client = Chains.CreateClient(
                scenario.Handler,
                new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus));
            using var request = Chains.Request(HttpMethod.Get);

            using var result = await scenario.SendAsync(client, request);

            Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
            scenario.Report.ShouldHaveAttempts(1);
        }

        using (var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.TooManyRequests),
                HttpFault.Success()),
            clock,
            clock.Advance))
        {
            using var client = Chains.CreateClient(
                scenario.Handler,
                new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus));
            using var request = Chains.Request(HttpMethod.Get);

            using var result = await scenario.SendAsync(client, request);

            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            scenario.Report.ShouldHaveAttempts(2);
        }
    }

    [Fact]
    public async Task NetworkFailuresAreRetriedOnlyWhenTheChainHandlesThem()
    {
        var clock = Chains.CreateClock();

        using (var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.NetworkError(), HttpFault.Success()),
            clock,
            clock.Advance))
        {
            using var client = Chains.CreateClient(
                scenario.Handler,
                new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus, retryExceptions: true));
            using var request = Chains.Request(HttpMethod.Get);

            using var result = await scenario.SendAsync(client, request);

            result.ShouldHaveStatus(HttpStatusCode.OK);
            scenario.Report.ShouldHaveAttempts(2);
            Assert.Equal(HttpAttemptOutcome.NetworkError, scenario.Report.Attempts[0].Outcome);
        }

        using (var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.NetworkError(), HttpFault.Success()),
            clock,
            clock.Advance))
        {
            using var client = Chains.CreateClient(scenario.Handler);
            using var request = Chains.Request(HttpMethod.Get);

            using var result = await scenario.SendAsync(client, request);

            result.ShouldHaveKind(ResilienceResultKind.DownstreamError).ShouldHaveException<HttpRequestException>();
            scenario.Report.ShouldHaveAttempts(1);
            Assert.Null(result.Response);
        }
    }

    [Fact]
    public async Task UnsafeMethodsAreNotRetriedWhenTheChainDisablesThem()
    {
        var clock = Chains.CreateClock();

        using (var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance))
        {
            using var client = Chains.CreateClient(
                scenario.Handler,
                new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus, retryUnsafeMethods: false));
            using var request = Chains.Request(HttpMethod.Post);

            using var result = await scenario.SendAsync(client, request);

            result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
            scenario.Report.ShouldHaveAttempts(1).ShouldNotHaveRetried(HttpMethod.Post);
        }

        using (var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance))
        {
            using var client = Chains.CreateClient(
                scenario.Handler,
                new RetryHandler(1, TimeSpan.Zero, clock, Chains.IsRetryableStatus, retryUnsafeMethods: false));
            using var request = Chains.Request(HttpMethod.Get);

            using var result = await scenario.SendAsync(client, request);

            result.ShouldHaveStatus(HttpStatusCode.OK);
            scenario.Report.ShouldHaveAttempts(2);
        }
    }

    [Fact]
    public async Task TimelineIsCompactAndFreeOfRequestData()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.FromSeconds(2), clock, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        Assert.Equal(2, scenario.Report.Timeline.Count);
        Assert.StartsWith("#1 GET -> response 503 (retry-after 2 s)", scenario.Report.Timeline[0], StringComparison.Ordinal);
        Assert.StartsWith("#2 GET -> response 200", scenario.Report.Timeline[1], StringComparison.Ordinal);
    }
}
