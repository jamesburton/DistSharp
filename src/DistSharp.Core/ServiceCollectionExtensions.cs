using DistSharp.Core.Abstractions;
using DistSharp.Core.Pipeline;
using DistSharp.Core.Writers;
using Microsoft.Extensions.DependencyInjection;

namespace DistSharp.Core;

/// <summary>Extension methods to register DistSharp Core services with an <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the dataset-writer factory and pipeline executor.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddDistSharpCore(this IServiceCollection services)
    {
        services.AddSingleton<IDatasetWriterFactory, DatasetWriterFactory>();
        services.AddSingleton<IPipelineExecutor, PipelineExecutor>();
        return services;
    }
}
