using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Fallback;
using Polly.RateLimiting;
using HttpResilience.NET.Abstractions;
using HttpResilience.NET.Configuration;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Extensions;

/// <summary>
/// Extension methods for registering HTTP resilience options and configuring HttpClient with resilience.
/// Used by multiple applications to get consistent retry, circuit breaker, timeouts, and optional rate limiting, fallback, and hedging.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HttpResilienceOptions"/> bound to the "HttpResilienceOptions" configuration section,
    /// validates all options (data annotations and allowed values), and runs validation at startup (only when <see cref="HttpResilienceOptions.Enabled"/> is true) so misconfiguration fails fast.
    /// <para><b>Use case:</b> Call once during app startup (e.g. in <c>Program.cs</c> or <c>Startup.ConfigureServices</c>) before registering any HTTP client that uses
    /// <see cref="AddHttpClientWithResilience(IHttpClientBuilder,IConfiguration,int?)"/>. Use the same <paramref name="configuration"/> instance when calling that extension.</para>
    /// </summary>
    /// <param name="services">The service collection (e.g. from dependency injection).</param>
    /// <param name="configuration">Application configuration (e.g. from <c>IConfiguration</c>) that contains the "HttpResilienceOptions" section.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHttpResilienceOptions(this IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(HttpResilienceConfigurationKeys.HttpResilienceOptions);
        return services.AddHttpResilienceOptions(section);
    }

    /// <summary>
    /// Registers <see cref="HttpResilienceOptions"/> bound to a specific configuration section,
    /// validates all options (data annotations and allowed values), and runs validation at startup (only when <see cref="HttpResilienceOptions.Enabled"/> is true) so misconfiguration fails fast.
    /// <para>
    /// <b>Use case:</b> Use when your application stores HTTP resilience options under a non-default section name
    /// (for example, when sharing a single configuration root across multiple tenants or environments) and you want to bind
    /// a particular <see cref="IConfigurationSection"/> as the source of truth for <see cref="HttpResilienceOptions"/>.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">Configuration section that contains the HTTP resilience options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHttpResilienceOptions(this IServiceCollection services, IConfigurationSection section)
    {
        services.AddOptions<HttpResilienceOptions>()
            .Bind(section)
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>();
        return services;
    }

    private static bool IsValidPipelineOrder(List<string>? order)
    {
        if (order is null || order.Count == 0)
            return false;

        var allowed = PipelineStrategyNames.Allowed;
        int standardOrHedging = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in order)
        {
            if (string.IsNullOrWhiteSpace(item) || !allowed.Contains(item))
                return false;
            if (!seen.Add(item))
                return false;

            if (string.Equals(item, PipelineStrategyNames.Standard, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase))
            {
                standardOrHedging++;
            }
        }

        return standardOrHedging == 1;
    }

    private sealed class HttpResilienceOptionsValidator : IValidateOptions<HttpResilienceOptions>
    {
        private static readonly DataAnnotationValidateOptions<HttpResilienceOptions> _dataAnnotations =
            new(Microsoft.Extensions.Options.Options.DefaultName);

        public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
        {
            // When the master switch is off, skip validation entirely so startup never fails.
            if (!options.Enabled)
                return ValidateOptionsResult.Success;

            var failures = new List<string>();

            // Run data-annotation validation, but ignore failures that come from
            // nested sections that are effectively disabled.
            var dataAnnotationsResult = _dataAnnotations.Validate(name, options);
            if (dataAnnotationsResult is { Failed: true, Failures: { } daFailures })
            {
                foreach (var failure in daFailures)
                {
                    if (ShouldIgnoreFailureForDisabledSection(failure, options))
                        continue;

                    failures.Add(failure);
                }
            }

            // Root-level and always-on validations
            if (options.Connection.Enabled && options.Connection.MaxConnectionsPerServer is < 1 or > 1000)
                failures.Add("Connection.MaxConnectionsPerServer must be between 1 and 1000.");

            if (options.Timeout.AttemptTimeoutSeconds > options.Timeout.TotalRequestTimeoutSeconds)
                failures.Add("Timeout.AttemptTimeoutSeconds must be less than or equal to Timeout.TotalRequestTimeoutSeconds.");

            if (!Enum.IsDefined(options.Retry.BackoffType))
                failures.Add("Retry.BackoffType must be Constant, Linear, or Exponential.");

            if (!IsValidPipelineOrder(options.PipelineOrder))
                failures.Add("PipelineOrder is required when Enabled is true and must contain only Fallback, Bulkhead, RateLimiter, Standard, Hedging with exactly one of Standard or Hedging. Example: [\"Standard\"] or [\"Fallback\", \"Bulkhead\", \"Standard\"].");

            if (!Enum.IsDefined(options.PipelineSelection.Mode))
                failures.Add("PipelineSelection.Mode must be None or ByAuthority.");

            // Nested feature-specific validations – only when nested feature is enabled / active.

            if (options.Fallback.Enabled &&
                options.Fallback.StatusCode is < 400 or > 599)
                failures.Add("Fallback.StatusCode must be between 400 and 599 when Fallback.Enabled is true.");

            if (options.RateLimiter.Enabled &&
                !Enum.IsDefined(options.RateLimiter.Algorithm))
                failures.Add("RateLimiter.Algorithm must be FixedWindow, SlidingWindow, or TokenBucket.");

            // Hedging options should only be validated when the pipeline is actually hedging.
            bool isHedging = options.PipelineOrder?.Exists(s =>
                string.Equals(s, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase)) ?? false;

            if (isHedging)
            {
                if (options.Hedging.DelaySeconds is < 0 or > 60)
                    failures.Add("Hedging.DelaySeconds must be between 0 and 60.");

                if (options.Hedging.MaxHedgedAttempts is < 0 or > 10)
                    failures.Add("Hedging.MaxHedgedAttempts must be between 0 and 10.");
            }

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }

        private static bool ShouldIgnoreFailureForDisabledSection(string failure, HttpResilienceOptions options)
        {
            // If root is disabled we never get here (short-circuited earlier).

            // Connection: only validate when the feature is enabled.
            if (!options.Connection.Enabled &&
                failure.Contains("Connection", StringComparison.OrdinalIgnoreCase))
                return true;

            // RateLimiter: only validate when the feature is enabled.
            if (!options.RateLimiter.Enabled &&
                failure.Contains("RateLimiter", StringComparison.OrdinalIgnoreCase))
                return true;

            // Fallback: only validate when the feature is enabled.
            if (!options.Fallback.Enabled &&
                failure.Contains("Fallback", StringComparison.OrdinalIgnoreCase))
                return true;

            // Bulkhead: only validate when the feature is enabled.
            if (!options.Bulkhead.Enabled &&
                failure.Contains("Bulkhead", StringComparison.OrdinalIgnoreCase))
                return true;

            // Hedging: only validate when the pipeline type is Hedging.
            bool isHedging = options.PipelineOrder?.Exists(s =>
                string.Equals(s, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!isHedging &&
                failure.Contains("Hedging", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }

    /// <summary>
    /// When <see cref="HttpResilienceOptions.Enabled"/> is true, configures the HttpClient with a primary <see cref="SocketsHttpHandler"/> (connection pooling, timeouts)
    /// and a resilience pipeline determined by <see cref="HttpResilienceOptions.PipelineOrder"/>: Standard (retry, circuit breaker, timeouts, optional rate limiting) or Hedging (multiple requests, first success wins).
    /// Optionally adds fallback and concurrency handlers when enabled in options. When Enabled is false or not set, returns the builder unchanged (no resilience applied).
    /// <para><b>Use case:</b> Use for typical outgoing HTTP calls (APIs, microservices). Include "Hedging" in <c>PipelineOrder</c> in config for latency-sensitive calls to multiple replicas. Call after <c>AddHttpClient("MyClient", ...)</c> (or overloads).
    /// Call <see cref="AddHttpResilienceOptions(IServiceCollection,IConfiguration)"/> first with the same <paramref name="configuration"/>. Pass <paramref name="requestTimeoutSeconds"/> to override the total timeout for this client (e.g. longer for background jobs).</para>
    /// </summary>
    /// <param name="builder">The HttpClient builder from <c>AddHttpClient("MyClient", ...)</c>.</param>
    /// <param name="configuration">Same configuration instance used in <see cref="AddHttpResilienceOptions(IServiceCollection,IConfiguration)"/> (must contain "HttpResilienceOptions" section).</param>
    /// <param name="requestTimeoutSeconds">Total request timeout in seconds for this client; when null, uses <see cref="TimeoutOptions.TotalRequestTimeoutSeconds"/> from configuration.</param>
    /// <returns>The HttpClient builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpClientWithResilience(this IHttpClientBuilder builder, IConfiguration configuration, int? requestTimeoutSeconds = null)
        => AddHttpClientWithResilience(builder, configuration, requestTimeoutSeconds, fallbackHandler: null);

    /// <summary>
    /// Same as <see cref="AddHttpClientWithResilience(IHttpClientBuilder,IConfiguration,int?)"/> but binds options from a specific configuration section.
    /// <para>
    /// <b>Use case:</b> When your application stores multiple HTTP resilience option sets under different section names
    /// (for example, per-tenant or per-environment) and you want a given HttpClient to use a particular section
    /// without changing the global "HttpResilienceOptions" binding.
    /// </para>
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="section">Configuration section that contains the HTTP resilience options for this client.</param>
    /// <param name="requestTimeoutSeconds">Total request timeout in seconds; when null, uses the section's <see cref="TimeoutOptions.TotalRequestTimeoutSeconds"/>.</param>
    /// <returns>The HttpClient builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpClientWithResilience(this IHttpClientBuilder builder, IConfigurationSection section, int? requestTimeoutSeconds = null)
        => AddHttpClientWithResilience(builder, section, requestTimeoutSeconds, fallbackHandler: null, configurePipeline: null);

    /// <summary>
    /// Same as <see cref="AddHttpClientWithResilience(IHttpClientBuilder,IConfiguration,int?)"/> with an optional custom fallback handler.
    /// When <paramref name="fallbackHandler"/> is set and fallback is enabled, it is invoked first on failure; if it returns a response, that is used; otherwise the synthetic response from options is used.
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="configuration">Configuration containing "HttpResilienceOptions" section.</param>
    /// <param name="requestTimeoutSeconds">Total request timeout in seconds; when null, uses config.</param>
    /// <param name="fallbackHandler">Optional custom fallback handler (e.g. from DI: <c>sp =&gt; sp.GetService&lt;IHttpFallbackHandler&gt;()</c>).</param>
    /// <returns>The HttpClient builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpClientWithResilience(this IHttpClientBuilder builder, IConfiguration configuration, int? requestTimeoutSeconds, IHttpFallbackHandler? fallbackHandler)
        => AddHttpClientWithResilience(builder, configuration, requestTimeoutSeconds, fallbackHandler, configurePipeline: null);

    /// <summary>
    /// Same as <see cref="AddHttpClientWithResilience(IHttpClientBuilder,IConfiguration,int?,IHttpFallbackHandler?)"/> with an optional delegate to add custom resilience handlers.
    /// <paramref name="configurePipeline"/> is invoked after all built-in handlers are added; any handlers added there are outermost (execute first). Use to add custom Polly strategies (e.g. custom retry, logging).
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="configuration">Configuration containing "HttpResilienceOptions" section.</param>
    /// <param name="requestTimeoutSeconds">Total request timeout in seconds; when null, uses config.</param>
    /// <param name="fallbackHandler">Optional custom fallback handler.</param>
    /// <param name="configurePipeline">Optional delegate to add custom resilience handlers (outermost). Called with the same <paramref name="builder"/>; e.g. <c>b =&gt; b.AddResilienceHandler("custom", ...)</c>.</param>
    /// <returns>The HttpClient builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpClientWithResilience(
        this IHttpClientBuilder builder,
        IConfiguration configuration,
        int? requestTimeoutSeconds,
        IHttpFallbackHandler? fallbackHandler,
        Action<IHttpClientBuilder>? configurePipeline)
    {
        IConfigurationSection section = configuration.GetSection(HttpResilienceConfigurationKeys.HttpResilienceOptions);
        return AddHttpClientWithResilience(builder, section, requestTimeoutSeconds, fallbackHandler, configurePipeline);
    }

    /// <summary>
    /// Core implementation that binds <see cref="HttpResilienceOptions"/> from a specific configuration section.
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="section">Configuration section containing HTTP resilience options.</param>
    /// <param name="requestTimeoutSeconds">Total request timeout in seconds; when null, uses config.</param>
    /// <param name="fallbackHandler">Optional custom fallback handler.</param>
    /// <param name="configurePipeline">Optional delegate to add custom resilience handlers (outermost).</param>
    /// <returns>The HttpClient builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpClientWithResilience(
        this IHttpClientBuilder builder,
        IConfigurationSection section,
        int? requestTimeoutSeconds,
        IHttpFallbackHandler? fallbackHandler,
        Action<IHttpClientBuilder>? configurePipeline)
    {
        // Lightweight probe to check the Enabled flag only.
        var probe = new HttpResilienceOptions();
        section.Bind(probe);

        if (!probe.Enabled)
            return builder;

        // Register named options bound to this section so DI is the single source of truth.
        string optionsName = builder.Name;
        builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
            .Bind(section);
        builder.Services.AddSingleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>();

        int timeout = requestTimeoutSeconds ?? probe.Timeout.TotalRequestTimeoutSeconds;

        if (probe.Connection.Enabled)
        {
            builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var opts = serviceProvider.GetRequiredService<IOptionsSnapshot<HttpResilienceOptions>>().Get(optionsName);
                return SocketsHttpHandlerFactory.Create(opts);
            });
        }

        if (probe.PipelineOrder is { Count: > 0 })
            AddHandlersInOrder(builder, probe, timeout, probe.PipelineOrder, fallbackHandler);

        configurePipeline?.Invoke(builder);
        return builder;
    }

    /// <summary>
    /// Same as <see cref="AddHttpClientWithResilience(IHttpClientBuilder,IConfigurationSection,int?,IHttpFallbackHandler?,Action{ResiliencePipelineBuilder{HttpResponseMessage}},Action{IHttpClientBuilder}?)"/>
    /// but binds from the default "HttpResilienceOptions" section and uses the globally registered <see cref="IOptions{HttpResilienceOptions}"/> for the primary handler.
    /// For section-specific options (e.g. per-tenant or per-client), use the overload that accepts <see cref="IConfigurationSection"/>.
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="configuration">Configuration containing "HttpResilienceOptions" section.</param>
    /// <param name="requestTimeoutSeconds">Total request timeout in seconds applied as the outermost strategy; when null, uses <see cref="TimeoutOptions.TotalRequestTimeoutSeconds"/> from configuration.</param>
    /// <param name="fallbackHandler">Optional custom fallback handler. Not wired automatically in this overload; add fallback via <paramref name="configureInnerPipeline"/> or <paramref name="configurePipeline"/> as needed.</param>
    /// <param name="configureInnerPipeline">
    /// Delegate that receives a <see cref="ResiliencePipelineBuilder{TResult}"/> (for <see cref="HttpResponseMessage"/>)
    /// to configure the inner resilience pipeline (e.g. AddRetry, AddCircuitBreaker, AddRateLimiter) in any order.
    /// The total request timeout from <paramref name="requestTimeoutSeconds"/> is already prepended as the outermost strategy; do not add a duplicate total timeout here.
    /// </param>
    /// <param name="configurePipeline">
    /// Optional delegate to add extra resilience handlers (outermost) on the <see cref="IHttpClientBuilder"/> after the custom inner pipeline has been added.
    /// </param>
    /// <returns>The HttpClient builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpClientWithResilience(
        this IHttpClientBuilder builder,
        IConfiguration configuration,
        int? requestTimeoutSeconds,
        IHttpFallbackHandler? fallbackHandler,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>> configureInnerPipeline,
        Action<IHttpClientBuilder>? configurePipeline = null)
    {
        IConfigurationSection section = configuration.GetSection(HttpResilienceConfigurationKeys.HttpResilienceOptions);
        var options = new HttpResilienceOptions();
        section.Bind(options);

        if (!options.Enabled)
            return builder;

        int timeout = requestTimeoutSeconds ?? options.Timeout.TotalRequestTimeoutSeconds;

        if (options.Connection.Enabled)
            builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                HttpResilienceOptions opts = serviceProvider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
                return SocketsHttpHandlerFactory.Create(opts);
            });

        AddCustomStandardHandler(builder, timeout, fallbackHandler, configureInnerPipeline, configurePipeline);
        return builder;
    }

    /// <summary>
    /// Same as <see cref="AddHttpClientWithResilience(IHttpClientBuilder,IConfiguration,int?,IHttpFallbackHandler?,Action{ResiliencePipelineBuilder{HttpResponseMessage}},Action{IHttpClientBuilder}?)"/>
    /// but binds options from the specified <paramref name="section"/> and uses those options for both the primary handler and timeout.
    /// Use this overload when different named clients or tenants use different configuration sections so that connection and handler settings are section-specific.
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="section">Configuration section that contains the HTTP resilience options for this client.</param>
    /// <param name="requestTimeoutSeconds">Total request timeout in seconds applied as the outermost strategy; when null, uses the section's <see cref="TimeoutOptions.TotalRequestTimeoutSeconds"/>.</param>
    /// <param name="fallbackHandler">Optional custom fallback handler. Not wired automatically; add fallback via <paramref name="configureInnerPipeline"/> or <paramref name="configurePipeline"/> as needed.</param>
    /// <param name="configureInnerPipeline">
    /// Delegate that receives a <see cref="ResiliencePipelineBuilder{TResult}"/> (for <see cref="HttpResponseMessage"/>)
    /// to configure the inner resilience pipeline (e.g. AddRetry, AddCircuitBreaker, AddRateLimiter) in any order.
    /// The total request timeout from <paramref name="requestTimeoutSeconds"/> is already prepended as the outermost strategy; do not add a duplicate total timeout here.
    /// </param>
    /// <param name="configurePipeline">
    /// Optional delegate to add extra resilience handlers (outermost) on the <see cref="IHttpClientBuilder"/> after the custom inner pipeline has been added.
    /// </param>
    /// <returns>The HttpClient builder for chaining.</returns>
    public static IHttpClientBuilder AddHttpClientWithResilience(
        this IHttpClientBuilder builder,
        IConfigurationSection section,
        int? requestTimeoutSeconds,
        IHttpFallbackHandler? fallbackHandler,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>> configureInnerPipeline,
        Action<IHttpClientBuilder>? configurePipeline = null)
    {
        var probe = new HttpResilienceOptions();
        section.Bind(probe);

        if (!probe.Enabled)
            return builder;

        string optionsName = builder.Name;
        builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
            .Bind(section);
        builder.Services.AddSingleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>();

        int timeout = requestTimeoutSeconds ?? probe.Timeout.TotalRequestTimeoutSeconds;

        if (probe.Connection.Enabled)
        {
            builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var opts = serviceProvider.GetRequiredService<IOptionsSnapshot<HttpResilienceOptions>>().Get(optionsName);
                return SocketsHttpHandlerFactory.Create(opts);
            });
        }

        AddCustomStandardHandler(builder, timeout, fallbackHandler, configureInnerPipeline, configurePipeline);
        return builder;
    }

    /// <summary>
    /// Shared wiring for the custom inner pipeline overloads. The inner pipeline is configured entirely via
    /// <paramref name="configureInnerPipeline"/>; configuration options such as <see cref="HttpResilienceOptions.PipelineOrder"/> are not applied.
    /// A total request timeout of <paramref name="totalTimeoutSeconds"/> is applied as the outermost strategy so that
    /// the resolved timeout (from <paramref name="totalTimeoutSeconds"/>) is always honoured as an absolute cap,
    /// regardless of what <paramref name="configureInnerPipeline"/> adds internally.
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="totalTimeoutSeconds">Effective total request timeout in seconds, applied as the outermost strategy.</param>
    /// <param name="fallbackHandler">Optional custom fallback handler (not wired automatically in this helper).</param>
    /// <param name="configureInnerPipeline">Delegate to configure the inner resilience pipeline.</param>
    /// <param name="configurePipeline">Optional delegate to add extra outer handlers.</param>
    private static void AddCustomStandardHandler(
        IHttpClientBuilder builder,
        int totalTimeoutSeconds,
        IHttpFallbackHandler? fallbackHandler,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>> configureInnerPipeline,
        Action<IHttpClientBuilder>? configurePipeline)
    {
        builder.AddResilienceHandler("custom-standard", resilienceBuilder =>
        {
            resilienceBuilder.AddTimeout(TimeSpan.FromSeconds(totalTimeoutSeconds));
            configureInnerPipeline(resilienceBuilder);
        });

        configurePipeline?.Invoke(builder);
    }

    private static void AddHandlersInOrder(IHttpClientBuilder builder, HttpResilienceOptions options, int timeout, List<string> order, IHttpFallbackHandler? fallbackHandler)
    {
        bool rateLimiterInOrder = order.Exists(s => string.Equals(s, PipelineStrategyNames.RateLimiter, StringComparison.OrdinalIgnoreCase));
        string clientName = builder.Name;

        for (int i = order.Count - 1; i >= 0; i--)
        {
            var name = order[i];
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (string.Equals(name, PipelineStrategyNames.Standard, StringComparison.OrdinalIgnoreCase))
            {
                var resilienceBuilder = builder.AddStandardResilienceHandler().Configure((resilienceOptions, serviceProvider) =>
                {
                    var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
                    HttpStandardResilienceHandlerConfig.Create(options, timeout, builder.Services, rateLimiterHandledExternally: rateLimiterInOrder, logger: logger, clientName: clientName)(resilienceOptions);
                });
                if (IsPipelineSelectionByAuthority(options))
                    resilienceBuilder.SelectPipelineByAuthority();
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase))
            {
                var hedgingBuilder = builder.AddStandardHedgingHandler().Configure((resilienceOptions, serviceProvider) =>
                {
                    var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
                    HttpStandardHedgingHandlerConfig.Create(options, timeout, logger: logger, clientName: clientName)(resilienceOptions);
                });
                if (IsPipelineSelectionByAuthority(options))
                    hedgingBuilder.SelectPipelineByAuthority();
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.RateLimiter, StringComparison.OrdinalIgnoreCase) && options.RateLimiter.Enabled)
            {
                AddRateLimitHandler(builder, options.RateLimiter);
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.Bulkhead, StringComparison.OrdinalIgnoreCase) && options.Bulkhead.Enabled)
            {
                builder.AddResilienceHandler("concurrency", resilienceBuilder =>
                    resilienceBuilder.AddConcurrencyLimiter(options.Bulkhead.Limit, options.Bulkhead.QueueLimit));
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.Fallback, StringComparison.OrdinalIgnoreCase) && options.Fallback.Enabled)
                AddFallbackHandler(builder, options.Fallback, fallbackHandler);
        }
    }

    private static bool IsPipelineSelectionByAuthority(HttpResilienceOptions options) =>
        options.PipelineSelection?.Mode == PipelineSelectionMode.ByAuthority;

    private static void AddRateLimitHandler(IHttpClientBuilder builder, RateLimiterOptions rateLimiterOptions)
    {
        var limiter = RateLimiterFactory.CreateRateLimiter(rateLimiterOptions);
        builder.Services.AddSingleton(limiter);

        builder.AddResilienceHandler("rateLimit", resilienceBuilder =>
        {
            resilienceBuilder.AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken)
            });
        });
    }

    private static void AddFallbackHandler(IHttpClientBuilder builder, FallbackOptions fallback, IHttpFallbackHandler? customHandler)
    {
        var only5xx = fallback.OnlyOn5xx;
        var body = fallback.ResponseBody;
        string clientName = builder.Name;
        builder.AddResilienceHandler("fallback", (resilienceBuilder, context) =>
        {
            var logger = context.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
            resilienceBuilder.AddFallback(new FallbackStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = only5xx
                    ? new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError)
                    : new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(r => !r.IsSuccessStatusCode),
                FallbackAction = args => ExecuteFallbackAsync(customHandler, args, fallback.StatusCode, body, logger, clientName)
            });
        });
    }

    private static async ValueTask<Outcome<HttpResponseMessage>> ExecuteFallbackAsync(
        IHttpFallbackHandler? customHandler,
        FallbackActionArguments<HttpResponseMessage> args,
        int statusCode,
        string? body,
        ILogger? logger,
        string clientName)
    {
        if (logger is not null)
        {
            HttpResilienceLogging.FallbackActivated(logger, clientName,
                (int?)args.Outcome.Result?.StatusCode,
                args.Outcome.Exception?.GetType().Name);
        }

        if (customHandler is not null)
        {
            var context = new HttpFallbackContext(args.Outcome);
            var customResponse = await customHandler.TryHandleAsync(context, args.Context.CancellationToken).ConfigureAwait(false);
            if (customResponse is not null)
                return Outcome.FromResult(customResponse);
        }
        var response = new HttpResponseMessage((HttpStatusCode)statusCode);
        if (!string.IsNullOrEmpty(body))
            response.Content = new StringContent(body);
        if (args.Outcome.Result?.RequestMessage is { } requestMessage)
            response.RequestMessage = requestMessage;
        return Outcome.FromResult(response);
    }
}
