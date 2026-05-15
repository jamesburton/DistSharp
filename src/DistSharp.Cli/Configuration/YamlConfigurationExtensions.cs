using Microsoft.Extensions.Configuration;

namespace DistSharp.Cli.Configuration;

/// <summary>Extension methods to add YAML configuration sources.</summary>
public static class YamlConfigurationExtensions
{
    /// <summary>Adds a YAML file as a configuration source.</summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="path">Path to the YAML file. Default: <c>distsharp.yaml</c>.</param>
    /// <param name="optional">Whether a missing file is silently ignored. Default: <see langword="true"/>.</param>
    /// <returns>The configuration builder, for chaining.</returns>
    public static IConfigurationBuilder AddYamlFile(
        this IConfigurationBuilder builder,
        string path = "distsharp.yaml",
        bool optional = true)
    {
        builder.Add(new YamlConfigurationSource { Path = path, Optional = optional });
        return builder;
    }
}
