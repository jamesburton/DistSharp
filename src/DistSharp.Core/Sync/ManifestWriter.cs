using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DistSharp.Core.Sync;

/// <summary>
/// Wraps an <see cref="IDatasetWriter"/> and, after all rows flow through, writes
/// <c>_distsharp/manifest.json</c> to the dataset directory.
/// Rows must carry a <c>symbol</c> field of type <see cref="ExtractedSymbol"/> and
/// a <c>dataset_type</c> field (set by the prompt builder). Rows missing either field
/// are forwarded to the inner writer but are not indexed in the manifest.
/// </summary>
public sealed class ManifestWriter : IDatasetWriter
{
    private readonly IDatasetWriter inner;
    private readonly string manifestPath;
    private readonly string solutionPath;
    private readonly string datasetType;
    private readonly ILogger<ManifestWriter> logger;
    private readonly List<ManifestRow> rowIndex = new();
    private int rowOffset;

    /// <summary>Initializes a new instance of the <see cref="ManifestWriter"/> class.</summary>
    /// <param name="inner">The underlying writer that receives each row.</param>
    /// <param name="datasetDir">Root dataset directory; manifest is written to <c>{datasetDir}/_distsharp/manifest.json</c>.</param>
    /// <param name="solutionPath">Path to the solution being analysed; used to populate manifest source metadata.</param>
    /// <param name="datasetType">The dataset type used by the pipeline (e.g. <c>explanation</c>).</param>
    /// <param name="logger">Logger for diagnostic messages.</param>
    public ManifestWriter(
        IDatasetWriter inner,
        string datasetDir,
        string solutionPath,
        string datasetType,
        ILogger<ManifestWriter> logger)
    {
        this.inner = inner;
        this.manifestPath = Path.Combine(datasetDir, "_distsharp", "manifest.json");
        this.solutionPath = solutionPath;
        this.datasetType = datasetType;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(Row row, CancellationToken cancellationToken)
    {
        await this.inner.WriteAsync(row, cancellationToken).ConfigureAwait(false);

        var symbol = row.Get<ExtractedSymbol>("symbol");
        if (symbol is null)
        {
            this.logger.LogDebug("ManifestWriter: row at offset {Offset} has no 'symbol' field; skipping manifest entry.", this.rowOffset);
            this.rowOffset++;
            return;
        }

        var promptVersion = RowIdentity.DefaultPromptVersion(this.datasetType);
        var rowId = RowIdentity.ComputeRowId(symbol.FullyQualifiedName, this.datasetType, promptVersion);
        var bodySha = RowIdentity.ComputeBodySha(symbol.BodyText);

        this.rowIndex.Add(new ManifestRow
        {
            Id = rowId,
            SymbolFqn = symbol.FullyQualifiedName,
            SymbolKind = symbol.Kind,
            DatasetType = this.datasetType,
            BodySha = bodySha,
            PromptVersion = promptVersion,
            RowOffset = this.rowOffset,
        });

        this.rowOffset++;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken cancellationToken) =>
        this.inner.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.inner.DisposeAsync().ConfigureAwait(false);
        await this.WriteManifestAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteManifestAsync(CancellationToken cancellationToken)
    {
        var headSha = GitHelper.TryGetHeadSha(this.solutionPath);
        var branch = GitHelper.TryGetBranch(this.solutionPath);

        string? commitRef = null;
        if (headSha is not null)
        {
            commitRef = branch is not null
                ? $"{branch}@{headSha[..Math.Min(7, headSha.Length)]}"
                : headSha[..Math.Min(7, headSha.Length)];
        }

        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Source = new ManifestSource
            {
                SolutionPath = this.solutionPath,
                SolutionSha = headSha,
                Commit = commitRef,
            },
            Rows = this.rowIndex,
        };

        try
        {
            await manifest.SaveAsync(this.manifestPath, cancellationToken).ConfigureAwait(false);
            this.logger.LogInformation("ManifestWriter: wrote manifest with {Count} rows to {Path}", this.rowIndex.Count, this.manifestPath);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "ManifestWriter: failed to write manifest to {Path}", this.manifestPath);
        }
    }
}
