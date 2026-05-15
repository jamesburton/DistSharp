namespace DistSharp.Core.Steps;

/// <summary>Configuration for <see cref="LlmStep"/>.</summary>
public sealed class LlmStepOptions
{
    /// <summary>Gets or sets the dataset type that drives prompt selection.</summary>
    public string DatasetType { get; set; } = "explanation";

    /// <summary>Gets or sets the model identifier to pass to the provider.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets the sampling temperature.</summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>Gets or sets the maximum tokens to request from the provider.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Gets or sets the number of parallel worker tasks. Default: 4.</summary>
    public int Workers { get; set; } = 4;

    /// <summary>Gets or sets a system-prompt override that replaces the prompt-builder's system message.</summary>
    public string? SystemPromptOverride { get; set; }

    /// <summary>Gets or sets a value indicating whether rows where LLM call or parsing failed are dropped. Default: <see langword="true"/>.</summary>
    public bool DropOnError { get; set; } = true;
}
