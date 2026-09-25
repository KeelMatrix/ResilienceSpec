using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// HttpClientFactory adapter that installs a scripted downstream without replacing the configured resilience
/// behaviour.
/// </summary>
/// <remarks>
/// The core package is usable without dependency injection. This adapter is an ergonomic layer over
/// <see cref="ResilienceScenario.Handler"/> and adds no behaviour of its own beyond a configuration guard for the
/// injected clock. Requests from the resulting client must still be started through
/// <see cref="ResilienceScenario.SendAsync"/>; direct factory-client calls fail with
/// <see cref="ScenarioConsumedException"/> before consuming the scenario.
/// </remarks>
public static class ResilienceSpecHttpClientBuilderExtensions
{
    /// <summary>
    /// Replaces only the primary (innermost) handler of the named or typed client with the scripted downstream of
    /// the given scenario.
    /// </summary>
    /// <param name="builder">The client builder of the client under test.</param>
    /// <param name="scenario">The scenario that owns the scripted downstream and its attempt report.</param>
    /// <returns>The same builder, so the call can be chained.</returns>
    public static IHttpClientBuilder UseResilienceSpecDownstream(this IHttpClientBuilder builder, ResilienceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scenario);

        builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
        {
            if (scenario.TimeProvider is not null && scenario.Options.RequireRegisteredTimeProvider)
            {
                var resolvedTimeProvider = serviceProvider.GetService<TimeProvider>();
                if (resolvedTimeProvider is not null && ReferenceEquals(resolvedTimeProvider, scenario.TimeProvider))
                {
                    scenario.MarkHttpClientFactoryIntegration();
                    return scenario.Handler;
                }

                throw new MissingTimeProviderException(
                    "The scenario expects its controllable clock to drive the resilience pipeline, but the resolved TimeProvider is " +
                    "missing or is a different instance, so the pipeline would not run on the scenario clock. Register the same clock instance before the client is built, " +
                    "for example services.AddSingleton<TimeProvider>(clock.TimeProvider), or set " +
                    "ResilienceScenarioOptions.RequireRegisteredTimeProvider to false when the pipeline time source is configured another way.");
            }

            scenario.MarkHttpClientFactoryIntegration();
            return scenario.Handler;
        });

        return builder;
    }

}
