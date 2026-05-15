using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;
using DistSharp.Core.Prompts;
using Microsoft.Extensions.Logging;

namespace DistSharp.Core.Steps;

/// <summary>Calls an <see cref="ILlmProvider"/> for each input row using N parallel workers, then attaches the response to the row.</summary>
public sealed class LlmStep : IStep
{
    private readonly ILlmProvider provider;
    private readonly LlmStepOptions options;
    private readonly ILogger<LlmStep> logger;

    /// <summary>Initializes a new instance of the <see cref="LlmStep"/> class.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="provider">The LLM provider to call.</param>
    /// <param name="options">Step configuration.</param>
    /// <param name="logger">Logger.</param>
    public LlmStep(string name, ILlmProvider provider, LlmStepOptions options, ILogger<LlmStep> logger)
    {
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
        var workerCount = Math.Max(1, this.options.Workers);
        var tasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            tasks[i] = this.WorkerLoopAsync(input, output, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static IReadOnlyList<ChatMessage> ReplaceSystemMessage(IReadOnlyList<ChatMessage> messages, string newSystem)
    {
        var result = new List<ChatMessage>(messages.Count + 1) { new(ChatRole.System, newSystem) };
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System)
            {
                continue;
            }

            result.Add(m);
        }

        return result;
    }

    private static Row MergeFields(Row row, IReadOnlyDictionary<string, object?> fields)
    {
        return fields.Count == 0 ? row : row.With(fields);
    }

    private async Task WorkerLoopAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        await foreach (var row in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var symbol = row.Get<ExtractedSymbol>("symbol");
            if (symbol is null)
            {
                this.logger.LogWarning("LlmStep '{Name}' received a row with no 'symbol' field; dropping.", this.Name);
                continue;
            }

            var prompt = PromptBuilderRegistry.Build(this.options.DatasetType, symbol);
            var messages = this.options.SystemPromptOverride is null
                ? prompt.Messages
                : ReplaceSystemMessage(prompt.Messages, this.options.SystemPromptOverride);

            var requestOptions = new LlmRequestOptions
            {
                Model = this.options.Model,
                Temperature = this.options.Temperature,
                MaxTokens = this.options.MaxTokens,
            };

            try
            {
                var raw = await this.provider.CompleteAsync(messages, requestOptions, cancellationToken).ConfigureAwait(false);
                var parsed = prompt.ParseResponse(raw);
                if (parsed is null)
                {
                    this.logger.LogDebug("LlmStep '{Name}': response did not parse for {Symbol}; dropping row.", this.Name, symbol.FullyQualifiedName);
                    if (this.options.DropOnError)
                    {
                        continue;
                    }

                    var errorRow = MergeFields(row, prompt.PreparedFields).With("error", "response did not parse").With("response_raw", raw);
                    await output.WriteAsync(errorRow, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var produced = MergeFields(row, prompt.PreparedFields);
                produced = MergeFields(produced, parsed);
                await output.WriteAsync(produced, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "LlmStep '{Name}': provider call failed for {Symbol}.", this.Name, symbol.FullyQualifiedName);
                if (this.options.DropOnError)
                {
                    continue;
                }

                var errorRow = MergeFields(row, prompt.PreparedFields).With("error", ex.Message);
                await output.WriteAsync(errorRow, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
