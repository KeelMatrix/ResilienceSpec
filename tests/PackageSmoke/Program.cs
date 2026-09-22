using System.Diagnostics;
using System.Net;
using KeelMatrix.ResilienceSpec;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using PackageSmoke;
using Polly;

// The package is consumed here exactly as a user would consume it: from a restored package, through
// IHttpClientFactory, with Microsoft's standard resilience handler in place and only the terminal network boundary
// replaced. The hosts are reserved '.invalid' names, so a request that left the process could not answer with 200.

var failures = new List<string>();
using var observer = new NetworkActivityObserver();
var inMemoryAttempts = 0;

await RunAsync("System clock cannot be wrapped as a deterministic clock", async () =>
{
    try
    {
        _ = new ResilienceScenarioClock(TimeProvider.System, static _ => { });
    }
    catch (ArgumentException exception) when (exception.Message.Contains("controllable", StringComparison.OrdinalIgnoreCase))
    {
        await Task.CompletedTask;
        return;
    }

    throw new InvalidOperationException("TimeProvider.System was accepted as a deterministic scenario clock.");
});

await RunAsync("GET 503 -> 200 through the standard resilience handler", async () =>
{
    var underlyingClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var clock = new ResilienceScenarioClock(underlyingClock, underlyingClock.Advance);
    using var scenario = new ResilienceScenario(
        HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)),
            HttpFault.Success()),
        clock.TimeProvider,
        clock.Advance);

    using var provider = BuildProvider("orders", scenario, options =>
    {
        options.Retry.MaxRetryAttempts = 1;
        options.Retry.Delay = TimeSpan.FromSeconds(2);
        options.Retry.BackoffType = DelayBackoffType.Constant;
        options.Retry.UseJitter = false;
    });

    var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://orders-endpoint.invalid/orders/42");
    using var result = await scenario.SendAsync(client, request);

    result.ShouldHaveStatus(HttpStatusCode.OK);
    scenario.Report
        .ShouldHaveAttempts(2)
        .ShouldHaveMethodSequence(HttpMethod.Get, HttpMethod.Get)
        .ShouldRespectRetryAfter()
        .ShouldHaveRetryDelay(TimeSpan.FromSeconds(2));

    inMemoryAttempts += scenario.Report.AttemptCount;
    Console.WriteLine($"    {scenario.Report.Timeline[0]}");
    Console.WriteLine($"    {scenario.Report.Timeline[1]}");
});

await RunAsync("POST is not retried when unsafe retries are disabled", async () =>
{
    var underlyingClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var clock = new ResilienceScenarioClock(underlyingClock, underlyingClock.Advance);
    using var scenario = new ResilienceScenario(
        HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable),
            HttpFault.Success()),
        clock.TimeProvider,
        clock.Advance);

    using var provider = BuildProvider("payments", scenario, options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Constant;
        options.Retry.UseJitter = false;
        options.Retry.DisableForUnsafeHttpMethods();
    });

    var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("payments");
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://payments-endpoint.invalid/payments")
    {
        Content = new StringContent("{\"amount\":10}", System.Text.Encoding.UTF8, "application/json"),
    };
    using var result = await scenario.SendAsync(client, request);

    result.ShouldHaveStatus(HttpStatusCode.ServiceUnavailable);
    scenario.Report.ShouldHaveAttempts(1).ShouldNotHaveRetried(HttpMethod.Post);

    inMemoryAttempts += scenario.Report.AttemptCount;
    Console.WriteLine($"    {scenario.Report.Timeline[0]}");
});

Console.WriteLine(
    $"Attempts answered in memory: {inMemoryAttempts}; reserved '.invalid' hosts were never resolved.");
Console.WriteLine(
    "Telemetry: this run sets KEELMATRIX_NO_TELEMETRY=1, so the transport measurement below covers the "
    + "verification path only; the optional telemetry transport is not exercised.");
foreach (var line in observer.DescribeEvents())
{
    Console.WriteLine($"Runtime transport events: {line}");
}

if (observer.UncreatedSources.Count > 0)
{
    Console.WriteLine(
        $"Transport EventSources never created during this run: {string.Join(", ", observer.UncreatedSources)}");
}

if (observer.TotalEvents != 0)
{
    failures.Add($"the smoke run observed {observer.TotalEvents} runtime transport event(s)");
}

if (inMemoryAttempts != 3)
{
    failures.Add($"expected 3 attempts answered in memory, observed {inMemoryAttempts}");
}

if (Environment.GetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY") != "1")
{
    failures.Add(
        "the zero-transport measurement is only valid under the documented telemetry opt-out, which this run did not set");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Package smoke failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    return 1;
}

Console.WriteLine(
    "Package smoke completed without a listener, socket, or name-resolution dependency on the verification path.");
return 0;

ServiceProvider BuildProvider(string name, ResilienceScenario scenario, Action<HttpStandardResilienceOptions> configure)
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddMetrics();
    services.AddSingleton<TimeProvider>(scenario.TimeProvider!);
    services.AddHttpClient(name)
        .UseResilienceSpecDownstream(scenario)
        .AddStandardResilienceHandler()
        .Configure(configure);
    return services.BuildServiceProvider();
}

async Task RunAsync(string title, Func<Task> scenario)
{
    Console.WriteLine(title);
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await scenario();
        Console.WriteLine($"  PASS ({stopwatch.ElapsedMilliseconds} ms)");
    }
    catch (Exception exception)
    {
        failures.Add($"{title}: {exception.GetType().Name}: {exception.Message}");
        Console.WriteLine($"  FAIL {exception.GetType().Name}");
    }
}
