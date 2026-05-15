using DistSharp.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DistSharp.Roslyn;

/// <summary>Extension methods to register DistSharp Roslyn services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ISolutionAnalyzer"/> backed by <see cref="RoslynSolutionAnalyzer"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddDistSharpRoslyn(this IServiceCollection services)
    {
        services.AddSingleton<ISolutionAnalyzer, RoslynSolutionAnalyzer>();
        return services;
    }
}
