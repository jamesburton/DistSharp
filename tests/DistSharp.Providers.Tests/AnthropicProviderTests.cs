using System.Net;
using System.Text;
using System.Text.Json;
using DistSharp.Core.Models;
using DistSharp.Providers.Anthropic;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DistSharp.Providers.Tests;

public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task CompleteAsync_FlattensSystemMessagesIntoSystemField()
    {
        var handler = new StubHandler(_ => Ok("hi"));
        var http = new HttpClient(handler);
        var options = new AnthropicProviderOptions { ApiKey = "k", DefaultModel = "claude-opus-4-7", MaxRetries = 0 };
        var provider = new AnthropicProvider(http, options, NullLogger<AnthropicProvider>.Instance);

        await provider.CompleteAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, "sys1"),
                new ChatMessage(ChatRole.System, "sys2"),
                new ChatMessage(ChatRole.User, "u1"),
                new ChatMessage(ChatRole.Assistant, "a1"),
                new ChatMessage(ChatRole.User, "u2"),
            },
            new LlmRequestOptions { MaxTokens = 100, Temperature = 0.3f },
            CancellationToken.None);

        var json = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        json.GetProperty("system").GetString().Should().Contain("sys1").And.Contain("sys2");
        json.GetProperty("messages").GetArrayLength().Should().Be(3);
        json.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
        json.GetProperty("messages")[1].GetProperty("role").GetString().Should().Be("assistant");
        json.GetProperty("messages")[2].GetProperty("role").GetString().Should().Be("user");
        json.GetProperty("max_tokens").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task CompleteAsync_DefaultsMaxTokensTo4096_WhenNotSpecified()
    {
        var handler = new StubHandler(_ => Ok("ok"));
        var http = new HttpClient(handler);
        var options = new AnthropicProviderOptions { ApiKey = "k", DefaultModel = "m", MaxRetries = 0 };
        var provider = new AnthropicProvider(http, options, NullLogger<AnthropicProvider>.Instance);

        await provider.CompleteAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new LlmRequestOptions(), CancellationToken.None);

        var json = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        json.GetProperty("max_tokens").GetInt32().Should().Be(4096);
    }

    [Fact]
    public async Task CompleteAsync_SetsAnthropicHeaders()
    {
        var handler = new StubHandler(_ => Ok("ok"));
        var http = new HttpClient(handler);
        var options = new AnthropicProviderOptions { ApiKey = "secret-key", DefaultModel = "m", MaxRetries = 0 };
        var provider = new AnthropicProvider(http, options, NullLogger<AnthropicProvider>.Instance);

        await provider.CompleteAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new LlmRequestOptions(), CancellationToken.None);

        handler.Requests[0].Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("secret-key");
        handler.Requests[0].Headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2023-06-01");
    }

    [Fact]
    public async Task CompleteAsync_ExtractsTextFromContentArray()
    {
        var handler = new StubHandler(_ => Ok("hello world"));
        var http = new HttpClient(handler);
        var options = new AnthropicProviderOptions { ApiKey = "k", DefaultModel = "m", MaxRetries = 0 };
        var provider = new AnthropicProvider(http, options, NullLogger<AnthropicProvider>.Instance);

        var result = await provider.CompleteAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new LlmRequestOptions(), CancellationToken.None);

        result.Should().Be("hello world");
    }

    private static HttpResponseMessage Ok(string text)
    {
        var json = $@"{{""content"":[{{""type"":""text"",""text"":""{text}""}}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
                this.Bodies.Add(await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
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
