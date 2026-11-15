using Polly.Telemetry;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Adds an OpenTelemetry-aligned <c>error.type</c> dimension to Polly's metrics for exception outcomes.
/// </summary>
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
