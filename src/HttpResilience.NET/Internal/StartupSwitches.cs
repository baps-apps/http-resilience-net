using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Reads <see cref="HttpResilienceStartupOptions"/> from the root section, and reports a value that is
/// neither true nor false.
/// </summary>
/// <remarks>
/// Parsed by hand rather than bound, so a malformed value fails validation with a message naming the key
/// instead of throwing from the binder.
/// </remarks>
internal static class StartupSwitches
{
    private static readonly (string Key, string Decides, Action<HttpResilienceStartupOptions, bool> Set)[] Switches =
    [
        (nameof(HttpResilienceStartupOptions.ValidateClientsOnStart),
            "whether every client this package configured is created while the host starts, so that a client " +
            "whose handler chain cannot be built fails the deployment rather than the first request that reaches it",
            static (options, value) => options.ValidateClientsOnStart = value),
        (nameof(HttpResilienceStartupOptions.AllowUnusedClientSections),
            "whether a client section that no client reads fails startup",
            static (options, value) => options.AllowUnusedClientSections = value),
        (nameof(HttpResilienceStartupOptions.AllowUnknownKeys),
            "whether a key this package does not recognize fails startup",
            static (options, value) => options.AllowUnknownKeys = value)
    ];

    public static void Read(IConfigurationSection root, HttpResilienceStartupOptions options)
    {
        // Read here, once for the application, so a malformed or newer-major schema version fails startup
        // whatever AllowUnknownKeys says.
        if (SchemaVersionRules.Failure(root) is { } schemaFailure)
        {
            OptionsComposition.AddMalformedKey(options, schemaFailure);
        }

        foreach ((string key, string decides, Action<HttpResilienceStartupOptions, bool> set) in Switches)
        {
            if (root[key] is not { } stated)
            {
                continue;
            }

            if (bool.TryParse(stated, out bool value))
            {
                set(options, value);
                continue;
            }

            OptionsComposition.AddMalformedKey(
                options,
                $"{root.Path}:{key} -- value '{stated}' is invalid. Expected 'true' or 'false'. Reason: this key " +
                $"decides {decides}, and a value that is neither leaves that ambiguous.");
        }
    }

    /// <summary>
    /// Creates each <see cref="HttpResilienceStartupOptions"/> with the root section already read, so every
    /// <c>Configure</c> and <c>PostConfigure</c> lands on top of configuration whatever the registration order.
    /// </summary>
    /// <remarks>
    /// <see cref="OptionsFactory{TOptions}.CreateInstance"/> is the options system's own hook for an instance's
    /// starting values; the rest of the factory (configure, post-configure, validate) is the default.
    /// </remarks>
    internal sealed class Factory : OptionsFactory<HttpResilienceStartupOptions>
    {
        private readonly HttpResilienceRegistration _registration;

        public Factory(
            HttpResilienceRegistration registration,
            IEnumerable<IConfigureOptions<HttpResilienceStartupOptions>> setups,
            IEnumerable<IPostConfigureOptions<HttpResilienceStartupOptions>> postConfigures,
            IEnumerable<IValidateOptions<HttpResilienceStartupOptions>> validations)
            : base(setups, postConfigures, validations) =>
            _registration = registration;

        protected override HttpResilienceStartupOptions CreateInstance(string name)
        {
            HttpResilienceStartupOptions options = base.CreateInstance(name);
            if (_registration.Section is { } section)
            {
                Read(section, options);
            }

            return options;
        }
    }

    /// <summary>Fails startup for a malformed switch value recorded by <see cref="Read"/>.</summary>
    internal sealed class Validator : IValidateOptions<HttpResilienceStartupOptions>
    {
        public ValidateOptionsResult Validate(string? name, HttpResilienceStartupOptions options) =>
            OptionsComposition.MalformedKeysOf(options) is { Count: > 0 } failures
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
    }
}
