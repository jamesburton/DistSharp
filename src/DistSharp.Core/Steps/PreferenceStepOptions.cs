namespace DistSharp.Core.Steps;

/// <summary>Configuration for <see cref="PreferenceStep"/>.</summary>
public sealed class PreferenceStepOptions
{
    /// <summary>Gets or sets the dataset type that drives prompt selection (e.g. <c>explanation</c>, <c>unit-test</c>).</summary>
    public string DatasetType { get; set; } = "explanation";

    /// <summary>Gets or sets the model identifier used for both chosen and rejected generation calls.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets the sampling temperature for the chosen response. Default: 0.7.</summary>
    public float ChosenTemperature { get; set; } = 0.7f;

    /// <summary>Gets or sets the sampling temperature for the rejected response. Default: 1.4.</summary>
    public float RejectedTemperature { get; set; } = 1.4f;

    /// <summary>
    /// Gets or sets the rejection strategy. Supported: <c>higher_temperature</c>.
    /// Other values cause <see cref="NotSupportedException"/> at runtime.
    /// </summary>
    public string RejectionStrategy { get; set; } = "higher_temperature";

    /// <summary>Gets or sets a value indicating whether the pairwise judge runs to assign chosen/rejected from the two candidates. Default: <see langword="true"/>.</summary>
    public bool JudgeEnabled { get; set; } = true;

    /// <summary>Gets or sets the model used for the pairwise judge call. When <see langword="null"/>, uses <see cref="Model"/>.</summary>
    public string? JudgeModel { get; set; }

    /// <summary>Gets or sets the rubric identifier passed to the judge. Default: <c>code_quality</c>.</summary>
    public string JudgeRubric { get; set; } = "code_quality";

    /// <summary>Gets or sets the minimum judge score the chosen response must reach. Rows where the winner scores below this are dropped. Default: 3.0.</summary>
    public float MinChosenScore { get; set; } = 3.0f;

    /// <summary>Gets or sets the number of parallel worker tasks. Default: 4.</summary>
    public int Workers { get; set; } = 4;

    /// <summary>Gets or sets a value indicating whether rows where an LLM call or judge call fails are dropped. Default: <see langword="true"/>.</summary>
    public bool DropOnError { get; set; } = true;
}
