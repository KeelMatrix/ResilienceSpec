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
/// injected clock.
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

        var services = builder.Services;
        builder.ConfigurePrimaryHttpMessageHandler(() =>
        {
            if (scenario.TimeProvider is not null &&
                scenario.Options.RequireRegisteredTimeProvider &&
                !HasTimeProvider(services))
            {
                throw new MissingTimeProviderException(
                    "The scenario expects its controllable clock to drive the resilience pipeline, but no TimeProvider is registered " +
                    "in the service collection, so the pipeline would run on the system clock. Register the clock before the client is built, " +
                    "for example services.AddSingleton<TimeProvider>(clock), or set " +
                    "ResilienceScenarioOptions.RequireRegisteredTimeProvider to false when the pipeline time source is configured another way.");
            }

            scenario.MarkHttpClientFactoryIntegration();
            return scenario.Handler;
        });

        return builder;
    }

    private static bool HasTimeProvider(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(TimeProvider))
            {
                return true;
            }
        }

        return false;
    }
}
