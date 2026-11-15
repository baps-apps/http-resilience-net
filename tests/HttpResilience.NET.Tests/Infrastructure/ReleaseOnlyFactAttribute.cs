using System.Runtime.CompilerServices;

namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>
/// A fact that runs only in a Release build, and reports itself as skipped -- with the reason -- otherwise.
/// </summary>
/// <remarks>
/// For thresholds that describe the shipped assembly, mainly allocation ceilings: Debug codegen adds display
/// classes and async state machines. Skipped rather than compiled out, so a Debug run still lists the test
/// and says why it did not run.
/// </remarks>
public sealed class ReleaseOnlyFactAttribute : FactAttribute
{
    public ReleaseOnlyFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Timeout = TestTimeouts.Default;
#if DEBUG
        Skip = "Calibrated against Release codegen. Run 'dotnet test -c Release'.";
#endif
    }
}
