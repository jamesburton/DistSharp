using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DistSharp.Core.Export;

/// <summary>Streams rows from a JSONL file as <see cref="Dictionary{TKey, TValue}"/> of <see cref="JsonElement"/>.</summary>
public sealed class JsonlDatasetReader
{
    private readonly ILogger logger;

    /// <summary>Initializes a new instance of the <see cref="JsonlDatasetReader"/> class.</summary>
    /// <param name="logger">Logger. Pass <see cref="NullLogger.Instance"/> if not needed.</param>
    public JsonlDatasetReader(ILogger? logger = null)
    {
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Reads each line of <paramref name="filePath"/> as a JSON object.</summary>
    /// <param name="filePath">Path to the JSONL file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of rows, each row as a dictionary of field name to <see cref="JsonElement"/>.</returns>
    public async IAsyncEnumerable<Dictionary<string, JsonElement>> ReadAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(filePath);
        var lineNumber = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Dictionary<string, JsonElement>? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    this.logger.LogDebug("Skipping non-object JSON at {File}:{Line}", filePath, lineNumber);
                    continue;
                }

                parsed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    parsed[prop.Name] = prop.Value.Clone();
                }
            }
            catch (JsonException ex)
            {
                this.logger.LogDebug(ex, "Skipping malformed JSON at {File}:{Line}", filePath, lineNumber);
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }
}
