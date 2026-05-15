using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Analyses a .NET solution and yields extracted symbols. Implemented in <c>DistSharp.Roslyn</c>.</summary>
public interface ISolutionAnalyzer
{
    /// <summary>Analyses the solution at <paramref name="solutionPath"/> and streams the extracted symbols.</summary>
    /// <param name="solutionPath">Path to a <c>.sln</c>, <c>.csproj</c>, or directory.</param>
    /// <param name="options">Analysis options such as namespace exclusions and complexity threshold.</param>
    /// <param name="cancellationToken">Token to cancel the analysis.</param>
    /// <returns>An async stream of <see cref="ExtractedSymbol"/> records.</returns>
    IAsyncEnumerable<ExtractedSymbol> AnalyzeAsync(
        string solutionPath,
        SolutionAnalysisOptions options,
        CancellationToken cancellationToken);
}
