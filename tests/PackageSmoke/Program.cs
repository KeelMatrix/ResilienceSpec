using System.Globalization;
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
// Every operation starts through ResilienceScenario.SendAsync; direct client or terminal-handler sends fail closed
// before consuming a script step or mutating the report.
// Telemetry policy: An activation is requested only after a settled scenario has either observed an injected failure
// published by an executed attempt, an executed injected timeout classified as a timeout by the settled client or
// strategy, or a positively recognized native HttpClient.Timeout outcome with at least one executed attempt, and an
// assertion is evaluated at or after settlement. ResponseFault is true only for an HTTP response with status 400 or
// higher published by an executed attempt. ExceptionFault is true only for a network failure published by an
// executed attempt, an executed injected timeout classified as a timeout by the settled client or strategy, or a
// positively recognized native HttpClient.Timeout outcome whose cancellation token is the same token passed to an
// executed scripted attempt and is canceled. Plain caller cancellation, arbitrary upstream timeout exceptions
// (including native-shaped exceptions without that token evidence), unused planned script steps, and observation
// cleanup do not set ExceptionFault. Failure categories accumulate across executed attempts: any case with a
// published response or network category produces one signal containing the accumulated categories, while a case
// with no category produces zero sink signals.

var failures = new List<string>();
using var observer = new NetworkActivityObserver();
var inMemoryAttempts = 0;

// Use the public FakeTimeProvider API directly. Timing admission is based on public TimeProvider observations.
var (realProvider, realAdvance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
_ = new ResilienceScenarioClock(realProvider, realAdvance);
Console.WriteLine("REAL_FRAMEWORK_FAKE_TIME_PROVIDER=ADMITTED timingEligible=True");

await RunAsync("System clock cannot be wrapped as a deterministic clock", async () =>
{
    Console.WriteLine($"  type={TimeProvider.System.GetType().FullName}");
    Console.WriteLine($"  assembly={TimeProvider.System.GetType().Assembly.FullName}");
    try
    {
        _ = new ResilienceScenarioClock(TimeProvider.System, static _ => { });
    }
    catch (ArgumentException exception) when (exception.Message.Contains("controllable", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  RESULT=REJECTED");
        await Task.CompletedTask;
        return;
    }

    throw new InvalidOperationException("TimeProvider.System was accepted as a deterministic scenario clock.");
});

await RunAsync("Retry-After wire values round-trip in the package consumer", async () =>
{
    foreach (var seconds in new[] { 0, 1, int.MaxValue })
    {
        var (underlyingClock, advance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clock = new ResilienceScenarioClock(underlyingClock, advance);
        var expected = TimeSpan.FromSeconds(seconds);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.TooManyRequests, retryAfter: expected)),
            clock);
        using var provider = BuildProvider(
            "retry-after-boundary",
            scenario,
            options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.DisableForUnsafeHttpMethods();
            });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("retry-after-boundary");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://retry-after-boundary.invalid")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.TooManyRequests);
        var retryAfter = result.Response!.Headers.RetryAfter!;
        if (retryAfter.Delta != expected || retryAfter.ToString() != seconds.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"Retry-After wire value {seconds} did not round-trip faithfully.");
        }

        using var parsed = new HttpResponseMessage();
        if (!parsed.Headers.TryAddWithoutValidation("Retry-After", retryAfter.ToString()) ||
            parsed.Headers.RetryAfter!.Delta != expected)
        {
            throw new InvalidOperationException($"Retry-After wire value {seconds} did not parse back to the same delta.");
        }

        inMemoryAttempts += scenario.Report.AttemptCount;
    }

    try
    {
        _ = HttpFault.Response(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(1_500));
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException("A fractional Retry-After value was accepted by the package consumer.");
});

await RunAsync("GET 503 -> 200 through the standard resilience handler", async () =>
{
    var (underlyingClock, advance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var clock = new ResilienceScenarioClock(underlyingClock, advance);
    using var scenario = new ResilienceScenario(
        HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)),
            HttpFault.Success()),
        clock);

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
        .ShouldHaveRetryDelay(TimeSpan.FromSeconds(2))
        .ShouldHaveAttemptDuration(1, TimeSpan.Zero);

    inMemoryAttempts += scenario.Report.AttemptCount;
    Console.WriteLine("REAL_FRAMEWORK_FAKE_TIME_PROVIDER_DURATION_ASSERTION=PASSED");
    Console.WriteLine($"    {scenario.Report.Timeline[0]}");
    Console.WriteLine($"    {scenario.Report.Timeline[1]}");
});

await RunAsync("POST is not retried when unsafe retries are disabled", async () =>
{
    var (underlyingClock, advance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var clock = new ResilienceScenarioClock(underlyingClock, advance);
    using var scenario = new ResilienceScenario(
        HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable),
            HttpFault.Success()),
        clock);

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

await RunAsync("Scenario-owned clock rejects bypass advances", async () =>
{
    await AssertScenarioAdvanceFailsClosedAsync("no-op", () =>
    {
        var (provider, _) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (provider, (Action<TimeSpan>)(_ => { }));
    });
    await AssertScenarioAdvanceFailsClosedAsync("different-provider", () =>
    {
        var (provider, _) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (_, otherAdvance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (provider, otherAdvance);
    });
    await AssertScenarioAdvanceFailsClosedAsync("half-step", () =>
    {
        var (provider, advance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (provider, (Action<TimeSpan>)(amount => advance(amount / 2)));
    });
    await AssertScenarioAdvanceFailsClosedAsync("double-step", () =>
    {
        var (provider, advance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (provider, (Action<TimeSpan>)(amount => advance(amount + amount)));
    });
    await AssertScenarioAdvanceFailsClosedAsync("offset-step", () =>
    {
        var (provider, advance) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (provider, (Action<TimeSpan>)(amount => advance(amount + TimeSpan.FromTicks(1))));
    });
    await AssertScenarioAdvanceFailsClosedAsync("progress-only", () =>
    {
        var (provider, _) = CreateRealTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (provider, (Action<TimeSpan>)(_ => { Thread.Yield(); }));
    });
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

if (inMemoryAttempts != 6)
{
    failures.Add($"expected 6 attempts answered in memory, observed {inMemoryAttempts}");
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

async Task AssertScenarioAdvanceFailsClosedAsync(
    string name,
    Func<(TimeProvider Provider, Action<TimeSpan> Advance)> createAdvance)
{
    var (provider, advance) = createAdvance();
    var clock = new ResilienceScenarioClock(provider, advance);
    using var scenario = new ResilienceScenario(
        HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromSeconds(1), HttpFault.Success())),
        clock,
        new ResilienceScenarioOptions { CleanupTimeout = TimeSpan.FromMilliseconds(100) });
    using var client = new HttpClient(scenario.Handler);
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://clock-binding.invalid");

    try
    {
        using var result = await scenario.SendAsync(client, request);
        throw new InvalidOperationException($"The {name} advance unexpectedly completed a scenario.");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("exactly", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {name}=REJECTED");
    }

    if (scenario.Report.SettledVirtualElapsed is not null)
    {
        throw new InvalidOperationException($"The {name} advance produced settlement timing evidence.");
    }
}

(TimeProvider Provider, Action<TimeSpan> Advance) CreateRealTimeProvider(DateTimeOffset start)
{
    var provider = new FakeTimeProvider(start);
    return (provider, provider.Advance);
}
