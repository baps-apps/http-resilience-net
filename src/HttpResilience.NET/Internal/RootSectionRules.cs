using Microsoft.Extensions.Configuration;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Rules about the root section that hold for every client, checked against the raw section so they apply
/// whichever way the root options are (or are not) materialized.
/// </summary>
internal static class RootSectionRules
{
    /// <summary>
    /// Whether the root states <c>Enabled: false</c>, the kill switch that turns every client off.
    /// </summary>
    public static bool IsSwitchedOff(IConfigurationSection section) =>
        bool.TryParse(section["Enabled"], out bool enabled) && !enabled;

    /// <summary>
    /// Failures for switching off either unsafe-method guard in the root section, which every client
    /// inherits, or <see langword="null"/> if there are none.
    /// </summary>
    public static List<string>? CollectUnsafeMethodGuardFailures(IConfigurationSection section)
    {
        List<string>? failures = null;

        Check("Retry:DisableForUnsafeHttpMethods", "standard",
            "Prefer Retry:RetryableMethods on that client.");

        Check("Hedging:DisableForUnsafeHttpMethods", "hedged",
            "Hedged attempts run at the same time, so an idempotency key may not prevent duplicates.");

        CheckRootAllowList(section, ref failures);
        return failures;

        void Check(string key, string pipeline, string tail)
        {
            if (!bool.TryParse(section[key], out bool guarded) || guarded)
            {
                return;
            }

            (failures ??= []).Add(
                $"{section.Path}:{key} -- value 'False' is invalid. Expected true at the root section; set " +
                $"false only under {section.Path}:{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} " +
                $"for the client that needs it. Reason: at the root it applies to every {pipeline} client, " +
                $"but whether a request may be sent twice depends on one endpoint's idempotency handling. " +
                tail);
        }
    }

    /// <summary>
    /// The unsafe-method guards as they stand at one point while a client's options are composed.
    /// </summary>
    internal readonly record struct UnsafeMethodState(
        bool RetryGuarded,
        bool HedgingGuarded,
        IReadOnlySet<string> UnsafeRetryableMethods)
    {
        public static UnsafeMethodState Of(HttpResilienceOptions options) => new(
            options.Retry.DisableForUnsafeHttpMethods,
            options.Hedging.DisableForUnsafeHttpMethods,
            UnsafeEntries(options.Retry.RetryableMethods));
    }

    /// <summary>
    /// The same rules for the root delegates passed to <c>services.AddHttpResilience(configure)</c>, which every
    /// client inherits exactly as it inherits the root section. Only what the delegates changed is reported, so
    /// a value already refused in the root section is not reported twice.
    /// </summary>
    public static List<string>? CollectRootDelegateFailures(UnsafeMethodState before, HttpResilienceOptions after)
    {
        List<string>? failures = null;
        const string Where =
            "the delegate passed to services.AddHttpResilience(configure), which every client inherits";
        const string Instead =
            $"for the one client that needs it, in {HttpResilienceConfigurationKeys.RootSection}:" +
            $"{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} or that client's " +
            "AddHttpResilienceHandler(configure)";

        if (before.RetryGuarded && !after.Retry.DisableForUnsafeHttpMethods)
        {
            (failures ??= []).Add(
                $"Retry.DisableForUnsafeHttpMethods -- value 'False' set in {Where} is invalid. Expected true " +
                $"there; set false only {Instead}. Reason: whether a request may be sent twice depends on one " +
                "endpoint's idempotency handling. Prefer Retry.RetryableMethods on that client.");
        }

        if (before.HedgingGuarded && !after.Hedging.DisableForUnsafeHttpMethods)
        {
            (failures ??= []).Add(
                $"Hedging.DisableForUnsafeHttpMethods -- value 'False' set in {Where} is invalid. Expected true " +
                $"there; set false only {Instead}. Reason: hedged attempts run at the same time, so an " +
                "idempotency key may not prevent duplicates.");
        }

        foreach (string method in UnsafeEntries(after.Retry.RetryableMethods))
        {
            if (before.UnsafeRetryableMethods.Contains(method))
            {
                continue;
            }

            (failures ??= []).Add(
                $"Retry.RetryableMethods -- value '{method}' added in {Where} is invalid. Expected only GET, " +
                $"HEAD, OPTIONS and TRACE there; list other methods {Instead}. Reason: whether a request may " +
                "be sent twice depends on one endpoint's idempotency handling.");
        }

        return failures;
    }

    private static HashSet<string> UnsafeEntries(IEnumerable<string> methods) =>
    [
        .. methods
            .Where(method => HttpMethodPredicates.IsValidMethodToken(method) && !HttpMethodPredicates.IsSafe(method))
            .Select(HttpMethodPredicates.Normalize)
    ];

    /// <summary>
    /// Rejects unsafe methods in the root <c>Retry:RetryableMethods</c>. A root list may only narrow retries.
    /// </summary>
    private static void CheckRootAllowList(IConfigurationSection section, ref List<string>? failures)
    {
        foreach (IConfigurationSection entry in section.GetSection("Retry:RetryableMethods").GetChildren())
        {
            if (entry.Value is not { } method ||
                !HttpMethodPredicates.IsValidMethodToken(method) ||
                HttpMethodPredicates.IsSafe(method))
            {
                continue;
            }

            (failures ??= []).Add(
                $"{section.Path}:Retry:RetryableMethods -- value '{method}' is invalid at the root section. " +
                "Expected only GET, HEAD, OPTIONS and TRACE here; list other methods under " +
                $"{section.Path}:{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} for the client whose " +
                "endpoint deduplicates on an idempotency key. Reason: every client inherits the root, but " +
                "whether a request may be sent twice depends on one endpoint's idempotency handling.");
        }
    }
}
