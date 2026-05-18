namespace DistSharp.Core.Sync;

/// <summary>A symbol entry with its pre-computed body SHA, used as input to the diff algorithm.</summary>
/// <param name="SymbolFqn">Fully-qualified symbol name.</param>
/// <param name="SymbolKind">Symbol kind (e.g. <c>method</c>, <c>class</c>).</param>
/// <param name="DatasetType">Dataset type for which this symbol is being tracked.</param>
/// <param name="PromptVersion">Prompt version string (e.g. <c>explanation@1</c>).</param>
/// <param name="BodySha">SHA-256 of the normalised symbol body.</param>
public sealed record SymbolWithBody(
    string SymbolFqn,
    string SymbolKind,
    string DatasetType,
    string PromptVersion,
    string BodySha);
