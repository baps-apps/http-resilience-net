using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Polly.Telemetry;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Minimal Polly metering enricher that adds Microsoft-style HTTP tags to resilience metrics.
/// </summary>
internal sealed class HttpResilienceMeteringEnricher : MeteringEnricher
{
    // Cache of canonical "HttpStatusCode.{n}" strings for known HTTP status codes — avoids per-event string allocation.
    private static readonly FrozenDictionary<int, string> _statusCodeNames = BuildStatusCodeNames();

    // Cache of "scheme://host[:port]" strings keyed by Uri reference — entries are kept alive as long as the Uri is.
    private static readonly ConditionalWeakTable<Uri, string> _dependencyNameByUri = new();

    // Cache of "pipeline/strategy" composite request names. Keys come from a small fixed set.
    private static readonly ConcurrentDictionary<(string Pipeline, string Strategy), string> _requestNameCache = new();

    public override void Enrich<TResult, TArgs>(in EnrichmentContext<TResult, TArgs> context)
    {
        // Single pass over context.Tags collects everything we need; downstream helpers reuse the locals.
        // Use index-based loop because context.Tags is IList<> — foreach over the interface allocates an enumerator object.
        string? pipelineName = null;
        string? strategyName = null;
        var tags = context.Tags;
        int count = tags.Count;
        for (int i = 0; i < count; i++)
        {
            var tag = tags[i];
            if (pipelineName is null && string.Equals(tag.Key, "pipeline.name", StringComparison.Ordinal))
            {
                pipelineName = tag.Value as string ?? tag.Value?.ToString();
            }
            else if (strategyName is null && string.Equals(tag.Key, "strategy.name", StringComparison.Ordinal))
            {
                strategyName = tag.Value as string ?? tag.Value?.ToString();
            }

            if (pipelineName is not null && strategyName is not null)
            {
                break;
            }
        }

        AddErrorType(context);
        AddRequestName(context, pipelineName, strategyName);
        AddRequestDependencyName(context, pipelineName);
    }

    private static void AddErrorType<TResult, TArgs>(in EnrichmentContext<TResult, TArgs> context)
    {
        string? errorType = null;

        var outcome = context.TelemetryEvent.Outcome;

        if (outcome?.Exception is Exception ex)
        {
            errorType = ex.GetType().FullName;
        }
        else if (outcome is { Result: HttpResponseMessage response } && !response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            if (!_statusCodeNames.TryGetValue(code, out errorType))
            {
                errorType = string.Concat("HttpStatusCode.", code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (!string.IsNullOrEmpty(errorType))
        {
            context.Tags.Add(new("error.type", errorType));
        }
    }

    private static void AddRequestName<TResult, TArgs>(
        in EnrichmentContext<TResult, TArgs> context,
        string? pipelineName,
        string? strategyName)
    {
        // Prefer explicit operation key if present.
        var operationKey = context.TelemetryEvent.Context?.OperationKey;
        if (!string.IsNullOrEmpty(operationKey))
        {
            context.Tags.Add(new("request.name", operationKey));
            return;
        }

        string? requestName;
        if (pipelineName is not null && strategyName is not null)
        {
            // Cache the composite name; pipeline/strategy values come from a small fixed set.
            var key = (pipelineName, strategyName);
            if (!_requestNameCache.TryGetValue(key, out requestName))
            {
                requestName = string.Concat(pipelineName, "/", strategyName);
                _requestNameCache.TryAdd(key, requestName);
            }
        }
        else
        {
            requestName = pipelineName ?? strategyName;
        }

        if (!string.IsNullOrEmpty(requestName))
        {
            context.Tags.Add(new("request.name", requestName));
        }
    }

    private static void AddRequestDependencyName<TResult, TArgs>(
        in EnrichmentContext<TResult, TArgs> context,
        string? pipelineName)
    {
        // Try to infer dependency name from HTTP request/response, if present.
        string? dependencyName = null;

        var outcome = context.TelemetryEvent.Outcome;

        if (outcome is { Result: HttpResponseMessage response })
        {
            dependencyName = GetDependencyNameFromResponse(response);
        }

        // Fall back to pipeline name if nothing HTTP-specific is available.
        dependencyName ??= pipelineName;

        if (!string.IsNullOrEmpty(dependencyName))
        {
            context.Tags.Add(new("request.dependency.name", dependencyName));
        }
    }

    private static string? GetDependencyNameFromResponse(HttpResponseMessage response)
    {
        var request = response.RequestMessage;
        if (request?.RequestUri is null)
        {
            return null;
        }

        return BuildDependencyName(request.RequestUri);
    }

    private static string BuildDependencyName(Uri uri)
    {
        // Cache per Uri instance; HttpClient typically reuses Uri references, so steady-state is allocation-free.
        if (_dependencyNameByUri.TryGetValue(uri, out var cached))
        {
            return cached;
        }

        var hostPort = uri.IsDefaultPort
            ? uri.Host
            : string.Concat(uri.Host, ":", uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var name = string.Concat(uri.Scheme, "://", hostPort);
        _dependencyNameByUri.AddOrUpdate(uri, name);
        return name;
    }

    private static FrozenDictionary<int, string> BuildStatusCodeNames()
    {
        // Pre-build canonical strings for all defined HttpStatusCode values plus all 4xx/5xx codes 400-599.
        var dict = new Dictionary<int, string>(capacity: 256);
        foreach (var value in Enum.GetValues<System.Net.HttpStatusCode>())
        {
            int code = (int)value;
            dict[code] = string.Concat("HttpStatusCode.", code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        for (int code = 400; code <= 599; code++)
        {
            if (!dict.ContainsKey(code))
            {
                dict[code] = string.Concat("HttpStatusCode.", code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return dict.ToFrozenDictionary();
    }
}
