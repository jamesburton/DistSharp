using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace DistSharp.Core.Steps;

/// <summary>
/// Uses an <see cref="ILlmProvider"/> to score each row from 1–5 against a rubric. Rows below
/// <see cref="LlmJudgeOptions.MinScore"/> are dropped. Score and reason are attached to passing rows
/// as <c>judge_score</c> and <c>judge_reason</c>.
/// </summary>
public sealed class LlmJudge : IStep
{
    private readonly ILlmProvider provider;
    private readonly LlmJudgeOptions options;
    private readonly ILogger<LlmJudge> logger;
    private readonly string systemPrompt;

    /// <summary>Initializes a new instance of the <see cref="LlmJudge"/> class.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="provider">The LLM provider used for judging.</param>
    /// <param name="options">Step configuration.</param>
    /// <param name="logger">Logger.</param>
    public LlmJudge(string name, ILlmProvider provider, LlmJudgeOptions options, ILogger<LlmJudge> logger)
    {
        this.Name = name;
        this.provider = provider;
        this.options = options;
        this.logger = logger;
        this.systemPrompt = options.PromptOverride ?? RubricToSystemPrompt(options.Rubric);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        var workerCount = Math.Max(1, this.options.Workers);
        var tasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            tasks[i] = this.WorkerAsync(input, output, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string BuildUserPrompt(string inputText, string outputText)
    {
        return
            "Score the response below from 1 to 5. Output JSON with keys `score` (integer 1–5) and `reason` (short string). Do not wrap the JSON in markdown.\n\n" +
            $"Instruction:\n{inputText}\n\nResponse:\n{outputText}";
    }

    private static string RubricToSystemPrompt(string rubric) => rubric.ToLowerInvariant() switch
    {
        "code_quality" =>
            "You are a senior .NET reviewer. Rate the response 1–5 on idiomatic C#, correctness, and style. " +
            "5 = exemplary; 4 = solid; 3 = acceptable; 2 = flawed; 1 = broken.",
        _ =>
            "You are a senior reviewer. Rate the response 1–5 on helpfulness and correctness for the given instruction. " +
            "5 = excellent; 4 = good; 3 = acceptable; 2 = poor; 1 = useless.",
    };

    private static bool TryParseScore(string raw, out float score, out string reason)
    {
        score = 0;
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

            if (!root.TryGetProperty("score", out var scoreEl))
            {
                return false;
            }

            if (scoreEl.ValueKind == JsonValueKind.Number)
            {
                score = scoreEl.GetSingle();
            }
            else if (scoreEl.ValueKind == JsonValueKind.String && float.TryParse(scoreEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                score = parsed;
            }
            else
            {
                return false;
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

    private async Task WorkerAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        await foreach (var row in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var inputText = row.Get<string>(this.options.InputField) ?? string.Empty;
            var outputText = row.Get<string>(this.options.OutputField) ?? string.Empty;

            if (string.IsNullOrEmpty(outputText))
            {
                this.logger.LogDebug("Judge '{Name}': empty {Field}; dropping row.", this.Name, this.options.OutputField);
                continue;
            }

            var userPrompt = BuildUserPrompt(inputText, outputText);
            var messages = new ChatMessage[]
            {
                new(ChatRole.System, this.systemPrompt),
                new(ChatRole.User, userPrompt),
            };

            var requestOptions = new LlmRequestOptions
            {
                Model = this.options.Model,
                Temperature = this.options.Temperature,
            };

            try
            {
                var raw = await this.provider.CompleteAsync(messages, requestOptions, cancellationToken).ConfigureAwait(false);
                if (!TryParseScore(raw, out var score, out var reason))
                {
                    this.logger.LogDebug("Judge '{Name}': could not parse score from '{Raw}'; dropping row.", this.Name, raw);
                    continue;
                }

                if (score < this.options.MinScore)
                {
                    this.logger.LogDebug("Judge '{Name}': score {Score} below MinScore {Min}; dropping.", this.Name, score, this.options.MinScore);
                    continue;
                }

                var produced = row
                    .With("judge_score", score)
                    .With("judge_reason", reason);
                await output.WriteAsync(produced, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Judge '{Name}' failed; dropping row.", this.Name);
            }
        }
    }
}
