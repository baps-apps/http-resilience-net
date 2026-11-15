using HttpResilience.NET.Internal;
using Microsoft.Extensions.Time.Testing;

namespace HttpResilience.NET.Tests.Internal;

public class RejectionLogThrottleTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheFirstRejection_IsLogged()
    {
        var throttle = new RejectionLogThrottle(new FakeTimeProvider());

        Assert.True(throttle.ShouldLog(out int suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RejectionsWithinTheInterval_AreCountedNotLogged()
    {
        var time = new FakeTimeProvider();
        var throttle = new RejectionLogThrottle(time);
        Assert.True(throttle.ShouldLog(out _));

        for (int i = 0; i < 500; i++)
        {
            Assert.False(throttle.ShouldLog(out _));
        }

        time.Advance(RejectionLogThrottle.Interval);

        Assert.True(throttle.ShouldLog(out int suppressed));
        Assert.Equal(500, suppressed);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void JustBeforeTheInterval_StillSuppresses()
    {
        var time = new FakeTimeProvider();
        var throttle = new RejectionLogThrottle(time);
        Assert.True(throttle.ShouldLog(out _));

        time.Advance(RejectionLogThrottle.Interval - TimeSpan.FromMilliseconds(1));

        Assert.False(throttle.ShouldLog(out _));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConcurrentRejections_LogExactlyOnce()
    {
        var throttle = new RejectionLogThrottle(new FakeTimeProvider());
        int logged = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 1_000; i++)
            {
                if (throttle.ShouldLog(out _))
                {
                    Interlocked.Increment(ref logged);
                }
            }
        })));

        Assert.Equal(1, logged);
    }
}
