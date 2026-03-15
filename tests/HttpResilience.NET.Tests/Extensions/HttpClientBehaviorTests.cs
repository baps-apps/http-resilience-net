using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using HttpResilience.NET.Abstractions;
using HttpResilience.NET.Extensions;

namespace HttpResilience.NET.Tests.Extensions;

public class HttpClientBehaviorTests
{
    private static ServiceProvider BuildProvider(IDictionary<string, string?> configData, Action<IServiceCollection>? configureServices = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);

        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _last;

        public SequenceHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> sequence)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(sequence);
            _last = _responses.Count > 0
                ? _responses.Last()
                : _ => new HttpResponseMessage(HttpStatusCode.OK);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var factory = _responses.Count > 0 ? _responses.Dequeue() : _last;
            return Task.FromResult(factory(request));
        }
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public DelayingHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }

    [Fact]
    public async Task Retry_RetriesOnTransientFailure()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "2",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "1",
            ["HttpResilienceOptions:Retry:BackoffType"] = "Constant",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "5"
        };

        int calls = 0;

        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("RetryClient")
                    .AddHttpClientWithResilience(configuration)
                    .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => { calls++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); },
                        _ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }
                    }));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("RetryClient");

        HttpResponseMessage response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task Fallback_ReturnsSyntheticResponseOnFailure()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "503"
        };

        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("FallbackClient")
                    .AddHttpClientWithResilience(configuration)
                    .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => throw new HttpRequestException("Simulated failure")
                    }));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("FallbackClient");

        HttpResponseMessage response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Hedging_SucceedsWhenOneEndpointFails()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineType"] = "Hedging",
            ["HttpResilienceOptions:Hedging:DelaySeconds"] = "0",
            ["HttpResilienceOptions:Hedging:MaxHedgedAttempts"] = "1",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "5",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "5",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "2"
        };

        int firstEndpointCalls = 0;
        int secondEndpointCalls = 0;

        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("HedgingClient")
                    .AddHttpClientWithResilience(configuration, requestTimeoutSeconds: 5)
                    .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ =>
                        {
                            firstEndpointCalls++;
                            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                            {
                                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://unhealthy.example.com/")
                            };
                        },
                        _ =>
                        {
                            secondEndpointCalls++;
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://healthy.example.com/")
                            };
                        }
                    }));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("HedgingClient");

        HttpResponseMessage response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(firstEndpointCalls >= 1);
        Assert.True(secondEndpointCalls >= 1);
    }

    [Fact]
    public async Task Fallback_syntheticResponseHasRequestMessageSetWhenOutcomeWasFailedResult()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "503",
            ["HttpResilienceOptions:Fallback:OnlyOn5xx"] = "true"
        };

        var requestUri = new Uri("https://example.com/fallback-test");
        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("FallbackRequestMessageClient")
                    .AddHttpClientWithResilience(configuration)
                    .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        req =>
                        {
                            var res = new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = req };
                            return res;
                        }
                    }));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("FallbackRequestMessageClient");

        HttpResponseMessage response = await client.GetAsync(requestUri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.RequestMessage);
        Assert.Same(requestUri, response.RequestMessage.RequestUri);
    }

    [Fact]
    public async Task Timeout_cancelsRequestWhenAttemptExceedsAttemptTimeout()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "3",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "1"
        };

        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("TimeoutClient")
                    .AddHttpClientWithResilience(configuration)
                    .ConfigurePrimaryHttpMessageHandler(() => new DelayingHandler(TimeSpan.FromSeconds(5)));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("TimeoutClient");

        await Assert.ThrowsAsync<Polly.Timeout.TimeoutRejectedException>(async () =>
            await client.GetAsync("https://example.com/"));
    }

    [Fact]
    public async Task CustomFallbackHandler_returnsHandlerResponseWhenHandlerReturnsNonNull()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "503"
        };

        var customResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("from-custom-handler") };
        var handler = new CustomFallbackHandler(customResponse);

        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("CustomFallbackClient")
                    .AddHttpClientWithResilience(configuration, requestTimeoutSeconds: null, fallbackHandler: handler)
                    .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => throw new HttpRequestException("fail")
                    }));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("CustomFallbackClient");

        HttpResponseMessage response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("from-custom-handler", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CustomFallbackHandler_returnsSyntheticResponseWhenHandlerReturnsNull()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "503",
            ["HttpResilienceOptions:Fallback:ResponseBody"] = "synthetic-body"
        };

        var handler = new CustomFallbackHandler(null);

        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("CustomFallbackNullClient")
                    .AddHttpClientWithResilience(configuration, requestTimeoutSeconds: null, fallbackHandler: handler)
                    .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        _ => throw new HttpRequestException("fail")
                    }));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("CustomFallbackNullClient");

        HttpResponseMessage response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("synthetic-body", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfigurePipeline_addsOuterHandlerThatRuns()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "10"
        };

        const string CustomHeaderName = "X-Custom-Pipeline";
        const string CustomHeaderValue = "configured";

        using ServiceProvider provider = BuildProvider(
            configData,
            services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                services.AddHttpClient("ConfigurePipelineClient")
                    .AddHttpClientWithResilience(configuration, requestTimeoutSeconds: null, fallbackHandler: null, configurePipeline: b =>
                    {
                        b.AddHttpMessageHandler(() => new AddHeaderHandler(CustomHeaderName, CustomHeaderValue));
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
                    {
                        req =>
                        {
                            Assert.True(req.Headers.TryGetValues(CustomHeaderName, out var values));
                            Assert.Equal(CustomHeaderValue, values!.First());
                            return new HttpResponseMessage(HttpStatusCode.OK);
                        }
                    }));
            });

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("ConfigurePipelineClient");

        HttpResponseMessage response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SectionBasedBinding_usesOptionsFromSpecifiedSection()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "60",
            ["HttpResilienceOptions:TenantA:Enabled"] = "true",
            ["HttpResilienceOptions:TenantA:Timeout:TotalRequestTimeoutSeconds"] = "5",
            ["HttpResilienceOptions:TenantA:Timeout:AttemptTimeoutSeconds"] = "2",
            ["HttpResilienceOptions:TenantA:Retry:MaxRetryAttempts"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);

        var tenantSection = configuration.GetSection("HttpResilienceOptions:TenantA");
        services.AddHttpClient("TenantAClient")
            .AddHttpClientWithResilience(tenantSection)
            .ConfigurePrimaryHttpMessageHandler(() => new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            }));

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("TenantAClient");

        HttpResponseMessage response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = new HttpResilience.NET.Options.HttpResilienceOptions();
        tenantSection.Bind(options);
        Assert.True(options.Enabled);
        Assert.Equal(5, options.Timeout.TotalRequestTimeoutSeconds);
        Assert.Equal(1, options.Retry.MaxRetryAttempts);
    }
}

internal sealed class CustomFallbackHandler : IHttpFallbackHandler
{
    private readonly HttpResponseMessage? _response;

    public CustomFallbackHandler(HttpResponseMessage? response) => _response = response;

    public ValueTask<HttpResponseMessage?> TryHandleAsync(HttpFallbackContext context, CancellationToken cancellationToken) =>
        new(_response);
}

internal sealed class AddHeaderHandler : DelegatingHandler
{
    private readonly string _name;
    private readonly string _value;

    public AddHeaderHandler(string name, string value)
    {
        _name = name;
        _value = value;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(_name, _value);
        return await base.SendAsync(request, cancellationToken);
    }
}

