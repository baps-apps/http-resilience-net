using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// How retry delay grows between attempts. Maps to Polly <see cref="Polly.DelayBackoffType"/>.
/// Config key: "HttpResilienceOptions:Retry:BackoffType" (values: "Constant", "Linear", "Exponential").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RetryBackoffType
{
    /// <summary>
    /// Same delay every time. Config value: "Constant".
    /// </summary>
    Constant = 0,

    /// <summary>
    /// Delay increases linearly with attempt number. Config value: "Linear".
    /// </summary>
    Linear = 1,

    /// <summary>
    /// Delay doubles each attempt (exponential backoff). Config value: "Exponential".
    /// </summary>
    Exponential = 2
}
