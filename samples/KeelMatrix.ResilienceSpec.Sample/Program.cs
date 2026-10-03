using System.Net;
using KeelMatrix.ResilienceSpec;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;

var failures = 0;

// Every operation starts through ResilienceScenario.SendAsync. Direct sends through the configured client or
// terminal handler bypass the logical-call lease and fail closed before consuming a script step or report state.
// Telemetry policy: An activation is requested only after a settled scenario has either observed an injected failure
// published by an executed attempt, an executed injected timeout classified as a timeout by the settled client or
// strategy, or a positively recognized native HttpClient.Timeout outcome with at least one executed attempt, and an
// assertion is evaluated at or after settlement. ResponseFault is true only for an HTTP response with status 400 or
// higher published by an executed attempt. ExceptionFault is true only for a network failure published by an
// executed attempt, an executed injected timeout classified as a timeout by the settled client or strategy, or a
// positively recognized native HttpClient.Timeout outcome whose cancellation token is the same token passed to an
// executed scripted attempt and is canceled. Plain caller cancellation, arbitrary upstream timeout exceptions
// (including native-shaped exceptions without that token evidence), unexecuted script steps, and observation
// cleanup do not set ExceptionFault. Failure categories accumulate across executed attempts: any case with a
// published response or network category produces one signal containing the accumulated categories, while a case
// with no category produces zero sink signals.

await RunAsync("GET 503 -> 200 with the standard resilience handler", async () =>
{
    var underlyingClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var clock = new ResilienceScenarioClock(underlyingClock, underlyingClock.Advance);
    // Supported timer deadlines are targeted directly, including one exactly at the remaining virtual budget.
    using var scenario = new ResilienceScenario(
        HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)),
            HttpFault.Success()),
        clock);

    var services = new ServiceCollection();
    services.AddSingleton<TimeProvider>(clock.TimeProvider);
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

    Console.WriteLine($"    {scenario.Report.DescribeTimeline().Replace(Environment.NewLine, Environment.NewLine + "    ", StringComparison.Ordinal)}");
});

await RunAsync("POST must not be retried when unsafe retries are disabled", async () =>
{
    var underlyingClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var clock = new ResilienceScenarioClock(underlyingClock, underlyingClock.Advance);
    using var scenario = new ResilienceScenario(
        HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable),
            HttpFault.Success()),
        clock);

    var services = new ServiceCollection();
    services.AddSingleton<TimeProvider>(clock.TimeProvider);
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

    Console.WriteLine($"    {scenario.Report.DescribeTimeline().Replace(Environment.NewLine, Environment.NewLine + "    ", StringComparison.Ordinal)}");
});

Console.WriteLine(failures == 0
    ? "Sample completed: every documented expectation held."
    : $"Sample failed: {failures} scenario(s) did not meet their expectation.");

return failures == 0 ? 0 : 1;

async Task RunAsync(string title, Func<Task> scenario)
{
    Console.WriteLine(title);
    try
    {
        await scenario();
        Console.WriteLine("  PASS");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"  FAIL {exception.GetType().Name}: {exception.Message}");
    }
}
