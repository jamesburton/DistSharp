using DistSharp.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>models</c> command — lists model IDs available from a configured LLM provider.</summary>
public sealed class ModelsCommandHandler
{
    private readonly ILlmProviderFactory providerFactory;
    private readonly IAnsiConsole console;
    private readonly ILogger<ModelsCommandHandler> logger;

    /// <summary>Initializes a new instance of the <see cref="ModelsCommandHandler"/> class.</summary>
    /// <param name="providerFactory">Factory used to resolve the requested provider.</param>
    /// <param name="console">Spectre console for output.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public ModelsCommandHandler(ILlmProviderFactory providerFactory, IAnsiConsole console, ILogger<ModelsCommandHandler> logger)
    {
        this.providerFactory = providerFactory;
        this.console = console;
        this.logger = logger;
    }

    /// <summary>Runs the <c>models</c> command.</summary>
    /// <param name="options">Bound command-line options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code (0 on success, 1 on failure, 2 on misconfiguration).</returns>
    public async Task<int> InvokeAsync(ModelsCommandOptions options, CancellationToken cancellationToken)
    {
        ILlmProvider provider;
        try
        {
            provider = this.providerFactory.Create(options.Provider);
        }
        catch (InvalidOperationException ex)
        {
            this.console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 2;
        }

        IReadOnlyList<string> models;
        try
        {
            models = await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Listing models from {Provider} failed", provider.ProviderName);
            this.console.MarkupLine($"[red]Failed to list models from {provider.ProviderName}: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        if (models.Count == 0)
        {
            this.console.MarkupLine($"[yellow]Provider [bold]{provider.ProviderName}[/] returned no models, or doesn't expose a discovery endpoint.[/]");
            return 0;
        }

        var filtered = string.IsNullOrEmpty(options.Filter)
            ? models
            : models.Where(m => m.Contains(options.Filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            this.console.MarkupLine($"[yellow]No models matched the filter '{options.Filter}'.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn($"[bold]{provider.ProviderName}[/] models ({filtered.Count})");

        foreach (var model in filtered)
        {
            table.AddRow(model);
        }

        this.console.Write(table);
        return 0;
    }
}
