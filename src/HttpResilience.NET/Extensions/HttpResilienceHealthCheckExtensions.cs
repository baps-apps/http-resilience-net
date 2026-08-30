using HttpResilience.NET.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Exposes circuit breaker state as an ASP.NET Core health check, for diagnostics -- not for probes.
/// </summary>
public static class HttpResilienceHealthCheckExtensions
{
    /// <summary>The tag applied by default, marking this as a dependency check rather than a probe.</summary>
    public const string DependencyTag = "dependency";

    private const string DefaultName = "http-resilience";

    /// <summary>
    /// Registers a health check reporting the state of every circuit breaker this library configures.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The health check name. Defaults to <c>http-resilience</c>.</param>
    /// <param name="tags">Tags used to select the check. Defaults to <see cref="DependencyTag"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Use it to answer "which of my dependencies is this pod currently refusing to call, and why is latency
    /// suddenly low?" during an incident. The response body names each breaker as
    /// <c>client -&gt; authority</c> with its state, so it tells you which dependency and -- under
    /// per-authority selection -- which host.
    /// <para>
    /// The check reports <see cref="HealthStatus.Degraded"/> at worst, never
    /// <see cref="HealthStatus.Unhealthy"/>, and is tagged <see cref="DependencyTag"/> so a probe that selects
    /// no tags will not pick it up.
    /// </para>
    /// <para>
    /// <b>Do not gate a Kubernetes liveness or readiness probe on this.</b> An open circuit means a downstream
    /// dependency is unhealthy, not that this process is. Restarting the pod or removing it from the load
    /// balancer would shed capacity during a dependency outage and amplify it -- turning a partial degradation
    /// into a self-inflicted one. Map it to a separate diagnostic endpoint instead.
    /// </para>
    /// <para>
    /// Idempotent under one <paramref name="name"/>, for the same reason
    /// <see cref="HttpResilienceServiceCollectionExtensions.AddHttpResilience(IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration)"/>
    /// is: a shared platform registration extension and the application that uses it may both ask for the
    /// dependency check without coordinating. A second call under a <i>different</i> name is a deliberate
    /// second registration and is honoured.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilienceHealthChecks();
    ///
    /// app.MapHealthChecks("/healthz/live",  new() { Predicate = _ =&gt; false });
    /// app.MapHealthChecks("/healthz/ready", new() { Predicate = r =&gt; !r.Tags.Contains("dependency") });
    /// app.MapHealthChecks("/healthz/deps",  new() { Predicate = r =&gt;  r.Tags.Contains("dependency") });
    /// </code>
    /// </example>
    public static IServiceCollection AddHttpResilienceHealthChecks(
        this IServiceCollection services,
        string name = DefaultName,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddHealthChecks().AddHttpResilience(name, tags);
        return services;
    }

    /// <summary>
    /// Registers the circuit breaker health check on an existing <see cref="IHealthChecksBuilder"/>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check name. Defaults to <c>http-resilience</c>.</param>
    /// <param name="tags">Tags used to select the check. Defaults to <see cref="DependencyTag"/>.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// The same registration as
    /// <see cref="AddHttpResilienceHealthChecks(IServiceCollection, string, IEnumerable{string})"/>, in the
    /// shape the rest of the health-check ecosystem uses, so this check reads like every other one in a
    /// service's startup. See that method for what the check reports and why it must not gate a probe.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHealthChecks()
    ///     .AddHttpResilience()
    ///     .AddNpgSql(connectionString);
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddHttpResilience(
        this IHealthChecksBuilder builder,
        string name = DefaultName,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // The tracker is registered by AddHttpResilience; TryAdd keeps a container that only registered
        // health checks working, and never replaces the instance the metrics gauge reads.
        builder.Services.TryAddSingleton<CircuitBreakerStateTracker>();

        string[] resolvedTags = tags is null ? [DependencyTag] : [.. tags];

        // Idempotent, and deliberately not via IHealthChecksBuilder.Add, which appends unconditionally:
        // HealthCheckService rejects duplicate names at resolve time, so a shared platform extension and the
        // application that uses it calling this without coordinating would fail the host with a message
        // naming neither this package nor the call to remove. Every other entry point here already survives
        // that pattern; this one was the exception.
        builder.Services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (HealthCheckRegistration existing in options.Registrations)
            {
                if (string.Equals(existing.Name, name, StringComparison.Ordinal))
                {
                    return;
                }
            }

            options.Registrations.Add(new HealthCheckRegistration(
                name,
                serviceProvider => new HttpResilienceHealthCheck(
                    serviceProvider.GetRequiredService<CircuitBreakerStateTracker>()),
                failureStatus: HealthStatus.Degraded,
                tags: resolvedTags));
        });

        return builder;
    }
}
