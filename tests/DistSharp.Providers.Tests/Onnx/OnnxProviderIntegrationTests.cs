using DistSharp.Core.Models;
using DistSharp.Providers.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DistSharp.Providers.Tests.Onnx;

/// <summary>
/// Manual integration tests requiring a real ONNX model on disk.
/// Skipped in CI — run locally after downloading microsoft/Phi-4-mini-instruct-onnx.
/// </summary>
[Trait("Category", "ManualIntegration")]
public sealed class OnnxProviderIntegrationTests
{
    [Fact(Skip = "Requires microsoft/Phi-4-mini-instruct-onnx in HF cache. Run manually.")]
    public async Task CompleteAsync_ReturnsCompletion_ForPhiModel()
    {
        var options = new OnnxProviderOptions
        {
            DefaultModel = "microsoft/Phi-4-mini-instruct-onnx",
            MaxConcurrentSessions = 1,
        };

        var cache = new HuggingFaceModelCache(null, new HttpClient());
        var provider = new OnnxProvider(options, cache, NullLogger<OnnxProvider>.Instance);

        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Say hello in one word."),
        };

        var result = await provider.CompleteAsync(messages, new LlmRequestOptions(), CancellationToken.None);

        Assert.NotEmpty(result);
    }
}
