using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// How the pipeline instance is selected per request. Config key: "HttpResilienceOptions:PipelineSelection:Mode" (values: "None", "ByAuthority").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineSelectionMode
{
    /// <summary>
    /// One pipeline per named client. Config value: "None".
    /// </summary>
    None = 0,

    /// <summary>
    /// Separate pipeline instance per request authority (scheme + host + port). Config value: "ByAuthority".
    /// </summary>
    ByAuthority = 1
}
