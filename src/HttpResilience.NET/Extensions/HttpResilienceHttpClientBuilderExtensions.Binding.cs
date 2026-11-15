using HttpResilience.NET;
using HttpResilience.NET.Internal;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

public static partial class HttpResilienceHttpClientBuilderExtensions
{
    /// <summary>
    /// Binds the root section, then the root delegates, then the client's section, then
    /// <paramref name="configure"/>, and resolves the redirect setting for the pipeline kind.
    /// </summary>
    private static void BindEffective(
        IConfigurationSection root,
        IReadOnlyList<Action<HttpResilienceOptions>> rootConfigures,
        string sectionName,
        Action<HttpResilienceOptions>? configure,
        PipelineKind kind,
        HttpResilienceOptions target)
    {
        Bind(root, rootConfigures, sectionName, configure, target);

        // Recorded before resolving, which erases whether the value was stated.
        OptionsComposition.SetRedirectStated(target.Connection, target.Connection.AllowAutoRedirect.HasValue);

        target.Connection.AllowAutoRedirect =
            target.Connection.FollowsRedirects(enforcesAllowList: kind is PipelineKind.Hedging);
    }

    private static void Bind(
        IConfigurationSection root,
        IReadOnlyList<Action<HttpResilienceOptions>> rootConfigures,
        string sectionName,
        Action<HttpResilienceOptions>? configure,
        HttpResilienceOptions options)
    {
        root.Bind(options);

        if (rootConfigures.Count > 0)
        {
            // Every client inherits these, so what they change is judged by the root section's rules. Recorded
            // here, between the root and the client, because afterwards the two can no longer be told apart.
            RootSectionRules.UnsafeMethodState before = RootSectionRules.UnsafeMethodState.Of(options);
            foreach (Action<HttpResilienceOptions> rootConfigure in rootConfigures)
            {
                rootConfigure(options);
            }

            // The client's own values hide what the root delegates did, so it is recorded now.
            OptionsComposition.SetRootDelegateFailures(
                options, RootSectionRules.CollectRootDelegateFailures(before, options));
        }

        if (!string.IsNullOrEmpty(sectionName))
        {
            IConfigurationSection client = root
                .GetSection(HttpResilienceConfigurationKeys.ClientsSection)
                .GetSection(sectionName);

            ResetListsStatedBy(client, options);
            client.Bind(options);
        }

        configure?.Invoke(options);

        // The root switch overrides clients and code, so operations can turn every client off.
        if (RootSectionRules.IsSwitchedOff(root))
        {
            options.Enabled = false;
        }
    }

    /// <summary>
    /// Clears lists the client section states, because the binder appends to an existing list instead of
    /// replacing it. Without this a client could widen an inherited allow-list but never narrow it.
    /// </summary>
    private static void ResetListsStatedBy(IConfigurationSection client, HttpResilienceOptions options)
    {
        if (client.GetSection("Retry:RetryableMethods").Exists())
        {
            options.Retry.RetryableMethods.Clear();
        }

        if (client.GetSection("PipelineSelection:Authorities").Exists())
        {
            options.PipelineSelection.Authorities.Clear();
        }
    }
}
