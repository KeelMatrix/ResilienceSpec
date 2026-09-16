using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace KeelMatrix.ResilienceSpec.IntegrationTests;

/// <summary>
/// Proves that the scripted downstream composes with the real Microsoft.Extensions.Http.Resilience handler chain
/// without the resilience layer being bypassed or replaced.
/// </summary>
public sealed class StandardResilienceTests
{
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ServiceUnavailableThenSuccessIsRetriedByTheRealChain()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options => StandardResilienceChains.UseConstantRetry(options, Backoff));
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report
            .ShouldHaveAttempts(2)
            .ShouldHaveMethodSequence(HttpMethod.Get, HttpMethod.Get)
            .ShouldHaveRetryDelay(Backoff)
            .ShouldHaveSettledAtVirtualTime(Backoff);
    }

    [Fact]
    public async Task RemovingTheResilienceHandlerIsTheNegativeControl()
    {
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()));
        using var chain = StandardResilienceChains.Create("orders", scenario, withResilience: false);
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task TheRetryHappensInsideTheResilienceHandler()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options => StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1)));
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.Equal(
            [
                "outer:request",
                "inner:request",
                "inner:response 503",
                "inner:request",
                "inner:response 200",
                "outer:response 200",
            ],
            chain.Observer.Events);
        Assert.Equal(1, chain.Observer.Events.Count(entry => entry == "outer:request"));
    }

    [Fact]
    public async Task DefaultOptionsRetryUnsafeMethods()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options => StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1)));
        using var request = StandardResilienceChains.Request(HttpMethod.Post);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2).ShouldHaveMethodSequence(HttpMethod.Post, HttpMethod.Post);
    }

    [Fact]
    public async Task DisablingUnsafeMethodRetriesStopsPostFromBeingRepeated()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options =>
            {
                StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1));
                options.Retry.DisableForUnsafeHttpMethods();
            });
        using var request = StandardResilienceChains.Request(HttpMethod.Post);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        scenario.Report.ShouldHaveAttempts(1).ShouldNotHaveRetried(HttpMethod.Post);
    }

    [Fact]
    public async Task DisablingUnsafeMethodRetriesStillRetriesSafeMethods()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options =>
            {
                StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1));
                options.Retry.DisableForUnsafeHttpMethods();
            });
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2);
    }

    [Fact]
    public async Task RetryAfterDeltaDrivesTheStandardHandlerDelay()
    {
        var clock = StandardResilienceChains.CreateClock();
        var advertised = TimeSpan.FromSeconds(5);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: advertised),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options => StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(2)));
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report
            .ShouldHaveAttempts(2)
            .ShouldRespectRetryAfter()
            .ShouldHaveRetryDelay(advertised)
            .ShouldHaveSettledAtVirtualTime(advertised);
    }

    [Fact]
    public async Task PerAttemptTimeoutFiresOnTheInjectedClock()
    {
        var clock = StandardResilienceChains.CreateClock();
        var attemptTimeout = TimeSpan.FromSeconds(1);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options =>
            {
                StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1));
                options.AttemptTimeout.Timeout = attemptTimeout;
            });
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report
            .ShouldHaveAttempts(2)
            .ShouldHaveAttemptDuration(1, attemptTimeout)
            .ShouldHaveSettledAtVirtualTime(attemptTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpAttemptOutcome.Abandoned, scenario.Report.Attempts[0].Outcome);
    }

    [Fact]
    public async Task TotalRequestTimeoutSettlesOnTheInjectedClock()
    {
        var clock = StandardResilienceChains.CreateClock();
        var total = TimeSpan.FromSeconds(3);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Always(HttpFault.Timeout()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options =>
            {
                StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1));
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
                options.TotalRequestTimeout.Timeout = total;
            });
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout);
        scenario.Report.ShouldHaveSettledAtVirtualTime(total);
        Assert.True(scenario.Report.AttemptCount >= 2);
    }

    [Fact]
    public async Task ScriptedNetworkFailureSurfacesAfterTheRetryBudget()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Always(HttpFault.NetworkError()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options => StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromMilliseconds(100), maximumRetries: 3));
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveKind(ResilienceResultKind.DownstreamError).ShouldHaveException<HttpRequestException>();
        scenario.Report.ShouldHaveAttempts(4);
    }

    [Fact]
    public async Task CallerCancellationEndsAPendingStandardHandlerRun()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            clock.Advance,
            new ResilienceScenarioOptions { AdvanceClock = false, PendingObservation = TimeSpan.FromMilliseconds(200) });
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options => StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1)));
        using var caller = new CancellationTokenSource();
        using var request = StandardResilienceChains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(chain.Client, request, caller.Token);
        await caller.CancelAsync();
        using var result = await run;

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task AttemptRecordsStayPrivacySafeWithTheRealChain()
    {
        var clock = StandardResilienceChains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var chain = StandardResilienceChains.Create(
            "orders",
            scenario,
            options => StandardResilienceChains.UseConstantRetry(options, TimeSpan.FromSeconds(1)));
        using var request = StandardResilienceChains.SecretRequest();

        using var result = await scenario.SendAsync(chain.Client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        var timeline = scenario.Report.DescribeTimeline();
        foreach (var marker in new[] { "super-secret", "orders.invalid", "/orders/42", "Bearer", "session=" })
        {
            Assert.DoesNotContain(marker, timeline, StringComparison.Ordinal);
        }
    }

}
