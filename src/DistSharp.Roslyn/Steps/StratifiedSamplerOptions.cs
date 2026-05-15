namespace DistSharp.Roslyn.Steps;

/// <summary>Sampling strategy for <see cref="StratifiedSamplerStep"/>.</summary>
public enum SamplingStrategy
{
    /// <summary>Uniform random sampling — every row has equal probability.</summary>
    Uniform,

    /// <summary>Complexity-weighted sampling — rows with higher complexity are more likely to be picked.</summary>
    ComplexityWeighted,
}

/// <summary>Configuration for <see cref="StratifiedSamplerStep"/>.</summary>
public sealed class StratifiedSamplerOptions
{
    /// <summary>Gets or sets the maximum number of rows to emit.</summary>
    public int MaxRows { get; set; }

    /// <summary>Gets or sets the sampling strategy. Default: <see cref="SamplingStrategy.ComplexityWeighted"/>.</summary>
    public SamplingStrategy Strategy { get; set; } = SamplingStrategy.ComplexityWeighted;

    /// <summary>Gets or sets a seed for the random number generator. <see langword="null"/> for a non-deterministic seed.</summary>
    public int? Seed { get; set; }
}
