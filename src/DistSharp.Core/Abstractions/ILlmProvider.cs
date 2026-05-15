using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Abstraction over an LLM API. Implemented in <c>DistSharp.Providers</c>.</summary>
public interface ILlmProvider
{
    /// <summary>Gets the provider name, e.g. <c>openai</c>, <c>anthropic</c>.</summary>
    string ProviderName { get; }

    /// <summary>Sends <paramref name="messages"/> to the model and returns the completion text.</summary>
    /// <param name="messages">The conversation turns to send.</param>
    /// <param name="options">Per-request options such as model, temperature, and max tokens.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The model's completion text.</returns>
    Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        LlmRequestOptions options,
        CancellationToken cancellationToken);
}
