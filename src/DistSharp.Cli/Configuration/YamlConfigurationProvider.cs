using System.IO;
using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;

namespace DistSharp.Cli.Configuration;

/// <summary>Reads a YAML file and exposes it through the .NET <see cref="IConfiguration"/> API.</summary>
internal sealed class YamlConfigurationProvider : ConfigurationProvider
{
    private readonly YamlConfigurationSource source;

    /* Inline constructor doc intentionally omitted — internal type, documentInternalElements=false. */
    internal YamlConfigurationProvider(YamlConfigurationSource source)
    {
        this.source = source;
    }

    /// <inheritdoc/>
    public override void Load()
    {
        if (!File.Exists(this.source.Path))
        {
            if (this.source.Optional)
            {
                return;
            }

            throw new FileNotFoundException($"YAML config file not found: {this.source.Path}", this.source.Path);
        }

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(this.source.Path);
        var yaml = new YamlStream();
        yaml.Load(reader);

        if (yaml.Documents.Count == 0)
        {
            this.Data = data;
            return;
        }

        if (yaml.Documents[0].RootNode is YamlMappingNode rootMap)
        {
            Flatten(rootMap, string.Empty, data);
        }

        this.Data = data;
    }

    private static void Flatten(YamlNode node, string prefix, IDictionary<string, string?> data)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                data[prefix] = scalar.Value;
                break;

            case YamlMappingNode mapping:
                foreach (var child in mapping.Children)
                {
                    var key = ((YamlScalarNode)child.Key).Value ?? string.Empty;
                    var nextPrefix = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";
                    Flatten(child.Value, nextPrefix, data);
                }

                break;

            case YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    var nextPrefix = string.IsNullOrEmpty(prefix) ? i.ToString() : $"{prefix}:{i}";
                    Flatten(sequence.Children[i], nextPrefix, data);
                }

                break;
        }
    }
}
