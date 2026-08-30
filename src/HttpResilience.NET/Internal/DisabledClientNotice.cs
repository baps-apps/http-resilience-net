using HttpResilience.NET.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Warns once, at startup, when a client is registered with resilience switched off.
/// </summary>
/// <remarks>
/// <c>Enabled</c> defaults to <see langword="false"/>, so a service that adds the package and registers every
/// client but never sets the flag gets no pipeline at all -- and the run-time state is identical to a
/// deliberate opt-out. Disabling on purpose is supported and sometimes necessary; doing it by accident has to
/// leave a trace loud enough and early enough to be caught by the deployment that introduced it.
/// <para>
/// Hence an <see cref="IPostConfigureOptions{TOptions}"/> rather than a hook on
/// <c>ConfigureHttpClient</c>. Every client registers its options with <c>ValidateOnStart</c>, so the host
/// materializes them before it accepts traffic and this runs there. Hanging off client creation instead meant
/// waiting for a client's first use, which for a rarely-exercised client can be hours or days after the
/// deploy -- long past the smoke check that should have caught it.
/// </para>
/// <para>
/// Logging is optional: a container with no <see cref="ILoggerFactory"/> must not fail to start because the
/// notice had nowhere to write. The once-guard matters because named options are re-created per scope for
/// anything resolving <see cref="IOptionsSnapshot{TOptions}"/>, and a warning per scope is log spam rather
/// than a signal.
/// </para>
/// </remarks>
internal sealed class DisabledClientNotice : IPostConfigureOptions<HttpResilienceOptions>
{
    private readonly string _optionsName;
    private readonly string _clientName;
    private readonly string _enabledPath;
    private readonly ILogger? _logger;
    private int _reported;

    public DisabledClientNotice(string optionsName, string clientName, string scope, ILoggerFactory? loggerFactory)
    {
        _optionsName = optionsName;
        _clientName = clientName;
        _enabledPath = $"{scope}:Enabled";
        _logger = loggerFactory?.CreateLogger("HttpResilience");
    }

    public void PostConfigure(string? name, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_logger is null || !string.Equals(name, _optionsName, StringComparison.Ordinal))
        {
            return;
        }

        // Read the flag rather than trusting the registration that created this: the value that reaches the
        // options is the snapshot the pipeline was built from, and a client whose flag was true never gets
        // this notice registered at all.
        if (options.Enabled)
        {
            return;
        }

        if (Volatile.Read(ref _reported) != 0 || Interlocked.Exchange(ref _reported, 1) != 0)
        {
            return;
        }

        HttpResilienceLogging.ResilienceDisabled(_logger, _clientName, _enabledPath);
    }
}
