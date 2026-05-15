using System.Net;
using System.Text;
using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Providers;
using DistSharp.Providers.OpenAI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DistSharp.Providers.Tests;

public sealed class OpenAICompatibleProviderTests
{
    [Fact]
    public async Task CompleteAsync_ReturnsContent_OnSuccessResponse()
    {
        var handler = new StubHandler(_ => Ok("hello"));
        var http = new HttpClient(handler);
        var options = new OpenAIProviderOptions { ApiKey = "test", BaseUrl = "https://api.openai.com", DefaultModel = "gpt-4.1-mini", MaxRetries = 0 };
        var provider = new OpenAIProvider(http, options, NullLogger<OpenAIProvider>.Instance);

        var result = await provider.CompleteAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new LlmRequestOptions { Temperature = 0.5f, MaxTokens = 100 },
            CancellationToken.None);

        result.Should().Be("hello");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("test");
    }

    [Fact]
    public async Task CompleteAsync_BuildsCorrectRequestBody()
    {
        var handler = new StubHandler(_ => Ok("ok"));
        var http = new HttpClient(handler);
        var options = new OpenAIProviderOptions { ApiKey = "k", BaseUrl = "https://api.openai.com", DefaultModel = "default-model", MaxRetries = 0 };
        var provider = new OpenAIProvider(http, options, NullLogger<OpenAIProvider>.Instance);

        await provider.CompleteAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, "sys"),
                new ChatMessage(ChatRole.User, "user-msg"),
            },
            new LlmRequestOptions { Model = "override", Temperature = 0.9f, MaxTokens = 256 },
            CancellationToken.None);

        var json = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        json.GetProperty("model").GetString().Should().Be("override");
        json.GetProperty("temperature").GetDouble().Should().BeApproximately(0.9, 0.01);
        json.GetProperty("max_tokens").GetInt32().Should().Be(256);
        json.GetProperty("messages").GetArrayLength().Should().Be(2);
        json.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("system");
        json.GetProperty("messages")[1].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task CompleteAsync_RetriesOn429_ThenSucceeds()
    {
        var attempt = 0;
        var handler = new StubHandler(_ => ++attempt == 1 ? Err(HttpStatusCode.TooManyRequests) : Ok("recovered"));
        var http = new HttpClient(handler);
        var options = new OpenAIProviderOptions { ApiKey = "k", BaseUrl = "https://api.openai.com", DefaultModel = "m", MaxRetries = 3, InitialRetryDelay = TimeSpan.FromMilliseconds(1) };
        var provider = new OpenAIProvider(http, options, NullLogger<OpenAIProvider>.Instance);

        var result = await provider.CompleteAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new LlmRequestOptions(),
            CancellationToken.None);

        result.Should().Be("recovered");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsLlmProviderException_AfterRetryBudgetExhausted()
    {
        var handler = new StubHandler(_ => Err(HttpStatusCode.ServiceUnavailable, "{\"error\":\"down\"}"));
        var http = new HttpClient(handler);
        var options = new OpenAIProviderOptions { ApiKey = "k", BaseUrl = "https://api.openai.com", DefaultModel = "m", MaxRetries = 2, InitialRetryDelay = TimeSpan.FromMilliseconds(1) };
        var provider = new OpenAIProvider(http, options, NullLogger<OpenAIProvider>.Instance);

        var act = () => provider.CompleteAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new LlmRequestOptions(),
            CancellationToken.None);

        await act.Should().ThrowAsync<LlmProviderException>()
            .Where(ex => ex.StatusCode == 503 && ex.ProviderName == "openai" && (ex.ResponseBody ?? string.Empty).Contains("down"));
        handler.Requests.Should().HaveCount(3); // 1 initial + 2 retries
    }

    [Fact]
    public async Task AzureProvider_PutsDeploymentInUrl_AndOmitsModelFromBody()
    {
        var handler = new StubHandler(_ => Ok("ok"));
        var http = new HttpClient(handler);
        var options = new AzureOpenAIProviderOptions { ApiKey = "key", BaseUrl = "https://my-resource.openai.azure.com", DefaultModel = "gpt-4-deployment", MaxRetries = 0 };
        var provider = new AzureOpenAIProvider(http, options, NullLogger<AzureOpenAIProvider>.Instance);

        await provider.CompleteAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") },
            new LlmRequestOptions(),
            CancellationToken.None);

        var url = handler.Requests[0].RequestUri!.ToString();
        url.Should().Contain("/openai/deployments/gpt-4-deployment/chat/completions");
        url.Should().Contain("api-version=");

        var json = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        json.TryGetProperty("model", out _).Should().BeFalse();

        handler.Requests[0].Headers.GetValues("api-key").Should().ContainSingle().Which.Should().Be("key");
    }

    private static HttpResponseMessage Ok(string content)
    {
        var json = $@"{{""choices"":[{{""message"":{{""content"":""{content}""}}}}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Err(HttpStatusCode code, string body = "{}")
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                this.Bodies.Add(body);
            }
            else
            {
                this.Bodies.Add(string.Empty);
            }

            this.Requests.Add(request);
            return this.respond(request);
        }
    }
}
