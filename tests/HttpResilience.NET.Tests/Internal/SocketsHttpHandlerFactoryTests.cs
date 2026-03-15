using System.Net.Http;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Internal;

public class SocketsHttpHandlerFactoryTests
{
    [Fact]
    public void Create_ReturnsNonNullHandler()
    {
        var options = new HttpResilienceOptions();
        SocketsHttpHandler handler = SocketsHttpHandlerFactory.Create(options);
        Assert.NotNull(handler);
    }

    [Fact]
    public void Create_SetsMaxConnectionsPerServerFromOptions()
    {
        var options = new HttpResilienceOptions { Connection = { MaxConnectionsPerServer = 42 } };
        SocketsHttpHandler handler = SocketsHttpHandlerFactory.Create(options);
        Assert.Equal(42, handler.MaxConnectionsPerServer);
    }

    [Fact]
    public void Create_SetsPooledConnectionTimeoutsFromOptions()
    {
        var options = new HttpResilienceOptions();
        SocketsHttpHandler handler = SocketsHttpHandlerFactory.Create(options);
        Assert.Equal(TimeSpan.FromSeconds(options.Connection.PooledConnectionIdleTimeoutSeconds), handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(options.Connection.PooledConnectionLifetimeSeconds), handler.PooledConnectionLifetime);
    }

    [Fact]
    public void Create_SetsCustomPooledConnectionTimeouts()
    {
        var options = new HttpResilienceOptions
        {
            Connection =
            {
                PooledConnectionIdleTimeoutSeconds = 60,
                PooledConnectionLifetimeSeconds = 300
            }
        };
        SocketsHttpHandler handler = SocketsHttpHandlerFactory.Create(options);
        Assert.Equal(TimeSpan.FromSeconds(60), handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), handler.PooledConnectionLifetime);
    }

    [Fact]
    public void Create_SetsConnectTimeoutFromOptions()
    {
        var options = new HttpResilienceOptions { Connection = { ConnectTimeoutSeconds = 5 } };
        SocketsHttpHandler handler = SocketsHttpHandlerFactory.Create(options);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
    }
}
