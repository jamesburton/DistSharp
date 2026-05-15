using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;

namespace DistSharp.Roslyn.Steps;

/// <summary>Source step that runs an <see cref="ISolutionAnalyzer"/> and emits a <see cref="Row"/> per symbol.</summary>
public sealed class RoslynSymbolExtractorStep : IStep
{
    private readonly ISolutionAnalyzer analyzer;
    private readonly RoslynSymbolExtractorOptions options;
    private readonly HashSet<string> symbolKindFilter;

    /// <summary>Initializes a new instance of the <see cref="RoslynSymbolExtractorStep"/> class.</summary>
    /// <param name="name">The step name in the pipeline.</param>
    /// <param name="analyzer">The solution analyzer to delegate to.</param>
    /// <param name="options">Step configuration.</param>
    public RoslynSymbolExtractorStep(string name, ISolutionAnalyzer analyzer, RoslynSymbolExtractorOptions options)
    {
        this.Name = name;
        this.analyzer = analyzer;
        this.options = options;
        this.symbolKindFilter = new HashSet<string>(options.SymbolKinds, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        var analysisOptions = new SolutionAnalysisOptions
        {
            IncludeTests = this.options.IncludeTests,
            IncludeGenerated = this.options.IncludeGenerated,
            MinComplexity = this.options.MinComplexity,
            ExcludeNamespaces = this.options.ExcludeNamespaces,
        };

        await foreach (var raw in this.analyzer.AnalyzeAsync(this.options.SolutionPath, analysisOptions, cancellationToken).ConfigureAwait(false))
        {
            if (!this.symbolKindFilter.Contains(raw.Kind))
            {
                continue;
            }

            var symbol = this.MaybeTruncate(raw);

            var row = Row.Empty
                .With("symbol", symbol)
                .With("kind", symbol.Kind)
                .With("name", symbol.FullyQualifiedName)
                .With("namespace", symbol.Namespace)
                .With("file_path", symbol.FilePath)
                .With("complexity", symbol.Complexity);

            await output.WriteAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }

    private ExtractedSymbol MaybeTruncate(ExtractedSymbol raw)
    {
        if (this.options.MaxBodyLines <= 0 || string.IsNullOrEmpty(raw.BodyText))
        {
            return raw;
        }

        var lines = raw.BodyText.Split('\n');
        if (lines.Length <= this.options.MaxBodyLines)
        {
            return raw;
        }

        var truncated = string.Join('\n', lines.Take(this.options.MaxBodyLines)) + "\n// ... (truncated)";
        return raw with { BodyText = truncated };
    }
}
