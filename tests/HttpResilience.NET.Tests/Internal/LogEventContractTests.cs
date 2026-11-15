using System.Reflection;
using HttpResilience.NET.Internal;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// Event IDs and levels are a contract: OPERATIONS.md tells operators to alert on them, so renumbering one or
/// changing its level silently breaks an alert. This pins every event the package logs.
/// </summary>
/// <remarks>
/// Fails if an event's ID or level changes, if an event is added without being added here (and to
/// OPERATIONS.md), or if one is removed.
/// </remarks>
public class LogEventContractTests
{
    private static readonly Dictionary<string, (int Id, LogLevel Level)> Expected = new(StringComparer.Ordinal)
    {
        [nameof(HttpResilienceLogging.RetryAttempt)] = (1, LogLevel.Debug),
        [nameof(HttpResilienceLogging.CircuitBreakerOpened)] = (2, LogLevel.Warning),
        [nameof(HttpResilienceLogging.CircuitBreakerHalfOpen)] = (3, LogLevel.Information),
        [nameof(HttpResilienceLogging.CircuitBreakerClosed)] = (4, LogLevel.Information),
        [nameof(HttpResilienceLogging.HedgingAttempt)] = (5, LogLevel.Debug),
        [nameof(HttpResilienceLogging.ResilienceDisabled)] = (6, LogLevel.Warning),
        [nameof(HttpResilienceLogging.ConcurrencyBackstopRejected)] = (7, LogLevel.Warning),
        [nameof(HttpResilienceLogging.RateLimiterRejected)] = (8, LogLevel.Warning),
        [nameof(HttpResilienceLogging.ConcurrencyLimiterRejected)] = (9, LogLevel.Warning),
        [nameof(HttpResilienceLogging.UnsafeMethodsRepeated)] = (10, LogLevel.Warning),
        [nameof(HttpResilienceLogging.CircuitBreakerReach)] = (11, LogLevel.Information),
        [nameof(HttpResilienceLogging.ExtraResilienceHandlers)] = (12, LogLevel.Information),
        [nameof(HttpResilienceLogging.RedirectBoundNotApplied)] = (13, LogLevel.Warning),
        [nameof(HttpResilienceLogging.RetryBudgetExceedsTotal)] = (14, LogLevel.Warning),
        [nameof(HttpResilienceLogging.ClientSummary)] = (15, LogLevel.Information),
        [nameof(HttpResilienceLogging.ResilienceHandlersRemoved)] = (16, LogLevel.Warning),
        [nameof(HttpResilienceLogging.EarlierResilienceHandlersReplaced)] = (17, LogLevel.Information),
        [nameof(HttpResilienceLogging.KeysFromNewerSchemaIgnored)] = (18, LogLevel.Warning),
        [nameof(HttpResilienceLogging.DefaultsResilienceHandlersRemoved)] = (19, LogLevel.Information),
        [nameof(HttpResilienceLogging.LegacyPollyHandlers)] = (20, LogLevel.Warning),
        [nameof(HttpResilienceLogging.HedgingFansOutEveryRequest)] = (21, LogLevel.Warning),
    };

    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryLogEvent_KeepsItsDocumentedIdAndLevel()
    {
        Dictionary<string, (int Id, LogLevel Level)> actual = typeof(HttpResilienceLogging)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => (method.Name, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(pair => pair.Attribute is not null)
            .ToDictionary(
                pair => pair.Name,
                pair => (pair.Attribute!.EventId, pair.Attribute.Level),
                StringComparer.Ordinal);

        Assert.Equal(
            Expected.OrderBy(pair => pair.Value.Id).ToArray(),
            actual.OrderBy(pair => pair.Value.Id).ToArray());
    }
}
