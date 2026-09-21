using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;

namespace KeelMatrix.ResilienceSpec.IntegrationTests;

/// <summary>Keeps telemetry out of the development and validation loop.</summary>
internal static class TelemetrySuppression
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Suppress() =>
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
}

/// <summary>Records the order in which the positions of one handler chain observed a request.</summary>
internal sealed class ChainObserver
{
    private readonly List<string> _events = new();
    private readonly object _gate = new();

    internal void Add(string value)
    {
        lock (_gate)
        {
            _events.Add(value);
        }
    }

    internal IReadOnlyList<string> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }
}

internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly ChainObserver _observer;
    private readonly string _name;

    internal RecordingHandler(ChainObserver observer, string name)
    {
        _observer = observer;
        _name = name;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _observer.Add($"{_name}:request");
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _observer.Add($"{_name}:response {(int)response.StatusCode}");
        return response;
    }
}

internal sealed record StandardClient(ServiceProvider Provider, HttpClient Client, ChainObserver Observer) : IDisposable
{
    public void Dispose()
    {
        Client.Dispose();
        Provider.Dispose();
    }
}

/// <summary>
/// Assembles a client whose real handler chain keeps Microsoft's standard resilience handler and replaces only the
/// innermost network boundary with the scripted downstream.
/// </summary>
internal static class StandardResilienceChains
{
    internal static readonly DateTimeOffset ClockStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static ResilienceScenarioClock CreateClock()
    {
        var inner = new FakeTimeProvider(ClockStart);
        return new ResilienceScenarioClock(inner, inner.Advance);
    }

    internal static StandardClient Create(
        string name,
        ResilienceScenario scenario,
        Action<HttpStandardResilienceOptions>? configure = null,
        bool withResilience = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        if (scenario.TimeProvider is { } clock)
        {
            services.AddSingleton<TimeProvider>(clock.TimeProvider);
        }

        var observer = new ChainObserver();
        var builder = services.AddHttpClient(name);
        builder.AddHttpMessageHandler(() => new RecordingHandler(observer, "outer"));
        if (withResilience)
        {
            var standard = builder.AddStandardResilienceHandler();
            if (configure is not null)
            {
                standard.Configure(configure);
            }
        }

        builder.AddHttpMessageHandler(() => new RecordingHandler(observer, "inner"));
        builder.UseResilienceSpecDownstream(scenario);

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        return new StandardClient(provider, client, observer);
    }

    /// <summary>Configures a deterministic retry so timing assertions have a fixed expectation.</summary>
    internal static void UseConstantRetry(HttpStandardResilienceOptions options, TimeSpan delay, int maximumRetries = 1)
    {
        options.Retry.MaxRetryAttempts = maximumRetries;
        options.Retry.Delay = delay;
        options.Retry.BackoffType = DelayBackoffType.Constant;
        options.Retry.UseJitter = false;
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path = "/orders/42")
    {
        var request = new HttpRequestMessage(method, new Uri($"https://orders.invalid{path}"));
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Content = new StringContent("{\"order\":42}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    internal static HttpRequestMessage SecretRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://orders.invalid/orders/42?token=super-secret-query"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "super-secret-token");
        request.Headers.Add("Cookie", "session=super-secret-cookie");
        return request;
    }
}
