using System.Runtime.CompilerServices;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Remembers which handler instances this package put in a client's chain, so their removal can be detected
/// even when a consumer adds a handler of the same type in their place.
/// </summary>
/// <remarks>
/// Keyed by instance and held weakly, so a disposed chain leaves nothing behind.
/// </remarks>
internal static class PackageHandlers
{
    private static readonly ConditionalWeakTable<DelegatingHandler, object> Owned = new();

    public static void Mark(DelegatingHandler handler) => Owned.AddOrUpdate(handler, Owned);

    public static bool IsOwned(DelegatingHandler handler) => Owned.TryGetValue(handler, out _);
}
