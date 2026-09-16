using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ResilienceSpec.Probe.Runner;

/// <summary>One assembled client under test, with the probe's observation objects.</summary>
internal sealed class ProbeChain : IDisposable
{
    private readonly ServiceProvider _provider;

    public ProbeChain(string name, ServiceProvider provider, HttpClient client, AttemptTimeline timeline, ScriptedTerminalHandler terminal)
    {
        Name = name;
        _provider = provider;
        Client = client;
        Timeline = timeline;
        Terminal = terminal;
    }

    public string Name { get; }

    public HttpClient Client { get; }

    public AttemptTimeline Timeline { get; }

    public ScriptedTerminalHandler Terminal { get; }

    public ServiceProvider Provider => _provider;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Chains.ProbeUri);
        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent("{\"probe\":true}", System.Text.Encoding.UTF8, "application/json");
        }

        return await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Client.Dispose();
        _provider.Dispose();
    }
}

/// <summary>
/// Builds the probe chains. Every chain keeps the application handler chain intact and replaces only the
/// innermost network boundary with the in-memory scripted handler.
/// </summary>
internal static class Chains
{
    public const string ProbeUri = "https://probe.invalid/orders/42";
    public const string OuterObserver = "outer";
    public const string InnerObserver = "inner";
    public const string ControlledPipelineName = "probe-controlled";

    private static readonly List<ScriptedTerminalHandler> Terminals = new();

    /// <summary>Total attempts answered in memory by every terminal handler created during the run.</summary>
    public static int TotalTerminalAttempts
    {
        get
        {
            lock (Terminals)
            {
                return Terminals.Sum(terminal => terminal.AttemptCount);
            }
        }
    }

    /// <summary>A named client with the standard resilience handler and two probe recording positions around it.</summary>
    public static ProbeChain Standard(
        string name,
        IEnumerable<ScriptStep> script,
        Action<HttpStandardResilienceOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null) =>
        Build(
            name,
            script,
            builder =>
            {
                var standard = builder.AddStandardResilienceHandler();
                if (configure is not null)
                {
                    standard.Configure(configure);
                }
            },
            configureServices);

    /// <summary>The negative control: the same scripted downstream with no resilience handler at all.</summary>
    public static ProbeChain WithoutResilience(string name, IEnumerable<ScriptStep> script) => Build(name, script, null, null);

    /// <summary>
    /// A named client whose resilience pipeline is assembled through the public builder API, so the probe can
    /// control the pipeline time source directly.
    /// </summary>
    public static ProbeChain Controlled(
        string name,
        IEnumerable<ScriptStep> script,
        TimeProvider timeProvider,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>> configure,
        Action<IServiceCollection>? configureServices = null) =>
        Build(
            name,
            script,
            builder => builder.AddResilienceHandler(ControlledPipelineName, (pipeline, _) =>
            {
                pipeline.TimeProvider = timeProvider;
                configure(pipeline);
            }),
            configureServices);

    private static ProbeChain Build(
        string name,
        IEnumerable<ScriptStep> script,
        Action<IHttpClientBuilder>? addResilience,
        Action<IServiceCollection>? configureServices)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        configureServices?.Invoke(services);

        var timeline = new AttemptTimeline();
        var terminal = new ScriptedTerminalHandler(script, timeline);
        lock (Terminals)
        {
            Terminals.Add(terminal);
        }

        var builder = services.AddHttpClient(name);
        builder.AddHttpMessageHandler(() => new RecordingDelegatingHandler(OuterObserver, timeline));
        addResilience?.Invoke(builder);
        builder.AddHttpMessageHandler(() => new RecordingDelegatingHandler(InnerObserver, timeline));
        builder.ConfigurePrimaryHttpMessageHandler(() => terminal);

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        return new ProbeChain(name, provider, client, timeline, terminal);
    }
}

/// <summary>Result of a bounded observation window over one request.</summary>
internal sealed record RequestObservation(bool Completed, int TerminalAttempts, TimeSpan WallElapsed, string Outcome);

internal static class Requests
{
    /// <summary>
    /// Runs one request and observes it for a bounded window. The window only distinguishes "completed" from
    /// "still pending"; no timing assertion in this probe depends on an elapsed-time tolerance.
    /// </summary>
    public static async Task<RequestObservation> ObserveAsync(
        ProbeChain chain,
        HttpMethod method,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var request = chain.SendAsync(method, cancellationToken);
        var completed = await CompletedWithinAsync(request, window).ConfigureAwait(false);
        if (!completed)
        {
            return new RequestObservation(false, chain.Terminal.AttemptCount, clock.Elapsed, "still pending");
        }

        return new RequestObservation(true, chain.Terminal.AttemptCount, clock.Elapsed, await DescribeAsync(request).ConfigureAwait(false));
    }

    /// <summary>Awaits a request to completion and describes its final status or exception type.</summary>
    public static async Task<string> DescribeAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request.ConfigureAwait(false);
            return $"status {(int)response.StatusCode} {response.StatusCode}";
        }
        catch (Exception exception)
        {
            return $"exception {exception.GetType().FullName}: {exception.Message}";
        }
    }

    public static async Task<bool> CompletedWithinAsync(Task task, TimeSpan window)
    {
        var completed = await Task.WhenAny(task, Task.Delay(window)).ConfigureAwait(false);
        return ReferenceEquals(completed, task);
    }

    /// <summary>Cancels a pending request and reports whether it completed within a bounded window.</summary>
    public static async Task<bool> CompleteAfterCancelAsync(CancellationTokenSource source, Task pending)
    {
        source.Cancel();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
