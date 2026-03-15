using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// Fallback strategy options (both pipelines). Config key: "HttpResilienceOptions:Fallback".
/// </summary>
public class FallbackOptions
{
    /// <summary>
    /// When true, on total failure (after retries/hedging) the pipeline returns a synthetic response instead of throwing.
    /// <para><b>Use case:</b> Enable for non-critical calls where callers can handle a default response instead of an exception. Default: false.</para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// HTTP status code (4xx or 5xx) returned when fallback is used and <see cref="Enabled"/> is true.
    /// <para><b>Use case:</b> 503 Service Unavailable is standard for "dependency down"; 504 for timeout. Default: 503.</para>
    /// </summary>
    [Range(400, 599, ErrorMessage = "StatusCode must be between 400 and 599.")]
    public int StatusCode { get; set; } = 503;

    /// <summary>
    /// When true, fallback only for 5xx and exceptions; when false, fallback for any non-success (including 4xx).
    /// <para><b>Use case:</b> Set true when 4xx should propagate; false for a single fallback for any failure. Default: false.</para>
    /// </summary>
    [JsonPropertyName("OnlyOn5xx")]
    public bool OnlyOn5xx { get; set; }

    /// <summary>
    /// Optional plain-text body for the fallback response. When null, the response body is empty.
    /// <para><b>Use case:</b> e.g. "Service temporarily unavailable" or a short JSON message. Default: null.</para>
    /// </summary>
    [JsonPropertyName("ResponseBody")]
    public string? ResponseBody { get; set; }
}
