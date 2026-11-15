using System.Reflection;
using Xunit.v3;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// Every test carries a timeout, so a hung test fails on its own instead of hanging the whole run.
/// </summary>
public class TestTimeoutCoverageTests
{
    /// <remarks>Fails if a test is added with a bare <c>[Fact]</c> or <c>[Theory]</c>.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryTest_HasATimeout()
    {
        MethodInfo[] tests =
        [
            .. typeof(TestTimeoutCoverageTests).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
                .Where(method => method.GetCustomAttributes().OfType<IFactAttribute>().Any())
        ];

        // Not vacuous: this method is one of them.
        Assert.Contains(tests, method => method.Name == nameof(EveryTest_HasATimeout));

        string[] untimed =
        [
            .. tests
                .Where(method => method.GetCustomAttributes().OfType<IFactAttribute>().Any(fact => fact.Timeout <= 0))
                .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
        ];
        Assert.True(untimed.Length == 0, $"Tests without a Timeout (use TestTimeouts.Default): {string.Join(", ", untimed)}");
    }
}
