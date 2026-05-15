using System.Net;
using System.Text;
using System.Text.Json;
using DistSharp.Core.Models;
using DistSharp.Providers.Gemini;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DistSharp.Providers.Tests;

public sealed class GeminiProviderTests
{
    [Fact]
    public async Task CompleteAsync_MapsAssistantRoleToModel()
    {
        var handler = new StubHandler(_ => Ok("ok"));
        var http = new HttpClient(handler);
        var options = new GeminiProviderOptions { ApiKey = "k", DefaultModel = "gemini-pro", MaxRetries = 0 };
        var provider = new GeminiProvider(http, options, NullLogger<GeminiProvider>.Instance);

        await provider.CompleteAsync(
            new[]
            {
                new ChatMessage(ChatRole.User, "u1"),
                new ChatMessage(ChatRole.Assistant, "a1"),
            },
            new LlmRequestOptions(),
            CancellationToken.None);

        var json = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        var contents = json.GetProperty("contents");
        contents[0].GetProperty("role").GetString().Should().Be("user");
        contents[1].GetProperty("role").GetString().Should().Be("model");
    }

    [Fact]
    public async Task CompleteAsync_PutsSystemMessagesInSystemInstruction()
    {
        var handler = new StubHandler(_ => Ok("ok"));
        var http = new HttpClient(handler);
        var options = new GeminiProviderOptions { ApiKey = "k", DefaultModel = "m", MaxRetries = 0 };
        var provider = new GeminiProvider(http, options, NullLogger<GeminiProvider>.Instance);

        await provider.CompleteAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, "sys-msg"),
                new ChatMessage(ChatRole.User, "u1"),
            },
            new LlmRequestOptions(),
            CancellationToken.None);

        var json = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        json.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("sys-msg");
        json.GetProperty("contents").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task CompleteAsync_PutsApiKeyInQueryString_AndModelInPath()
    {
        var handler = new StubHandler(_ => Ok("ok"));
        var http = new HttpClient(handler);
        var options = new GeminiProviderOptions { ApiKey = "secret", DefaultModel = "gemini-2.5-pro", MaxRetries = 0 };
        var provider = new GeminiProvider(http, options, NullLogger<GeminiProvider>.Instance);

        await provider.CompleteAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new LlmRequestOptions(), CancellationToken.None);

        var url = handler.Requests[0].RequestUri!.ToString();
        url.Should().Contain("/v1beta/models/gemini-2.5-pro:generateContent");
        url.Should().Contain("key=secret");
    }

    [Fact]
    public async Task CompleteAsync_ExtractsTextFromCandidatesPath()
    {
        var handler = new StubHandler(_ => Ok("hello gemini"));
        var http = new HttpClient(handler);
        var options = new GeminiProviderOptions { ApiKey = "k", DefaultModel = "m", MaxRetries = 0 };
        var provider = new GeminiProvider(http, options, NullLogger<GeminiProvider>.Instance);

        var result = await provider.CompleteAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, new LlmRequestOptions(), CancellationToken.None);

        result.Should().Be("hello gemini");
    }

    private static HttpResponseMessage Ok(string text)
    {
        var json = $@"{{""candidates"":[{{""content"":{{""parts"":[{{""text"":""{text}""}}]}}}}]}}";
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
