using System.Text.Json;
using DistSharp.Core.Export;
using DistSharp.Core.HuggingFace;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>export</c> command — converts a DistSharp JSONL dataset to another format and/or uploads to Hugging Face Hub.</summary>
public sealed class ExportCommandHandler
{
    private readonly IAnsiConsole console;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<ExportCommandHandler> logger;

    /// <summary>Initializes a new instance of the <see cref="ExportCommandHandler"/> class.</summary>
    /// <param name="console">The Spectre console for output.</param>
    /// <param name="httpClientFactory">Factory for creating <see cref="HttpClient"/> instances.</param>
    /// <param name="logger">Logger for error reporting.</param>
    public ExportCommandHandler(IAnsiConsole console, IHttpClientFactory httpClientFactory, ILogger<ExportCommandHandler> logger)
    {
        this.console = console;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    /// <summary>Runs the export command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code: 0 on success, 1 on upload failure, 2 on bad arguments.</returns>
    public async Task<int> InvokeAsync(ExportCommandOptions options, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.DatasetDir))
        {
            this.console.MarkupLine($"[red]Dataset directory not found: {options.DatasetDir}[/]");
            return 2;
        }

        var jsonlFiles = Directory.GetFiles(options.DatasetDir, "*.jsonl", SearchOption.TopDirectoryOnly);
        if (jsonlFiles.Length == 0)
        {
            this.console.MarkupLine($"[red]No .jsonl files found in {options.DatasetDir}[/]");
            return 2;
        }

        var format = (options.Format ?? "jsonl").ToLowerInvariant();
        var outDir = options.OutDir ?? Path.Combine(options.DatasetDir, $"export-{format}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(outDir);

        Func<IReadOnlyDictionary<string, JsonElement>, Dictionary<string, object?>>? converter = format switch
        {
            "alpaca" => AlpacaConverter.Convert,
            "sharegpt" => ShareGptConverter.Convert,
            "jsonl" => row => row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            _ => null,
        };

        if (converter is null)
        {
            this.console.MarkupLine($"[red]Unsupported export format: {options.Format}. Supported: jsonl, alpaca, sharegpt.[/]");
            return 2;
        }

        var outputs = new List<string>();
        var reader = new JsonlDatasetReader(this.logger);

        foreach (var file in jsonlFiles)
        {
            var outFile = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "." + format + ".jsonl");
            await using (var writer = new StreamWriter(outFile))
            {
                await foreach (var row in reader.ReadAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    var converted = converter(row);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(converted).AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }

            outputs.Add(outFile);
            this.console.MarkupLine($"[grey]  wrote[/] [cyan]{outFile}[/]");
        }

        this.console.MarkupLine($"[green]Wrote {outputs.Count} file(s) to {outDir}[/]");

        if (string.IsNullOrEmpty(options.HfRepo))
        {
            return 0;
        }

        var token = options.HfToken ?? Environment.GetEnvironmentVariable("HF_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            this.console.MarkupLine("[red]HF token required. Pass --hf-token or set HF_TOKEN.[/]");
            return 2;
        }

        var http = this.httpClientFactory.CreateClient("HuggingFace");
        var client = new HuggingFaceClient(http, token);

        try
        {
            await client.EnsureRepoAsync(options.HfRepo, cancellationToken).ConfigureAwait(false);
            foreach (var file in outputs)
            {
                var remote = $"{options.Split}/{Path.GetFileName(file)}";
                this.console.MarkupLine($"  uploading [cyan]{remote}[/]");
                await client.UploadFileAsync(options.HfRepo, remote, file, "DistSharp export", cancellationToken).ConfigureAwait(false);
            }

            this.console.MarkupLine($"[green]Uploaded to https://huggingface.co/datasets/{options.HfRepo}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Hugging Face upload failed");
            this.console.MarkupLine($"[red]Upload failed: {ex.Message}[/]");
            return 1;
        }
    }
}
