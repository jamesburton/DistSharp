using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>init</c> command.</summary>
public sealed class InitCommandHandler
{
    private readonly IAnsiConsole console;

    /// <summary>Initializes a new instance of the <see cref="InitCommandHandler"/> class.</summary>
    /// <param name="console">The Spectre console for output.</param>
    public InitCommandHandler(IAnsiConsole console)
    {
        this.console = console;
    }

    /// <summary>Runs the init command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code: 0 on success, 2 on unknown template.</returns>
    public async Task<int> InvokeAsync(InitCommandOptions options, CancellationToken cancellationToken)
    {
        var content = Templates.Get(options.Template, options.PipelineName);
        if (content is null)
        {
            this.console.MarkupLine($"[red]Unknown template: {options.Template}. Available: dotnet-mixed, dotnet-explanation, dotnet-unit-test.[/]");
            return 2;
        }

        await File.WriteAllTextAsync(options.OutputPath, content, cancellationToken).ConfigureAwait(false);
        this.console.MarkupLine($"[green]Wrote {options.OutputPath}[/]");
        return 0;
    }
}
