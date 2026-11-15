namespace HttpResilience.NET.Internal;

/// <summary>
/// Limits a limiter's rejection log to one entry per <see cref="Interval"/>, counting the rest.
/// </summary>
/// <remarks>
/// Rejections arrive in bursts exactly when a service is overloaded; one log entry per rejection would add
/// to that load. The rejection count is available from Polly's metrics.
/// </remarks>
internal sealed class RejectionLogThrottle
{
    /// <summary>The minimum time between two rejection log entries for the same limiter.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _timeProvider;
    private readonly long _intervalTicks;
    private long _nextAllowed = long.MinValue;
    private int _suppressed;

    public RejectionLogThrottle(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _intervalTicks = (long)(Interval.TotalSeconds * timeProvider.TimestampFrequency);
    }

    public static RejectionLogThrottle Create(IServiceProvider serviceProvider) =>
        new((TimeProvider?)serviceProvider.GetService(typeof(TimeProvider)) ?? TimeProvider.System);

    /// <summary>
    /// Returns <see langword="true"/> when this rejection should be logged, with the number of rejections
    /// skipped since the previous entry.
    /// </summary>
    public bool ShouldLog(out int suppressed)
    {
        long now = _timeProvider.GetTimestamp();
        long next = Volatile.Read(ref _nextAllowed);

        if (now >= next && Interlocked.CompareExchange(ref _nextAllowed, now + _intervalTicks, next) == next)
        {
            suppressed = Interlocked.Exchange(ref _suppressed, 0);
            return true;
        }

        Interlocked.Increment(ref _suppressed);
        suppressed = 0;
        return false;
    }
}
