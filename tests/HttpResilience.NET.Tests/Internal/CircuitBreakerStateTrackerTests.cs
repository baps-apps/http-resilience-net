using HttpResilience.NET.Internal;

namespace HttpResilience.NET.Tests.Internal;

public class CircuitBreakerStateTrackerTests
{
    [Fact]
    public void GetState_UnknownClient_ReturnsClosed()
    {
        var tracker = new CircuitBreakerStateTracker();
        var state = tracker.GetState("unknown-client");
        Assert.Equal(CircuitState.Closed, state);
    }

    [Fact]
    public void ReportOpened_ThenGetState_ReturnsOpen()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("my-client");
        Assert.Equal(CircuitState.Open, tracker.GetState("my-client"));
    }

    [Fact]
    public void ReportHalfOpen_ThenGetState_ReturnsHalfOpen()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportHalfOpen("my-client");
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("my-client"));
    }

    [Fact]
    public void ReportClosed_AfterOpen_ReturnsClosed()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("my-client");
        tracker.ReportClosed("my-client");
        Assert.Equal(CircuitState.Closed, tracker.GetState("my-client"));
    }

    [Fact]
    public void GetAllStates_ReturnsAllTrackedClients()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("client-a");
        tracker.ReportClosed("client-b");

        var all = tracker.GetAllStates();
        Assert.Equal(2, all.Count);
        Assert.Equal(CircuitState.Open, all["client-a"]);
        Assert.Equal(CircuitState.Closed, all["client-b"]);
    }

    [Fact]
    public void HasOpenCircuits_WhenNoneOpen_ReturnsFalse()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportClosed("client-a");
        Assert.False(tracker.HasOpenCircuits);
    }

    [Fact]
    public void HasOpenCircuits_WhenOneOpen_ReturnsTrue()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("client-a");
        Assert.True(tracker.HasOpenCircuits);
    }
}
