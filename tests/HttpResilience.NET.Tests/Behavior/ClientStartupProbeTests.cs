using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The client startup probe creates every client this package configured, once, at host start, so that a
/// failure only reachable through handler construction fails the deployment instead of the first request
/// that happens to use that client.
/// </summary>
/// <remarks>
/// The failure it is for: <c>Connection:Enabled</c> on a client whose primary handler is not a
/// <c>SocketsHttpHandler</c>. That is a DI fact, not an options fact, so <c>ValidateOnStart</c> cannot see it
/// -- the handler chain is not built until <c>CreateClient</c>. A client used on a rare code path therefore
/// threw hours after the deploy.
/// <para>
/// <b>On by default.</b> It was opt-in until the fourth review, on the reasoning that eagerly constructing
/// every handler chain has a cost and is wrong for a process that registers clients it may never use. That
/// reasoning is weak for a client which has explicitly opted into a resilience pipeline -- registering one
/// is the statement of intent to use it -- and it left a control that a fleet of fifty services reaches only
/// by each of them remembering a checklist item. Opting out is a configuration key, so it stays available
/// during an incident without a redeploy.
/// </para>
/// </remarks>
public class ClientStartupProbeTests
{
    private static ServiceProvider Build(
        bool explicitCall = false,
        string? validateClientsOnStart = null,
        Action<IHttpClientBuilder>? afterRegistration = null)
    {
        Settings settings = Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:PooledConnectionLifetime", "00:01:00")
            .Set("Connection:PooledConnectionIdleTimeout", "00:00:30");

        if (validateClientsOnStart is not null)
        {
            settings.Set("ValidateClientsOnStart", validateClientsOnStart);
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        if (explicitCall)
        {
            services.ValidateHttpResilienceClientsOnStart();
        }

        IHttpClientBuilder builder = services.AddHttpClient("test").AddHttpResilience();
        afterRegistration?.Invoke(builder);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task StartAsync(ServiceProvider provider)
    {
        foreach (IHostedService service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
        }
    }

    private static Action<IHttpClientBuilder> UnconfigurableHandler() =>
        builder => builder.ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());

    /// <summary>
    /// The probe is registered by <c>AddHttpResilience</c> itself, with nothing else asked for.
    /// </summary>
    /// <remarks>
    /// Production change that would make this fail: removing the registration from
    /// <c>AddHttpResilience</c> and returning it to <c>ValidateHttpResilienceClientsOnStart</c> alone. The
    /// container would then start cleanly and the failure would wait for traffic, which is the state this
    /// change exists to end.
    /// </remarks>
    [Fact]
    public async Task TheProbeRuns_WithoutBeingAskedFor()
    {
        await using ServiceProvider provider = Build(afterRegistration: UnconfigurableHandler());

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));

        Assert.Contains("Connection:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SocketsHttpHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains("test", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opt-out key turns it off, and the failure goes back to waiting for the first request.
    /// </summary>
    /// <remarks>
    /// Root-only and read from the raw section, like <c>AllowUnusedClientSections</c>: it is a statement
    /// about the process, and binding it per client would make it look inheritable when it is not.
    /// </remarks>
    [Fact]
    public async Task TheOptOutKey_TurnsTheProbeOff()
    {
        await using ServiceProvider provider = Build(
            validateClientsOnStart: "false",
            afterRegistration: UnconfigurableHandler());

        await StartAsync(provider);

        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("test"));
    }

    /// <summary>
    /// A value that is neither true nor false fails, rather than being read as one of them.
    /// </summary>
    /// <remarks>
    /// The same rule <c>AllowUnusedClientSections</c> follows, for the same reason: an operator who wrote
    /// "yes" would otherwise see the probe run anyway, on a key they believed they had turned off.
    /// </remarks>
    [Fact]
    public async Task AMalformedOptOutValue_FailsAtStart()
    {
        await using ServiceProvider provider = Build(validateClientsOnStart: "yes");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));

        Assert.Contains("ValidateClientsOnStart", exception.Message, StringComparison.Ordinal);
        Assert.Contains("yes", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opt-out key set to false while the code also calls
    /// <c>ValidateHttpResilienceClientsOnStart</c> fails, naming both.
    /// </summary>
    /// <remarks>
    /// Two written statements about the same thing, one of which is not in force -- the class of silent
    /// contradiction this package fails startup for everywhere else. The direction that matters is this one:
    /// the operator reaching for the key during an incident is the person who most needs to be told that a
    /// line of code is overriding them.
    /// </remarks>
    [Fact]
    public async Task TheOptOutKey_ContradictingAnExplicitCall_FailsAtStart()
    {
        await using ServiceProvider provider = Build(explicitCall: true, validateClientsOnStart: "false");

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));

        Assert.Contains("ValidateClientsOnStart", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ValidateHttpResilienceClientsOnStart", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A container whose clients are all constructible must start, and the probe must not be the thing that
    /// makes a healthy service fail to boot.
    /// </summary>
    [Fact]
    public async Task AWellFormedContainer_StartsCleanly()
    {
        await using ServiceProvider provider = Build();

        await StartAsync(provider);
    }

    /// <summary>
    /// The now-redundant explicit call still works and still creates every client exactly once.
    /// </summary>
    /// <remarks>
    /// It stays in the public surface because removing it would break every consumer that followed the
    /// production checklist. Calling it twice -- a shared platform extension and the application -- must
    /// not register a second probe either.
    /// </remarks>
    [Fact]
    public async Task TheExplicitCallIsStillSupported_AndIdempotent()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.ValidateHttpResilienceClientsOnStart();
        services.ValidateHttpResilienceClientsOnStart();
        services.AddHttpClient("test").AddHttpResilience();

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Single(provider.GetServices<IHostedService>());
        await StartAsync(provider);
    }

    /// <summary>
    /// Registering the schema and never calling the opt-in registers exactly one probe, not two.
    /// </summary>
    [Fact]
    public void TheDefaultRegistration_AddsExactlyOneProbe()
    {
        using ServiceProvider provider = Build();

        Assert.Single(provider.GetServices<IHostedService>());
    }

    /// <summary>
    /// Registering the schema and no HTTP clients at all must still start.
    /// </summary>
    /// <remarks>
    /// A regression introduced by making the probe the default and caught here rather than in a service.
    /// While the probe was opt-in it was only ever registered by someone who had clients; registered
    /// unconditionally it is activated unconditionally, and <see cref="IHttpClientFactory"/> is not in the
    /// container until something calls <c>AddHttpClient</c>. A shared platform extension calling
    /// <c>AddHttpResilience</c> in a service with no outbound clients would have failed to start with
    /// "Unable to resolve service for type 'System.Net.Http.IHttpClientFactory'" -- a message naming neither
    /// this package nor anything the operator could act on.
    /// <para>
    /// Production change that would make this fail: removing the <c>AddHttpClient()</c> core registration
    /// from <c>AddHttpResilience</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AContainerWithNoHttpClientsAtAll_StillStarts()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        await StartAsync(provider);
    }

    /// <summary>
    /// Only the clients this package configured. Creating an unrelated client would make the probe an
    /// opinion about someone else's registration, and could fail a deployment over a handler this package
    /// never touched.
    /// </summary>
    [Fact]
    public async Task ClientsThisPackageDidNotConfigure_AreNotCreated()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("test").AddHttpResilience();

        // Not registered with this package, and deliberately unconstructible.
        services.AddHttpClient("someone-elses")
            .ConfigurePrimaryHttpMessageHandler(
                () => throw new InvalidOperationException("this handler must never be constructed"));

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        await StartAsync(provider);
    }
}
