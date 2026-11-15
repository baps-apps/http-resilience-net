using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>
/// Asserts that configuration fails when the host starts, which is when client options are first built.
/// </summary>
internal static class StartupAssert
{
    /// <summary>Runs <paramref name="register"/>, builds the container and runs startup validation.</summary>
    public static OptionsValidationException Fails(IServiceCollection services, Action register)
    {
        register();
        return Fails(() => services.BuildServiceProvider());
    }

    /// <summary>Builds the container and runs startup validation, returning every failure as one exception.</summary>
    public static OptionsValidationException Fails(Func<ServiceProvider> build)
    {
        using ServiceProvider provider = build();
        Exception? captured = Record.Exception(() =>
        {
            foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
            {
                validator.Validate();
            }
        });

        Assert.NotNull(captured);
        List<OptionsValidationException> failures = [.. Flatten(captured)];
        Assert.True(failures.Count > 0, $"Expected an options validation failure, got {captured}");

        return new OptionsValidationException(
            failures[0].OptionsName, failures[0].OptionsType, failures.SelectMany(f => f.Failures));
    }

    private static IEnumerable<OptionsValidationException> Flatten(Exception exception) => exception switch
    {
        OptionsValidationException validation => [validation],
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten),
        _ => []
    };
}
