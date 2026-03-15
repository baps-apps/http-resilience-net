namespace HttpResilience.NET.Configuration;

/// <summary>
/// Configuration section keys for HttpResilience.NET. Use these constants so section names stay consistent across applications.
/// </summary>
public static class HttpResilienceConfigurationKeys
{
    /// <summary>
    /// Section name for HTTP resilience options. Use this when binding or reading options so all consuming applications use the same key.
    /// <para><b>Use case:</b> In appsettings.json place options under <c>"HttpResilienceOptions": { ... }</c>. In code, get the section with
    /// <c>configuration.GetSection(HttpResilienceConfigurationKeys.HttpResilienceOptions)</c> or pass the same <c>IConfiguration</c> to <c>AddHttpResilienceOptions</c> and the AddHttpClient* extensions.</para>
    /// </summary>
    public const string HttpResilienceOptions = "HttpResilienceOptions";
}
