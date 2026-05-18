namespace DistSharp.Core.Prompts;

/// <summary>
/// Documents the output row schema produced by <c>PreferenceStep</c>.
/// All fields are written to the row dictionary by that step; this record exists as an
/// authoritative reference for consumers and as the basis for export converters.
/// </summary>
/// <param name="Prompt">The instruction sent to the model.</param>
/// <param name="Chosen">The preferred (higher-quality) response.</param>
/// <param name="Rejected">The dispreferred (lower-quality) response.</param>
/// <param name="Margin">
/// Score gap between chosen and rejected on the judge's 1–4 margin scale.
/// Zero when <c>JudgeEnabled</c> is false.
/// </param>
/// <param name="ScoreChosen">
/// Approximate chosen score on the 1–5 scale, derived from judge margin.
/// Zero when <c>JudgeEnabled</c> is false.
/// </param>
/// <param name="ScoreRejected">
/// Approximate rejected score on the 1–5 scale, derived from judge margin.
/// Zero when <c>JudgeEnabled</c> is false.
/// </param>
/// <param name="RejectionStrategy">The strategy used to produce the rejected response, e.g. <c>higher_temperature</c>.</param>
/// <param name="DatasetType">The originating dataset type, e.g. <c>explanation</c>.</param>
/// <param name="SymbolFqn">The fully-qualified symbol name, for deduplication and traceability.</param>
public sealed record PreferenceRow(
    string Prompt,
    string Chosen,
    string Rejected,
    float Margin,
    float ScoreChosen,
    float ScoreRejected,
    string RejectionStrategy,
    string DatasetType,
    string SymbolFqn);
