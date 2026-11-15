namespace HttpResilience.NET.Tests;

/// <summary>
/// The time limit every test carries, so one hung test fails on its own instead of hanging the run.
/// </summary>
/// <remarks>
/// xunit v3 has no assembly-wide timeout, so each <c>[Fact]</c> and <c>[Theory]</c> states it
/// (<c>EveryTest_HasATimeout</c> enforces that). The slowest test takes about 4 seconds; the margin is for a
/// loaded build agent. Awaited calls take <c>TestContext.Current.CancellationToken</c> (xUnit1051), which the
/// timeout cancels, so the abandoned work stops too.
/// </remarks>
internal static class TestTimeouts
{
    public const int Default = 30_000;
}
