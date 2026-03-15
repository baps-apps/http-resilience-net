using System.Net;
using System.Net.Http;
using Polly.Telemetry;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Minimal Polly metering enricher that adds Microsoft-style HTTP tags to resilience metrics.
/// </summary>
internal sealed class HttpResilienceMeteringEnricher : MeteringEnricher
{
    public override void Enrich<TResult, TArgs>(in EnrichmentContext<TResult, TArgs> context)
    {
        AddErrorType(context);
        AddRequestName(context);
        AddRequestDependencyName(context);
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
            errorType = $"HttpStatusCode.{(int)response.StatusCode}";
        }

        if (!string.IsNullOrEmpty(errorType))
        {
            context.Tags.Add(new("error.type", errorType));
        }
    }

    private static void AddRequestName<TResult, TArgs>(in EnrichmentContext<TResult, TArgs> context)
    {
        // Prefer explicit operation key if present.
        var operationKey = context.TelemetryEvent.Context?.OperationKey;
        if (!string.IsNullOrEmpty(operationKey))
        {
            context.Tags.Add(new("request.name", operationKey));
            return;
        }

        // Fall back to pipeline/strategy identity when available.
        string? pipelineName = GetTagValue(context, "pipeline.name");
        string? strategyName = GetTagValue(context, "strategy.name");

        string? requestName = pipelineName switch
        {
            not null when strategyName is not null => $"{pipelineName}/{strategyName}",
            not null => pipelineName,
            _ => strategyName
        };

        if (!string.IsNullOrEmpty(requestName))
        {
            context.Tags.Add(new("request.name", requestName));
        }
    }

    private static void AddRequestDependencyName<TResult, TArgs>(in EnrichmentContext<TResult, TArgs> context)
    {
        // Try to infer dependency name from HTTP request/response, if present.
        string? dependencyName = null;

        var outcome = context.TelemetryEvent.Outcome;

        if (outcome is { Result: HttpResponseMessage response })
        {
            dependencyName = GetDependencyNameFromResponse(response);
        }

        if (dependencyName is null && context.TelemetryEvent.Arguments is IHttpPolicyEventArguments policyArgs)
        {
            dependencyName = GetDependencyNameFromRequest(policyArgs);
        }

        // Fall back to pipeline name if nothing HTTP-specific is available.
        dependencyName ??= GetTagValue(context, "pipeline.name");

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

    private static string? GetDependencyNameFromRequest(IHttpPolicyEventArguments policyArgs)
    {
        var request = policyArgs.Request;
        if (request?.RequestUri is null)
        {
            return null;
        }

        return BuildDependencyName(request.RequestUri);
    }

    private static string BuildDependencyName(Uri uri)
    {
        // Use scheme + host + optional port to identify the dependency.
        var hostPort = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{hostPort}";
    }

    private static string? GetTagValue<TResult, TArgs>(in EnrichmentContext<TResult, TArgs> context, string name)
    {
        foreach (var tag in context.Tags)
        {
            if (string.Equals(tag.Key, name, StringComparison.Ordinal))
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    // Minimal interface to allow extracting HttpRequestMessage when Polly exposes HTTP-specific arguments.
    internal interface IHttpPolicyEventArguments
    {
        HttpRequestMessage? Request { get; }
    }
}

