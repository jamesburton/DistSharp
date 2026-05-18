using System.Text.Json.Serialization;

namespace DistSharp.Core.Sync;

/// <summary>Source-solution metadata stored in the manifest.</summary>
public sealed class ManifestSource
{
    /// <summary>Gets or sets the path to the solution file used during generation.</summary>
    [JsonPropertyName("solution_path")]
    public string? SolutionPath { get; set; }

    /// <summary>Gets or sets the git SHA of the solution repository at generation time.</summary>
    [JsonPropertyName("solution_sha")]
    public string? SolutionSha { get; set; }

    /// <summary>Gets or sets the branch+commit string (e.g. <c>main@a1b2c3d</c>), if discoverable.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }
}
