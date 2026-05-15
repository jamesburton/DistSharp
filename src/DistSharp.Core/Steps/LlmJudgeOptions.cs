namespace DistSharp.Core.Steps;

/// <summary>Configuration for <see cref="LlmJudge"/>.</summary>
public sealed class LlmJudgeOptions
{
    /// <summary>Gets or sets the model used by the judge.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets the sampling temperature for judging. Default: 0.0 for deterministic scoring.</summary>
    public float Temperature { get; set; }

    /// <summary>Gets or sets the number of parallel worker tasks. Default: 2.</summary>
    public int Workers { get; set; } = 2;

    /// <summary>Gets or sets the minimum score (1–5) required for a row to pass through. Default: 3.0.</summary>
    public float MinScore { get; set; } = 3.0f;

    /// <summary>Gets or sets the rubric identifier. Built-in: <c>helpfulness_and_correctness</c>, <c>code_quality</c>.</summary>
    public string Rubric { get; set; } = "helpfulness_and_correctness";

    /// <summary>Gets or sets a custom prompt override. When set, replaces the built-in rubric prompt.</summary>
    public string? PromptOverride { get; set; }

    /// <summary>Gets or sets the field name that holds the row's input prompt/instruction. Default: <c>instruction</c>.</summary>
    public string InputField { get; set; } = "instruction";

    /// <summary>Gets or sets the field name that holds the row's output to be judged. Default: <c>response</c>.</summary>
    public string OutputField { get; set; } = "response";
}
