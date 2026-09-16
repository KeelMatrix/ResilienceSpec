using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class ClientCompositionTests
{
    [Fact]
    public async Task OuterHandlersSeeOneRequestWhileTheRetryHappensInside()
    {
        var clock = Chains.CreateClock();
        var observer = new ChainObserver();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RecordingHandler(observer, "outer"),
            new RetryHandler(1, TimeSpan.FromSeconds(1), clock, Chains.IsRetryableStatus),
            new RecordingHandler(observer, "inner"));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

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
            observer.Events);
        Assert.Equal(1, observer.Events.Count(entry => entry == "outer:request"));
    }

    [Fact]
    public async Task NamedClientsKeepSeparateScriptsAndReports()
    {
        var clock = Chains.CreateClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);

        using var orders = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);
        using var billing = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()), clock, clock.Advance);

        services.AddHttpClient("orders")
            .AddHttpMessageHandler(() => new RetryHandler(1, TimeSpan.FromSeconds(1), clock, Chains.IsRetryableStatus))
            .UseResilienceSpecDownstream(orders);
        services.AddHttpClient("billing").UseResilienceSpecDownstream(billing);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using (var result = await orders.SendAsync(factory.CreateClient("orders"), Chains.Request(HttpMethod.Get)))
        {
            result.ShouldHaveStatus(HttpStatusCode.OK);
        }

        using (var result = await billing.SendAsync(factory.CreateClient("billing"), Chains.Request(HttpMethod.Get)))
        {
            result.ShouldHaveStatus(HttpStatusCode.OK);
        }

        orders.Report.ShouldHaveAttempts(2);
        billing.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task TypedClientsComposeThroughTheAdapter()
    {
        var clock = Chains.CreateClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);

        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);

        services.AddHttpClient<OrdersClient>()
            .AddHttpMessageHandler(() => new RetryHandler(1, TimeSpan.FromSeconds(1), clock, Chains.IsRetryableStatus))
            .UseResilienceSpecDownstream(scenario);

        using var provider = services.BuildServiceProvider();
        var typed = provider.GetRequiredService<OrdersClient>();

        using var result = await scenario.SendAsync(typed.Client, Chains.Request(HttpMethod.Get));

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2);
    }

    [Fact]
    public void AdapterFailsClearlyWhenTheControllableClockIsNotRegistered()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            clock,
            clock.Advance);
        var services = new ServiceCollection();
        services.AddHttpClient("orders").UseResilienceSpecDownstream(scenario);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var failure = Assert.Throws<MissingTimeProviderException>(() => factory.CreateClient("orders"));
        Assert.Contains("no TimeProvider is registered", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterRejectsMissingArguments()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        Assert.Throws<ArgumentNullException>(() => ResilienceSpecHttpClientBuilderExtensions.UseResilienceSpecDownstream(null!, scenario));

        var services = new ServiceCollection();
        var builder = services.AddHttpClient("orders");
        Assert.Throws<ArgumentNullException>(() => builder.UseResilienceSpecDownstream(null!));
    }

    internal sealed class OrdersClient(HttpClient client)
    {
        internal HttpClient Client { get; } = client;
    }
}

public sealed class CancellationTests
{
    [Fact]
    public async Task CallerCancellationEndsTheRunDeterministically()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            clock,
            clock.Advance,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(200),
            });
        using var client = Chains.CreateClient(scenario.Handler);
        using var caller = new CancellationTokenSource();
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request, caller.Token);
        await caller.CancelAsync();
        using var result = await run;

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
        scenario.Report.ShouldHaveAttempts(1);
        Assert.Equal(HttpAttemptOutcome.Abandoned, scenario.Report.Attempts[0].Outcome);
    }

    [Fact]
    public async Task StrategyTimeoutWithoutCallerCancellationIsReportedAsTimeout()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout).ShouldHaveException<TimeoutException>();
        scenario.Report.ShouldHaveAttempts(1).ShouldHaveAttemptDuration(1, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CallerCancellationRacingAStrategyTimeoutIsReportedAsCanceled()
    {
        var clock = Chains.CreateClock();
        using var caller = new CancellationTokenSource();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            clock.Advance);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock, caller));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request, caller.Token);

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
        Assert.NotNull(result.Exception);
        scenario.Report.ShouldHaveAttempts(1);
    }
}
