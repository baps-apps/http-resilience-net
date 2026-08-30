using Polly.Telemetry;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Adds an OpenTelemetry-aligned <c>error.type</c> dimension to Polly's metrics for exception outcomes.
/// </summary>
/// <remarks>
/// This fills one gap and nothing else. <c>Microsoft.Extensions.Http.Resilience</c> already tags
/// <c>error.type</c> with the status code when a response is the failure -- on every pipeline in the process,
/// including the ones this package registers with <c>AddResilienceHandler</c> -- and Polly already tags
/// <c>exception.type</c> when an exception is the failure. What nothing supplies is <c>error.type</c> for the
/// exception case, which is the attribute the OpenTelemetry convention names and the one a dashboard filters
/// on, so a query for <c>error.type</c> would silently miss every connection failure and timeout.
/// <para>
/// Tagging response outcomes here as well would put <c>error.type</c> on the measurement twice, since this
/// enricher runs before the platform's. Anything already carrying the key is therefore left alone.
/// </para>
/// <para>
/// Polly already emits <c>pipeline.name</c>, <c>pipeline.instance</c> and <c>strategy.name</c>, and
/// <c>System.Net.Http</c> already emits <c>server.address</c>, <c>server.port</c>, <c>http.request.method</c>
/// and <c>http.response.status_code</c>. Anything derived from the request URI would restate one of those from
/// a less reliable source, with cardinality bounded only by the set of hosts the process happens to call, so
/// nothing here reads request data. The one value this adds is an exception type name, bounded by the code.
/// </para>
/// </remarks>
internal sealed class HttpResilienceMeteringEnricher : MeteringEnricher
{
    private const string ErrorTypeTag = "error.type";

    public override void Enrich<TResult, TArgs>(in EnrichmentContext<TResult, TArgs> context)
    {
        if (context.TelemetryEvent.Outcome is not { } outcome ||
            outcome.Exception is not { } exception ||
            HasErrorType(context.Tags))
        {
            return;
        }

        context.Tags.Add(new KeyValuePair<string, object?>(ErrorTypeTag, exception.GetType().FullName));
    }

    private static bool HasErrorType(IList<KeyValuePair<string, object?>> tags)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            if (string.Equals(tags[i].Key, ErrorTypeTag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
