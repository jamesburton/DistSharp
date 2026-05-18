using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;
using DistSharp.Core.Prompts;
using Microsoft.Extensions.Logging;

namespace DistSharp.Core.Steps;

/// <summary>
/// Generates preference pair rows by making two LLM calls (chosen at lower temperature, rejected at higher
/// temperature) then a third pairwise judge call to assign winner/loser. Each output row carries
/// <c>prompt</c>, <c>chosen</c>, <c>rejected</c>, <c>margin</c>, <c>score_chosen</c>, and <c>score_rejected</c>.
/// </summary>
public sealed class PreferenceStep : IStep
{
    private readonly ILlmProvider provider;
    private readonly PreferenceStepOptions options;
    private readonly ILogger<PreferenceStep> logger;

    /// <summary>Initializes a new instance of the <see cref="PreferenceStep"/> class.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="provider">The LLM provider used for all generation and judge calls.</param>
    /// <param name="options">Step configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown when <see cref="PreferenceStepOptions.RejectionStrategy"/> is not <c>higher_temperature</c>.
    /// </exception>
    public PreferenceStep(string name, ILlmProvider provider, PreferenceStepOptions options, ILogger<PreferenceStep> logger)
    {
        if (!string.Equals(options.RejectionStrategy, "higher_temperature", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"RejectionStrategy '{options.RejectionStrategy}' is not supported in Phase 1. Only 'higher_temperature' is implemented.");
        }

        this.Name = name;
        this.provider = provider;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        /* PreferenceStep makes 3 LLM calls per row (chosen + rejected + judge).
           Log a startup warning so callers are aware of the cost multiplier. */
        this.logger.LogWarning(
            "PreferenceStep '{Name}' makes 3 LLM calls per row. Verify your row count before running at scale.",
            this.Name);

        var workerCount = Math.Max(1, this.options.Workers);
        var tasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            tasks[i] = this.WorkerLoopAsync(input, output, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string BuildPairwiseJudgeSystemPrompt(string rubric) => rubric.ToLowerInvariant() switch
    {
        "code_quality" =>
            "You are a senior .NET reviewer. Given the instruction and two candidate responses, decide which is better. " +
            "Output JSON: {\"winner\": 1, \"margin\": 2, \"reason\": \"...\"}. " +
            "winner is 1 or 2 (index of the better response). margin is 1 (slight) to 4 (decisive). Do not wrap the JSON in markdown.",
        _ =>
            "You are a senior reviewer. Given the instruction and two candidate responses, decide which is better on helpfulness and correctness. " +
            "Output JSON: {\"winner\": 1, \"margin\": 2, \"reason\": \"...\"}. " +
            "winner is 1 or 2 (index of the better response). margin is 1 (slight) to 4 (decisive). Do not wrap the JSON in markdown.",
    };

    private static string BuildPairwiseJudgeUserPrompt(string instruction, string responseA, string responseB)
    {
        return
            $"Instruction:\n{instruction}\n\n" +
            $"Response 1:\n{responseA}\n\n" +
            $"Response 2:\n{responseB}";
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline > 0)
        {
            trimmed = trimmed[(firstNewline + 1)..];
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private static bool TryParsePairwiseResult(
        string raw,
        out int winner,
        out float margin,
        out string reason)
    {
        winner = 0;
        margin = 0f;
        reason = string.Empty;

        try
        {
            var trimmed = StripCodeFences(raw);
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("winner", out var winnerEl))
            {
                return false;
            }

            if (winnerEl.ValueKind == JsonValueKind.Number)
            {
                winner = winnerEl.GetInt32();
            }
            else if (winnerEl.ValueKind == JsonValueKind.String &&
                     int.TryParse(winnerEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWinner))
            {
                winner = parsedWinner;
            }
            else
            {
                return false;
            }

            if (winner != 1 && winner != 2)
            {
                return false;
            }

            if (root.TryGetProperty("margin", out var marginEl))
            {
                if (marginEl.ValueKind == JsonValueKind.Number)
                {
                    margin = marginEl.GetSingle();
                }
                else if (marginEl.ValueKind == JsonValueKind.String &&
                         float.TryParse(marginEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMargin))
                {
                    margin = parsedMargin;
                }
            }

            if (root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
            {
                reason = reasonEl.GetString() ?? string.Empty;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task WorkerLoopAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        await foreach (var row in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var symbol = row.Get<ExtractedSymbol>("symbol");
            if (symbol is null)
            {
                this.logger.LogWarning("PreferenceStep '{Name}' received a row with no 'symbol' field; dropping.", this.Name);
                continue;
            }

            try
            {
                var produced = await this.ProcessRowAsync(row, symbol, cancellationToken).ConfigureAwait(false);
                if (produced is not null)
                {
                    await output.WriteAsync(produced, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "PreferenceStep '{Name}': failed for {Symbol}; dropping row.", this.Name, symbol.FullyQualifiedName);
                if (!this.options.DropOnError)
                {
                    await output.WriteAsync(row.With("error", ex.Message), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<Row?> ProcessRowAsync(Row row, ExtractedSymbol symbol, CancellationToken cancellationToken)
    {
        var prompt = PromptBuilderRegistry.Build(this.options.DatasetType, symbol);

        /* Call 1: generate the chosen candidate at lower temperature. */
        var chosenOptions = new LlmRequestOptions
        {
            Model = this.options.Model,
            Temperature = this.options.ChosenTemperature,
        };

        var rawChosen = await this.provider
            .CompleteAsync(prompt.Messages, chosenOptions, cancellationToken)
            .ConfigureAwait(false);

        /* Call 2: generate the rejected candidate at higher temperature. */
        var rejectedOptions = new LlmRequestOptions
        {
            Model = this.options.Model,
            Temperature = this.options.RejectedTemperature,
        };

        var rawRejected = await this.provider
            .CompleteAsync(prompt.Messages, rejectedOptions, cancellationToken)
            .ConfigureAwait(false);

        /* Extract the instruction field from PreparedFields for use in the judge prompt. */
        var instruction = prompt.PreparedFields.TryGetValue("instruction", out var instrObj)
            ? instrObj as string ?? string.Empty
            : string.Empty;

        string chosen;
        string rejected;
        float scoreChosen;
        float scoreRejected;
        float margin;

        if (this.options.JudgeEnabled)
        {
            /* Call 3: pairwise judge to assign winner/loser and extract margin. */
            var judgeResult = await this.CallPairwiseJudgeAsync(
                instruction, rawChosen, rawRejected, cancellationToken)
                .ConfigureAwait(false);

            if (judgeResult is null)
            {
                this.logger.LogDebug("PreferenceStep '{Name}': judge call did not parse for {Symbol}; dropping row.", this.Name, symbol.FullyQualifiedName);
                return null;
            }

            /* Assign chosen/rejected based on judge winner. */
            if (judgeResult.Value.Winner == 1)
            {
                chosen = rawChosen;
                rejected = rawRejected;

                /* Derive score proxies: winner is ~3+margin/2, loser is winner - margin.
                   The pairwise judge doesn't emit per-response scores, so we approximate
                   from margin on the 1–5 scale used by the scoring judge. */
                scoreChosen = Math.Min(5f, 3f + (judgeResult.Value.Margin / 2f));
                scoreRejected = Math.Max(1f, scoreChosen - judgeResult.Value.Margin);
            }
            else
            {
                chosen = rawRejected;
                rejected = rawChosen;
                scoreChosen = Math.Min(5f, 3f + (judgeResult.Value.Margin / 2f));
                scoreRejected = Math.Max(1f, scoreChosen - judgeResult.Value.Margin);
            }

            margin = judgeResult.Value.Margin;

            if (scoreChosen < this.options.MinChosenScore)
            {
                this.logger.LogDebug("PreferenceStep '{Name}': chosen score {Score:F1} below MinChosenScore {Min:F1} for {Symbol}; dropping.", this.Name, scoreChosen, this.options.MinChosenScore, symbol.FullyQualifiedName);
                return null;
            }
        }
        else
        {
            /* No judge: assume lower-temperature response is chosen. */
            chosen = rawChosen;
            rejected = rawRejected;
            scoreChosen = 0f;
            scoreRejected = 0f;
            margin = 0f;
        }

        return row
            .With(prompt.PreparedFields)
            .With("prompt", instruction)
            .With("chosen", chosen)
            .With("rejected", rejected)
            .With("margin", margin)
            .With("score_chosen", scoreChosen)
            .With("score_rejected", scoreRejected)
            .With("rejection_strategy", this.options.RejectionStrategy)
            .With("dataset_type", this.options.DatasetType)
            .With("symbol_fqn", symbol.FullyQualifiedName);
    }

    private async Task<(int Winner, float Margin, string Reason)?> CallPairwiseJudgeAsync(
        string instruction,
        string responseA,
        string responseB,
        CancellationToken cancellationToken)
    {
        var systemPrompt = BuildPairwiseJudgeSystemPrompt(this.options.JudgeRubric);
        var userPrompt = BuildPairwiseJudgeUserPrompt(instruction, responseA, responseB);

        var messages = new ChatMessage[]
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var judgeRequestOptions = new LlmRequestOptions
        {
            Model = this.options.JudgeModel ?? this.options.Model,
            Temperature = 0f, /* deterministic judging */
        };

        var raw = await this.provider
            .CompleteAsync(messages, judgeRequestOptions, cancellationToken)
            .ConfigureAwait(false);

        if (!TryParsePairwiseResult(raw, out var winner, out var margin, out var reason))
        {
            this.logger.LogDebug("PreferenceStep '{Name}': pairwise judge returned unparseable JSON: '{Raw}'.", this.Name, raw);
            return null;
        }

        return (winner, margin, reason);
    }
}
