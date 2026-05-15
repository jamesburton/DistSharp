namespace DistSharp.Core.Models;

/// <summary>A single message in a chat conversation.</summary>
/// <param name="Role">The role of the message sender.</param>
/// <param name="Content">The text content of the message.</param>
public sealed record ChatMessage(ChatRole Role, string Content);
