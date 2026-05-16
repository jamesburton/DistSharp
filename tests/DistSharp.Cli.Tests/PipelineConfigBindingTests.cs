using DistSharp.Cli.Configuration;
using DistSharp.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DistSharp.Cli.Tests;

public sealed class PipelineConfigBindingTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".yaml");

    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }

    [Fact]
    public void StepConfig_Dictionary_IsPopulated_FromYaml()
    {
        var yaml = "name: test\n"
            + "steps:\n"
            + "  - name: extract\n"
            + "    type: RoslynSymbolExtractor\n"
            + "    depends_on: []\n"
            + "    config:\n"
            + "      include_tests: true\n"
            + "      min_complexity: 5\n"
            + "  - name: sample\n"
            + "    type: StratifiedSampler\n"
            + "    depends_on: [extract]\n"
            + "    config:\n"
            + "      max_rows: 100\n"
            + "      strategy: uniform\n";
        File.WriteAllText(this.path, yaml);

        var configuration = new ConfigurationBuilder().AddYamlFile(this.path, optional: false).Build();
        var pipelineConfig = new PipelineConfig();
        configuration.Bind(pipelineConfig);

        // Mimic the PipelineRunCommandHandler post-bind step:
        var stepsSection = configuration.GetSection("steps");
        var i = 0;
        foreach (var stepSection in stepsSection.GetChildren())
        {
            if (i >= pipelineConfig.Steps.Count)
            {
                break;
            }

            var configSection = stepSection.GetSection("config");
            if (configSection.Exists())
            {
                pipelineConfig.Steps[i].Config = Flatten(configSection);
            }

            i++;
        }

        pipelineConfig.Steps.Should().HaveCount(2);
        pipelineConfig.Steps[0].Config["include_tests"].Should().Be(true);
        pipelineConfig.Steps[0].Config["min_complexity"].Should().Be(5);
        pipelineConfig.Steps[1].Config["max_rows"].Should().Be(100);
        pipelineConfig.Steps[1].Config["strategy"].Should().Be("uniform");
    }

    private static Dictionary<string, object?> Flatten(IConfigurationSection section)
    {
        var result = new Dictionary<string, object?>();
        foreach (var child in section.GetChildren())
        {
            if (child.Value is null)
            {
                result[child.Key] = Flatten(child);
                continue;
            }

            if (bool.TryParse(child.Value, out var b))
            {
                result[child.Key] = b;
            }
            else if (int.TryParse(child.Value, out var iVal))
            {
                result[child.Key] = iVal;
            }
            else
            {
                result[child.Key] = child.Value;
            }
        }

        return result;
    }
}
