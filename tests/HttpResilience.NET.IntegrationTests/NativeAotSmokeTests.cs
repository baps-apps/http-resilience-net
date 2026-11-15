using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// The Native AOT smoke scenarios (<c>tests/HttpResilience.NET.AotSmoke</c>), under JIT on every run and in a
/// published Native AOT binary on request.
/// </summary>
public class NativeAotSmokeTests
{
    private const string OptIn = "HTTPRESILIENCE_AOT_SMOKE";

    /// <summary>
    /// The scenarios' own logic, without trimming. A check that fails here is wrong before AOT is involved.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheSmokeScenarios_PassUnderJit() =>
        Assert.Empty(await SmokeScenarios.RunAsync());

    /// <summary>
    /// Proves <c>IsTrimmable</c> and <c>IsAotCompatible</c> at run time: a trimmed reflective binder does not
    /// throw, it leaves defaults, and only a published binary shows that.
    /// </summary>
    /// <remarks>
    /// Opt-in (a release gate): publishing takes minutes and needs the platform's native toolchain. Run with
    /// <c>HTTPRESILIENCE_AOT_SMOKE=1 dotnet test -c Release --filter "FullyQualifiedName~NativeAotSmokeTests"</c>.
    /// </remarks>
    [Fact(Timeout = 15 * 60 * 1000)]
    public async Task TheSmokeScenarios_PassInAPublishedNativeAotBinary()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(OptIn) == "1",
            $"Publishes a Native AOT binary, which takes minutes and the native toolchain; set {OptIn}=1 to run it.");

        string root = RepositoryRoot();
        string rid = RuntimeInformation.RuntimeIdentifier;
        string project = Path.Combine(root, "tests", "HttpResilience.NET.AotSmoke", "HttpResilience.NET.AotSmoke.csproj");

        (int published, string publishLog) = await RunAsync(
            "dotnet", ["publish", project, "-c", "Release", "-r", rid], root, NativeLibraryPath());
        Assert.True(published == 0, $"dotnet publish failed:{Environment.NewLine}{Tail(publishLog)}{LinkerHint(publishLog)}");

        string binary = Path.Combine(
            root, "tests", "HttpResilience.NET.AotSmoke", "bin", "Release", "net10.0", rid, "publish",
            OperatingSystem.IsWindows() ? "HttpResilience.NET.AotSmoke.exe" : "HttpResilience.NET.AotSmoke");
        (int exitCode, string output) = await RunAsync(binary, [], root);

        Assert.True(exitCode == 0, output);
        Assert.Contains("AOT smoke passed", output, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HttpResilience.NET.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No HttpResilience.NET.slnx above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// <c>LIBRARY_PATH</c> with Homebrew's library directory appended, when Homebrew's OpenSSL is installed on
    /// macOS: the Homebrew .NET SDK links the native binary with <c>-lssl -lcrypto</c>, which clang cannot find
    /// otherwise. <see langword="null"/> leaves the environment unchanged.
    /// </summary>
    private static string? NativeLibraryPath()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        string? current = Environment.GetEnvironmentVariable("LIBRARY_PATH");
        foreach (string directory in (string[])["/opt/homebrew/lib", "/usr/local/lib"])
        {
            if (!File.Exists(Path.Combine(directory, "libssl.dylib")))
            {
                continue;
            }

            if (current?.Split(':').Contains(directory, StringComparer.Ordinal) == true)
            {
                return null;
            }

            return string.IsNullOrEmpty(current) ? directory : $"{current}:{directory}";
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string file, string[] arguments, string directory, string? libraryPath = null)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (libraryPath is not null)
        {
            start.Environment["LIBRARY_PATH"] = libraryPath;
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            // A timed-out test must not leave the publish or the binary running.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        return (process.ExitCode, await output + await error);
    }

    private static string Tail(string log) => log.Length <= 4000 ? log : log[^4000..];

    /// <summary>The fix for the Homebrew .NET SDK's link failure, when the publish log shows it.</summary>
    private static string LinkerHint(string log) =>
        log.Contains("library 'ssl' not found", StringComparison.Ordinal)
            ? $"{Environment.NewLine}The linker cannot find OpenSSL (Homebrew .NET SDK): install it " +
              "(brew install openssl@3), or put its lib directory on LIBRARY_PATH (CONTRIBUTING.md)."
            : string.Empty;
}
