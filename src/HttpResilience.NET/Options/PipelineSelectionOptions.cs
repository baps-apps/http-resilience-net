using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// Options for selecting which pipeline instance handles each request (e.g. per authority).
/// Config key: "HttpResilienceOptions:PipelineSelection".
/// </summary>
public class PipelineSelectionOptions
{
    /// <summary>
    /// Selection mode. Config key: "PipelineSelection:Mode".
    /// <para><b>Options:</b> <see cref="PipelineSelectionMode.None"/> (default), <see cref="PipelineSelectionMode.ByAuthority"/>.</para>
    /// <para><b>Effect:</b> None = one pipeline per named client; ByAuthority = separate pipeline instance per request authority (scheme + host + port), so each host has its own circuit breaker and state.</para>
    /// </summary>
    [JsonPropertyName("Mode")]
    public PipelineSelectionMode Mode { get; set; } = PipelineSelectionMode.None;
}
