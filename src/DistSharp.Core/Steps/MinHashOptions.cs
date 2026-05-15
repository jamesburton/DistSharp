namespace DistSharp.Core.Steps;

/// <summary>Configuration for <see cref="MinHashDeduplicator"/>.</summary>
public sealed class MinHashOptions
{
    /// <summary>Gets or sets the field that holds the text used for similarity comparison. Default: <c>response</c>.</summary>
    public string Field { get; set; } = "response";

    /// <summary>Gets or sets the Jaccard similarity threshold above which rows are considered duplicates. Default: 0.85.</summary>
    public double Threshold { get; set; } = 0.85;

    /// <summary>Gets or sets the number of MinHash functions used per signature. Default: 128.</summary>
    public int NumHashes { get; set; } = 128;

    /// <summary>Gets or sets the word-shingle (n-gram) size. Default: 3.</summary>
    public int ShingleSize { get; set; } = 3;

    /// <summary>Gets or sets the random seed used to derive hash function seeds. Default: 42.</summary>
    public int Seed { get; set; } = 42;
}
