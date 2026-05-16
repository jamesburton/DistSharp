using Spectre.Console;
using Spectre.Console.Rendering;

namespace DistSharp.Cli.Progress;

/// <summary>Renders a live Spectre.Console table tracking each step's row counters and status.</summary>
public sealed class LiveProgressDisplay
{
    private readonly IAnsiConsole console;
    private readonly TimeSpan refreshInterval;

    /// <summary>Initializes a new instance of the <see cref="LiveProgressDisplay"/> class.</summary>
    /// <param name="console">The Spectre console to render into.</param>
    /// <param name="refreshInterval">How often the table refreshes. Default: 250 ms.</param>
    public LiveProgressDisplay(IAnsiConsole console, TimeSpan? refreshInterval = null)
    {
        this.console = console;
        this.refreshInterval = refreshInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Runs <paramref name="work"/> while live-rendering progress for <paramref name="metrics"/>.</summary>
    /// <param name="metrics">The step metrics to display, in pipeline order.</param>
    /// <param name="work">The pipeline-execution task.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> that completes when the work and final render are done.</returns>
    public async Task RunAsync(IReadOnlyList<StepMetrics> metrics, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        // LiveDisplay requires an interactive terminal; fall back to plain output in non-TTY contexts
        // (e.g. CI, piped output, or background processes) to avoid cursor-manipulation exceptions.
        if (!this.console.Profile.Capabilities.Interactive)
        {
            await work(cancellationToken).ConfigureAwait(false);
            this.console.Write(BuildTable(metrics));
            return;
        }

        var table = BuildTable(metrics);

        await this.console.Live(table).StartAsync(async ctx =>
        {
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var refreshTask = this.RefreshLoopAsync(ctx, metrics, refreshCts.Token);
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                refreshCts.Cancel();
                try
                {
                    await refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                ctx.UpdateTarget(BuildTable(metrics));
                ctx.Refresh();
            }
        }).ConfigureAwait(false);
    }

    private static IRenderable BuildTable(IReadOnlyList<StepMetrics> metrics)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Step")
            .AddColumn(new TableColumn("In").RightAligned())
            .AddColumn(new TableColumn("Out").RightAligned())
            .AddColumn("Status");

        foreach (var m in metrics)
        {
            var statusColour = m.Status switch
            {
                "running" => "[yellow]",
                "complete" => "[green]",
                "failed" => "[red]",
                "cancelled" => "[grey]",
                _ => "[grey]",
            };

            table.AddRow(m.Name, m.RowsIn.ToString(), m.RowsOut.ToString(), $"{statusColour}{m.Status}[/]");
        }

        return table;
    }

    private async Task RefreshLoopAsync(LiveDisplayContext ctx, IReadOnlyList<StepMetrics> metrics, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(this.refreshInterval, cancellationToken).ConfigureAwait(false);
                ctx.UpdateTarget(BuildTable(metrics));
                ctx.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
