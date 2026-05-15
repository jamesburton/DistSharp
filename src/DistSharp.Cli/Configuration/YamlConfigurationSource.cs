using Microsoft.Extensions.Configuration;

namespace DistSharp.Cli.Configuration;

/// <summary>Adds a <c>distsharp.yaml</c> configuration source to an <see cref="IConfigurationBuilder"/>.</summary>
public sealed class YamlConfigurationSource : IConfigurationSource
{
    /// <summary>Gets or sets the path to the YAML file.</summary>
    public string Path { get; set; } = "distsharp.yaml";

    /// <summary>Gets or sets a value indicating whether a missing file is silently ignored. Default: <see langword="true"/>.</summary>
    public bool Optional { get; set; } = true;

    /// <inheritdoc/>
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new YamlConfigurationProvider(this);
}
