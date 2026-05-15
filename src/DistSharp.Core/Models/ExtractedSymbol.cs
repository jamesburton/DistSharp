namespace DistSharp.Core.Models;

/// <summary>
/// A .NET symbol extracted from a solution by <c>ISolutionAnalyzer</c>.
/// Contains no Roslyn types so it can be consumed by Core steps without a Roslyn dependency.
/// </summary>
public sealed record ExtractedSymbol
{
    /// <summary>Gets the fully qualified name, e.g. <c>MyApp.Services.OrderService.PlaceOrderAsync</c>.</summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>Gets the full method or property signature text.</summary>
    public required string SignatureText { get; init; }

    /// <summary>Gets the cleaned source of the method or property body.</summary>
    public required string BodyText { get; init; }

    /// <summary>Gets the existing XML doc comment, or <see langword="null"/> if absent.</summary>
    public string? XmlDocComment { get; init; }

    /// <summary>Gets the name of the containing type.</summary>
    public required string ContainingType { get; init; }

    /// <summary>Gets the containing namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>Gets the relative source file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the cyclomatic complexity score.</summary>
    public int Complexity { get; init; }

    /// <summary>Gets the symbol kind: <c>method</c>, <c>property</c>, <c>class</c>, or <c>interface</c>.</summary>
    public required string Kind { get; init; }
}
