using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>export</c> command. Real implementation lands in Phase 7.</summary>
public sealed class ExportCommandHandler
{
    private readonly IAnsiConsole console;

    /// <summary>Initializes a new instance of the <see cref="ExportCommandHandler"/> class.</summary>
    /// <param name="console">The Spectre console for output.</param>
    public ExportCommandHandler(IAnsiConsole console)
    {
        this.console = console;
    }

    /// <summary>Runs the export command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Always returns 1 until Phase 7 implements this command.</returns>
    public Task<int> InvokeAsync(ExportCommandOptions options, CancellationToken cancellationToken)
    {
        this.console.MarkupLine("[yellow]Export is not yet implemented. This will land in Phase 7.[/]");
        return Task.FromResult(1);
    }
}
