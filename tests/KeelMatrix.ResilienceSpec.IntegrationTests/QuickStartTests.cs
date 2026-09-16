using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;
using Xunit;

namespace KeelMatrix.ResilienceSpec.IntegrationTests;

/// <summary>
/// Executes the documented quick-start examples exactly as the README and the package README present them, so the
/// published examples cannot drift from the shipped behaviour.
/// </summary>
public sealed class QuickStartTests
{
    [Fact]
    public async Task ReadmeQuickStartRetriesAndHonoursRetryAfter()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)),
                HttpFault.Success()),
            clock,
            clock.Advance);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("orders")
            .UseResilienceSpecDownstream(scenario)
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = TimeSpan.FromSeconds(2);
                options.Retry.BackoffType = DelayBackoffType.Constant;
                options.Retry.UseJitter = false;
            });

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://orders.invalid/orders/42");
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report
            .ShouldHaveAttempts(2)
            .ShouldHaveMethodSequence(HttpMethod.Get, HttpMethod.Get)
            .ShouldRespectRetryAfter();
    }

    [Fact]
    public async Task ReadmePostExampleDoesNotRetryUnsafeRequests()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            clock.Advance);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("payments")
            .UseResilienceSpecDownstream(scenario)
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.Delay = TimeSpan.FromSeconds(1);
                options.Retry.BackoffType = DelayBackoffType.Constant;
                options.Retry.UseJitter = false;
                options.Retry.DisableForUnsafeHttpMethods();
            });

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("payments");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://payments.invalid/payments")
        {
            Content = new StringContent("{\"amount\":10}", System.Text.Encoding.UTF8, "application/json"),
        };
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
        scenario.Report.ShouldHaveAttempts(1).ShouldNotHaveRetried(HttpMethod.Post);
    }

    [Fact]
    public async Task ReadmeExceptionExampleReportsTheHandledNetworkFailure()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.NetworkError(), HttpFault.Success()),
            clock,
            clock.Advance);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("orders")
            .UseResilienceSpecDownstream(scenario)
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = TimeSpan.FromSeconds(1);
                options.Retry.BackoffType = DelayBackoffType.Constant;
                options.Retry.UseJitter = false;
            });

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://orders.invalid/orders/42");
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2);
        Assert.Equal(HttpAttemptOutcome.NetworkError, scenario.Report.Attempts[0].Outcome);
    }

    [Fact]
    public async Task ReadmeCancellationExampleReportsCancellation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            clock.Advance,
            new ResilienceScenarioOptions { AdvanceClock = false, PendingObservation = TimeSpan.FromMilliseconds(200) });

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddHttpClient("orders")
            .UseResilienceSpecDownstream(scenario)
            .AddStandardResilienceHandler();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://orders.invalid/orders/42");
        var run = scenario.SendAsync(client, request, caller.Token);
        await caller.CancelAsync();
        using var result = await run;

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
    }
}
