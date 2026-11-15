using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Runs this package's handler mechanism against the platform version the application resolved, so a release of
/// <c>Microsoft.Extensions.Http</c> that stops running actions in registration order fails host start instead of
/// building misordered pipelines.
/// </summary>
/// <remarks>
/// The order is relied on but undocumented (CLAUDE.md, rule 7, "Accepted risk"); the weekly drift run sees a new
/// platform only after consumers can resolve it. The check builds two clients in a private container: one with a
/// concurrency limit and a handler after this package's, one disabled, both under a defaults standard handler
/// registered after them. It asserts the resulting chains and the finite <see cref="HttpClient.Timeout"/>. Once per
/// process: the platform cannot change while it runs. Its gauges are published under a meter name nothing exports.
/// </remarks>
internal sealed class PlatformSelfCheck
{
    private const string EnabledClient = "HttpResilience.NET.self-check";
    private const string DisabledClient = "HttpResilience.NET.self-check-disabled";

    private static readonly Lazy<string?> ForThisProcess = new(() => Run(perturb: null));

    private readonly Action<IServiceCollection>? _perturb;

    public PlatformSelfCheck()
    {
    }

    /// <param name="perturb">Applied after the canary's registrations: stands in for a platform behaving differently.</param>
    internal PlatformSelfCheck(Action<IServiceCollection> perturb) => _perturb = perturb;

    /// <summary>Why the platform failed the check, or <see langword="null"/> when it passed.</summary>
    public string? Failure => _perturb is null ? ForThisProcess.Value : Run(_perturb);

    public void ThrowIfFailed()
    {
        if (Failure is { } failure)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private static string? Run(Action<IServiceCollection>? perturb)
    {
        string? finding;
        try
        {
            finding = Inspect(perturb);
        }
#pragma warning disable CA1031 // Whatever the mechanism throws on this platform is the finding.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            finding = $"building its canary clients threw {exception.GetType().Name}: {exception.Message}";
        }

        return finding is null
            ? null
            : $"HttpResilience.NET's platform self-check failed on Microsoft.Extensions.Http {Version<IHttpClientFactory>()} " +
              $"and Microsoft.Extensions.Http.Resilience {Version<ResilienceHandler>()}: {finding} This package relies on " +
              "IHttpClientFactory running HttpClientFactoryOptions.HttpMessageHandlerBuilderActions and " +
              "HttpClientActions in registration order, ConfigureHttpClientDefaults actions first; the platform does " +
              "so without documenting it, and these versions appear to differ. Resolve the platform versions this " +
              "HttpResilience.NET release was tested with, and report the versions above. " +
              "HttpResilience:ValidateClientsOnStart: false skips this check, and the client probe, at the risk of " +
              "running misordered pipelines.";
    }

    private static string? Inspect(Action<IServiceCollection>? perturb)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeterFactory, MeterFactory>();
        services.AddHttpResilience(static _ => { });

        services.AddHttpClient(EnabledClient)
            .AddHttpResilienceHandler(static options =>
            {
                options.ConcurrencyLimiter.Enabled = true;
                options.ConcurrencyLimiter.Limit = 1;
            })
            .AddHttpMessageHandler(static () => new Tail())
            .ConfigurePrimaryHttpMessageHandler(static () => new Primary());

        services.AddHttpClient(DisabledClient)
            .AddHttpResilienceHandler(static options => options.Enabled = false)
            .AddHttpMessageHandler(static () => new Tail())
            .ConfigurePrimaryHttpMessageHandler(static () => new Primary());

        // After the clients: defaults actions must still run first, be replaced on the enabled client and removed
        // from the disabled one.
        services.ConfigureHttpClientDefaults(static defaults => defaults.AddStandardResilienceHandler());

        perturb?.Invoke(services);

        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpMessageHandlerFactory handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        // Concurrency limiter, then the package's standard pipeline (the defaults' replaced), then the tail.
        string enabled = Shape(handlers.CreateHandler(EnabledClient));
        if (enabled != "ResilienceHandler ResilienceHandler Tail")
        {
            return $"A client with a concurrency limit and a later handler was built as [{enabled}], expected " +
                   "[ResilienceHandler ResilienceHandler Tail].";
        }

        // Nothing of the package's, and the defaults' handler removed: it would retry unsafe methods.
        string disabled = Shape(handlers.CreateHandler(DisabledClient));
        if (disabled != "Tail")
        {
            return $"A disabled client was built as [{disabled}], expected [Tail].";
        }

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(EnabledClient);
        if (client.Timeout == Timeout.InfiniteTimeSpan)
        {
            return "The platform handler's infinite HttpClient.Timeout ran after this package's finite one.";
        }

        return null;
    }

    /// <summary>The resilience handlers, this package's own handlers and the canary's tail, outermost first.</summary>
    private static string Shape(HttpMessageHandler handler)
    {
        List<string> shape = [];
        for (HttpMessageHandler? current = handler; current is DelegatingHandler delegating; current = delegating.InnerHandler)
        {
            if (current is ResilienceHandler)
            {
                shape.Add(nameof(ResilienceHandler));
            }
            else if (current.GetType().Assembly == typeof(PlatformSelfCheck).Assembly)
            {
                // A tail, or a marker or handler of this package's left where it should not be.
                shape.Add(current.GetType().Name);
            }
        }

        return string.Join(' ', shape);
    }

    /// <summary>The package version, without the commit hash the informational version carries.</summary>
    private static string Version<T>()
    {
        string? version = typeof(T).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(T).Assembly.GetName().Version?.ToString();

        return version is null ? "(unknown version)" : version.Split('+')[0];
    }

    private sealed class Tail : DelegatingHandler;

    private sealed class Primary : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The self-check sends no requests.");
    }

    /// <summary>
    /// The canary's meters, renamed so an exporter listening for this package's or Polly's meter never sees them,
    /// and disposed with the private container.
    /// </summary>
    internal sealed class MeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(new MeterOptions($"HttpResilience.NET.SelfCheck.{options.Name}")
            {
                Version = options.Version,
                Tags = options.Tags,
                Scope = this
            });

            lock (_meters)
            {
                _meters.Add(meter);
            }

            return meter;
        }

        public void Dispose()
        {
            lock (_meters)
            {
                foreach (Meter meter in _meters)
                {
                    meter.Dispose();
                }

                _meters.Clear();
            }
        }
    }
}
