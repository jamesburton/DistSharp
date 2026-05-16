using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>inspect</c> command.</summary>
public sealed class InspectCommandHandler
{
    private readonly ISolutionAnalyzer analyzer;
    private readonly IAnsiConsole console;

    /// <summary>Initializes a new instance of the <see cref="InspectCommandHandler"/> class.</summary>
    /// <param name="analyzer">The solution analyzer used to extract symbols.</param>
    /// <param name="console">The Spectre console for output.</param>
    public InspectCommandHandler(ISolutionAnalyzer analyzer, IAnsiConsole console)
    {
        this.analyzer = analyzer;
        this.console = console;
    }

    /// <summary>Runs the inspect command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code: 0 on success, 2 on bad arguments.</returns>
    public async Task<int> InvokeAsync(InspectCommandOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.Solution))
        {
            this.console.MarkupLine("[red]Error: solution path is required.[/]");
            return 2;
        }

        var analysisOptions = new SolutionAnalysisOptions
        {
            IncludeTests = options.IncludeTests,
        };

        var symbols = new List<ExtractedSymbol>();
        await foreach (var symbol in this.analyzer.AnalyzeAsync(options.Solution, analysisOptions, cancellationToken).ConfigureAwait(false))
        {
            symbols.Add(symbol);
        }

        var byKind = symbols.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        var methodSymbols = symbols.Where(s => s.Kind == "method").ToList();
        var avgComplexity = methodSymbols.Count > 0 ? methodSymbols.Average(s => s.Complexity) : 0;
        var filesCount = symbols.Select(s => s.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn(new TableColumn("Value").RightAligned());

        table.AddRow("Source files", filesCount.ToString());
        foreach (var (kind, count) in byKind.OrderByDescending(p => p.Value))
        {
            table.AddRow($"  {kind}", count.ToString());
        }

        table.AddRow("Average method complexity", avgComplexity.ToString("F1"));
        this.console.Write(table);

        if (options.ShowFiles)
        {
            this.console.MarkupLine("[bold]Files:[/]");
            foreach (var file in symbols.Select(s => s.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                this.console.WriteLine($"  {file}");
            }
        }

        if (options.ShowSymbols)
        {
            this.console.MarkupLine("[bold]Symbols:[/]");
            foreach (var symbol in symbols.OrderBy(s => s.FullyQualifiedName, StringComparer.Ordinal))
            {
                this.console.WriteLine($"  [{symbol.Kind}] {symbol.FullyQualifiedName} (complexity={symbol.Complexity})");
            }
        }

        if (!string.IsNullOrEmpty(options.Report))
        {
            var json = JsonSerializer.Serialize(
                new
                {
                    solution = options.Solution,
                    files = filesCount,
                    by_kind = byKind,
                    avg_complexity = avgComplexity,
                    symbols,
                },
                new JsonSerializerOptions { WriteIndented = true });
            var reportDir = Path.GetDirectoryName(options.Report);
            if (!string.IsNullOrEmpty(reportDir))
            {
                Directory.CreateDirectory(reportDir);
            }

            await File.WriteAllTextAsync(options.Report, json, cancellationToken).ConfigureAwait(false);
            this.console.MarkupLine($"[green]Report written to {options.Report}[/]");
        }

        return 0;
    }
}
