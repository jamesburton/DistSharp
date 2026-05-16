using DistSharp.Cli.Commands;
using DistSharp.Cli.Configuration;
using DistSharp.Core;
using DistSharp.Core.Configuration;
using DistSharp.Providers;
using DistSharp.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace DistSharp.Cli;

/// <summary>Builds the application <see cref="IHost"/> for the DistSharp CLI.</summary>
public static class HostBuilder
{
    /// <summary>Creates and configures the host with all DistSharp services registered.</summary>
    /// <param name="args">The original command-line arguments (used for the host builder; CLI parsing happens elsewhere).</param>
    /// <returns>A configured, ready-to-use <see cref="IHost"/>.</returns>
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddYamlFile("distsharp.yaml", optional: true)
            .AddEnvironmentVariables();

        builder.Services.Configure<PipelineConfig>(builder.Configuration);

        builder.Services.AddDistSharpCore();
        builder.Services.AddDistSharpRoslyn();
        builder.Services.AddDistSharpProviders();
        builder.Services.AddHttpClient("HuggingFace");

        builder.Services.AddSingleton(AnsiConsole.Console);

        builder.Services.AddSingleton<PipelineBuilder>();
        builder.Services.AddSingleton<DistSharp.Cli.Progress.LiveProgressDisplay>();

        builder.Services.AddTransient<GenerateCommandHandler>();
        builder.Services.AddTransient<InspectCommandHandler>();
        builder.Services.AddTransient<InitCommandHandler>();
        builder.Services.AddTransient<PipelineRunCommandHandler>();
        builder.Services.AddTransient<ExportCommandHandler>();

        return builder.Build();
    }
}
