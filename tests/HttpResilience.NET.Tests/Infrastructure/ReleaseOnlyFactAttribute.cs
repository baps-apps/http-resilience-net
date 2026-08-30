namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>
/// A fact that runs only in a Release build, and reports itself as skipped -- with the reason -- otherwise.
/// </summary>
/// <remarks>
/// For assertions whose threshold is a property of the shipped assembly rather than of the source: allocation
/// ceilings, mainly. Debug codegen adds display classes and unelided async state machines, so a measurement
/// taken there describes the JIT's debug mode and not the package. Widening a ceiling until it covers both
/// configurations raises it past whatever it existed to exclude, which is worse than not asserting at all.
/// <para>
/// Skipped rather than compiled out with <c>#if</c> so that a Debug run still lists the test and says why it
/// did not run. A test that silently disappears from the suite in the configuration most developers use is a
/// test nobody remembers exists.
/// </para>
/// </remarks>
public sealed class ReleaseOnlyFactAttribute : FactAttribute
{
    public ReleaseOnlyFactAttribute()
    {
#if DEBUG
        Skip = "Calibrated against Release codegen. Run 'dotnet test -c Release'.";
#endif
    }
}
