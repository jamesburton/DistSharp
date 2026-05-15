namespace DistSharp.Core.Models;

/// <summary>Per-request options passed to an <c>ILlmProvider</c>.</summary>
public sealed class LlmRequestOptions
{
    /// <summary>Gets or initialises the model identifier. When <see langword="null"/>, the provider uses its configured default.</summary>
    public string? Model { get; init; }

    /// <summary>Gets or initialises the sampling temperature. When <see langword="null"/>, the provider uses its default.</summary>
    public float? Temperature { get; init; }

    /// <summary>Gets or initialises the maximum number of tokens to generate. When <see langword="null"/>, the provider uses its default.</summary>
    public int? MaxTokens { get; init; }
}
