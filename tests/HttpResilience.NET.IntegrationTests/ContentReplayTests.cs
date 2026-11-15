using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// A retry re-sends the same <see cref="HttpRequestMessage"/>, so its content must be replayable.
/// </summary>
/// <remarks>
/// Real sockets and the factory's <see cref="SocketsHttpHandler"/>: <c>TestServer</c> reads content through
/// <c>ReadAsStreamAsync</c>, which skips <see cref="StreamContent"/>'s consumed-stream check, so it would report
/// an empty body where production fails the retry.
/// </remarks>
public class ContentReplayTests
{
    /// <summary>
    /// A single-pass stream is delivered once; the retry cannot read it again and the caller gets an exception,
    /// although the origin already received the request.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ANonSeekableBody_IsDeliveredOnce_AndTheRetryFails()
    {
        await using var origin = await Origin.StartAsync();

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => PostAsync(origin, buffered: false));

        Assert.Equal(["payload"], origin.Bodies);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    /// <summary>
    /// Buffered content is replayed in full on every attempt.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ABufferedBody_IsDeliveredInFull_OnEveryAttempt()
    {
        await using var origin = await Origin.StartAsync();

        HttpStatusCode status = await PostAsync(origin, buffered: true);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(["payload", "payload", "payload"], origin.Bodies);
    }

    private static async Task<HttpStatusCode> PostAsync(Origin origin, bool buffered)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HttpResilience:Retry:BaseDelay"] = "00:00:00",
            ["HttpResilience:Clients:api:Retry:RetryableMethods:0"] = "POST"
        }).Build());
        services.AddHttpClient("api").AddHttpResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var content = new StreamContent(new NonSeekableStream("payload"u8.ToArray()));
        if (buffered)
        {
            await content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        }

        using HttpResponseMessage response = await client.PostAsync(
            $"{origin.Authority}/orders", content, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>A Kestrel origin that records each delivered body and fails every request.</summary>
    private sealed class Origin : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly List<string> _bodies = [];

        private Origin(IHost host) => _host = host;

        public string Authority => _host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_bodies)
                {
                    return [.. _bodies];
                }
            }
        }

        public static async Task<Origin> StartAsync()
        {
            Origin? origin = null;
            IHost host = Host.CreateDefaultBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseKestrel();
                    web.UseUrls("http://127.0.0.1:0");
                    web.ConfigureServices(services => services.AddRouting());
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapPost("/orders", async (HttpContext context) =>
                        {
                            using var reader = new StreamReader(context.Request.Body);
                            string body = await reader.ReadToEndAsync(context.RequestAborted);
                            lock (origin!._bodies)
                            {
                                origin._bodies.Add(body);
                            }

                            return Results.StatusCode(500);
                        }));
                    });
                })
                .Build();

            origin = new Origin(host);
            await host.StartAsync(TestContext.Current.CancellationToken);
            return origin;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    /// <summary>A stream that can be read once, like a network or pipe stream.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
