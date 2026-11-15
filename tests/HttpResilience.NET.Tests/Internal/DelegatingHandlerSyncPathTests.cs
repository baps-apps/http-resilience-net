using System.Reflection;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// Every handler this package puts in a chain must cover <c>HttpClient.Send</c> as well as <c>SendAsync</c>.
/// </summary>
public class DelegatingHandlerSyncPathTests
{
    private static readonly Type[] _sendParameters = [typeof(HttpRequestMessage), typeof(CancellationToken)];

    /// <summary>
    /// <c>DelegatingHandler.Send</c> forwards to the inner handler without calling <c>SendAsync</c>, so a
    /// handler that overrides only <c>SendAsync</c> is skipped by every synchronous request.
    /// </summary>
    /// <remarks>
    /// Fails if a handler in the library overrides <c>SendAsync</c> without <c>Send</c>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryHandlerThatOverridesSendAsync_AlsoOverridesSend()
    {
        Type[] handlers =
        [
            .. typeof(HttpResilienceOptions).Assembly.GetTypes()
                .Where(type => type.IsSubclassOf(typeof(DelegatingHandler)) && !type.IsAbstract)
                .Where(type => Declares(type, "SendAsync"))
        ];

        // Not vacuous: the authority allow-list handler overrides SendAsync.
        Assert.NotEmpty(handlers);

        string[] missing = [.. handlers.Where(type => !Declares(type, "Send")).Select(type => type.Name)];

        Assert.True(missing.Length == 0,
            $"These handlers override SendAsync but not Send, so HttpClient.Send bypasses them: {string.Join(", ", missing)}");
    }

    private static bool Declares(Type type, string method) =>
        type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, _sendParameters)?.DeclaringType == type;
}
