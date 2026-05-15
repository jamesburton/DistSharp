using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>
/// The output of a prompt builder: the chat messages to send to the LLM, prepared row fields to set before
/// calling the LLM, and a delegate that knows how to merge the LLM response into row fields afterwards.
/// </summary>
public sealed record PromptResult
{
    /// <summary>Gets the chat messages to send.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Gets the fields to set on the row before the LLM call (e.g. <c>instruction</c>, <c>prompt</c>).</summary>
    public required IReadOnlyDictionary<string, object?> PreparedFields { get; init; }

    /// <summary>
    /// Gets the response parser. Returns the fields to merge into the row from the raw LLM response,
    /// or <see langword="null"/> if the response could not be parsed (caller should drop the row).
    /// </summary>
    public required Func<string, IReadOnlyDictionary<string, object?>?> ParseResponse { get; init; }
}
