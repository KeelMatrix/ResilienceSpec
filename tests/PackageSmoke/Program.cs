using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using KeelMatrix.ResilienceSpec;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using PackageSmoke;
using Polly;

// The package is consumed here exactly as a user would consume it: from a restored package, through
// IHttpClientFactory, with Microsoft's standard resilience handler in place and only the terminal network boundary
// replaced. The hosts are reserved '.invalid' names, so a request that left the process could not answer with 200.
// Every operation starts through ResilienceScenario.SendAsync; direct client or terminal-handler sends fail closed
// before consuming a script step or mutating the report.

var failures = new List<string>();
using var observer = new NetworkActivityObserver();
var inMemoryAttempts = 0;

if (args is ["--cold-load-spoof"])
{
    return await RunColdLoadSpoofAsync();
}

var coldLoadExitCode = await RunColdLoadSpoofProcessAsync();
if (coldLoadExitCode != 0)
{
    failures.Add($"cold-load spoof regression exited with code {coldLoadExitCode}");
}

// Load and admit the authentic provider into the parent process before any parent-side spoof checks.
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

await RunAsync("A public system-clock wrapper cannot be wrapped as a deterministic clock", async () =>
{
    var provider = new SystemDelegatingTimeProvider();
    Console.WriteLine($"  type={provider.GetType().FullName}");
    Console.WriteLine($"  assembly={provider.GetType().Assembly.FullName}");
    try
    {
        _ = new ResilienceScenarioClock(provider, static _ => { });
    }
    catch (ArgumentException exception) when (exception.Message.Contains("deterministic timing", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  RESULT=REJECTED");
        await Task.CompletedTask;
        return;
    }

    throw new InvalidOperationException("A public system-clock wrapper was accepted as a deterministic scenario clock.");
});

await RunAsync("A cross-assembly framework-name and assembly-name spoof cannot be wrapped as a deterministic clock", async () =>
{
    var provider = SpoofedTimeProviderFactory.Create();
    if (provider.GetType().FullName != "Microsoft.Extensions.Time.Testing.FakeTimeProvider" ||
        provider.GetType().Assembly.GetName().Name != "Microsoft.Extensions.TimeProvider.Testing")
    {
        throw new InvalidOperationException("The consumer spoof fixture did not preserve the reviewer's runtime identity shape.");
    }

    Console.WriteLine("  LOAD_ORDER=WARM");
    Console.WriteLine("  EVASION=strong-identity cross-assembly name spoof");
    Console.WriteLine($"  type={provider.GetType().FullName}");
    Console.WriteLine($"  assembly={provider.GetType().Assembly.FullName}");
    try
    {
        _ = new ResilienceScenarioClock(provider, static _ => { });
    }
    catch (ArgumentException exception) when (exception.Message.Contains("runtime identity", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  RESULT=REJECTED");
        await Task.CompletedTask;
        return;
    }

    throw new InvalidOperationException("A cross-assembly framework-name and assembly-name spoof was accepted as a deterministic scenario clock.");
});

await RunAsync("A consumer-authored derived provider cannot be wrapped as a deterministic clock", async () =>
{
    var provider = new ConsumerAuthoredTimeProvider();
    Console.WriteLine("  EVASION=consumer-authored derived TimeProvider");
    Console.WriteLine($"  type={provider.GetType().FullName}");
    Console.WriteLine($"  assembly={provider.GetType().Assembly.FullName}");
    try
    {
        _ = new ResilienceScenarioClock(provider, static _ => { });
    }
    catch (ArgumentException exception) when (exception.Message.Contains("runtime identity", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  RESULT=REJECTED");
        await Task.CompletedTask;
        return;
    }

    throw new InvalidOperationException("A consumer-authored derived provider was accepted as a deterministic scenario clock.");
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

async Task<int> RunColdLoadSpoofProcessAsync()
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = Directory.GetCurrentDirectory(),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    startInfo.ArgumentList.Add("--cold-load-spoof");

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start cold-load child process.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    Console.WriteLine("Cold-load spoof child output:");
    Console.Write(output);
    if (!string.IsNullOrWhiteSpace(error))
    {
        Console.Error.WriteLine(error);
    }

    return process.ExitCode;
}

async Task<int> RunColdLoadSpoofAsync()
{
    try
    {
        var spoofAssemblyPath = Environment.GetEnvironmentVariable("RESILIENCE_SPEC_CONSUMER_SPOOF_PATH");
        if (string.IsNullOrWhiteSpace(spoofAssemblyPath))
        {
            throw new InvalidOperationException("The cold-load spoof fixture path was not configured.");
        }

        var spoofAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(spoofAssemblyPath);
        var coldLoadSpoof = (TimeProvider)Activator.CreateInstance(
            spoofAssembly.GetType("Microsoft.Extensions.Time.Testing.FakeTimeProvider", throwOnError: true)!)!;
        Func<AssemblyLoadContext, AssemblyName, Assembly?> coldLoadResolver = (_, name) =>
            string.Equals(name.Name, "Microsoft.Extensions.TimeProvider.Testing", StringComparison.Ordinal)
                ? coldLoadSpoof.GetType().Assembly
                : null;
        AssemblyLoadContext.Default.Resolving += coldLoadResolver;
        try
        {
            Console.WriteLine("LOAD_ORDER=COLD");
            Console.WriteLine("RESOLVER_HOOK=AssemblyLoadContext.Default.Resolving installed");
            Console.WriteLine($"type={coldLoadSpoof.GetType().FullName}");
            Console.WriteLine($"assembly={coldLoadSpoof.GetType().Assembly.FullName}");
            var clock = new ResilienceScenarioClock(coldLoadSpoof, static _ => { });
            Console.WriteLine("RESULT=ADMITTED");

            using var scenario = new ResilienceScenario(
                HttpFaultScript.Sequence(HttpFault.Delay(TimeSpan.FromMilliseconds(20), HttpFault.Success())),
                clock);
            using var client = new HttpClient(scenario.Handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://spoof-endpoint.invalid");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var result = await scenario.SendAsync(client, request, cancellation.Token);
            result.ShouldHaveStatus(HttpStatusCode.OK);

            scenario.Report.ShouldHaveAttemptDuration(1, TimeSpan.FromMilliseconds(20));
            Console.WriteLine("SPOOF_WALL_CLOCK_DURATION_ASSERTION=PASSED");
            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.WriteLine($"RESULT=REJECTED: {exception.Message}");
            return 0;
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= coldLoadResolver;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"COLD_LOAD_ERROR={exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

(TimeProvider Provider, Action<TimeSpan> Advance) CreateRealTimeProvider(DateTimeOffset start)
{
    var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    var providerAssemblyPath = Path.Combine(
        packagesRoot,
        "microsoft.extensions.timeprovider.testing",
        "10.10.0",
        "lib",
        "net8.0",
        "Microsoft.Extensions.TimeProvider.Testing.dll");
    var consumerDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    var consumerAssemblyPath = Path.Combine(consumerDirectory, "Microsoft.Extensions.TimeProvider.Testing.dll");
    if (!File.Exists(consumerAssemblyPath))
    {
        File.Copy(providerAssemblyPath, consumerAssemblyPath);
    }
    var providerAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(consumerAssemblyPath);
    var providerType = providerAssembly.GetType("Microsoft.Extensions.Time.Testing.FakeTimeProvider", throwOnError: true)!;
    var provider = (TimeProvider)Activator.CreateInstance(providerType, start)!;
    var advanceMethod = providerType.GetMethod("Advance", new[] { typeof(TimeSpan) })!;
    return (provider, amount => advanceMethod.Invoke(provider, new object[] { amount }));
}

internal sealed class ConsumerAuthoredTimeProvider : TimeProvider
{
}
