namespace DistSharp.Core.Models;

/// <summary>The role of a participant in a chat conversation.</summary>
public enum ChatRole
{
    /// <summary>The system prompt role.</summary>
    System,

    /// <summary>The human turn.</summary>
    User,

    /// <summary>The model turn.</summary>
    Assistant,
}
