using DistSharp.Cli.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DistSharp.Cli.Tests.Configuration;

public sealed class YamlConfigurationProviderTests : IDisposable
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
    public void Load_FlattensNestedKeys()
    {
        File.WriteAllText(this.path, "solution:\n  path: ./MyApp.sln\n  include_tests: false\n");
        var config = new ConfigurationBuilder().AddYamlFile(this.path, optional: false).Build();

        config["solution:path"].Should().Be("./MyApp.sln");
        config["solution:include_tests"].Should().Be("false");
    }

    [Fact]
    public void Load_FlattensListsWithIndexes()
    {
        File.WriteAllText(this.path, "steps:\n  - name: a\n    type: T1\n  - name: b\n    type: T2\n");
        var config = new ConfigurationBuilder().AddYamlFile(this.path, optional: false).Build();

        config["steps:0:name"].Should().Be("a");
        config["steps:1:name"].Should().Be("b");
        config["steps:1:type"].Should().Be("T2");
    }

    [Fact]
    public void Load_OptionalFile_DoesNotThrow_WhenMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".yaml");

        var act = () => new ConfigurationBuilder().AddYamlFile(missing, optional: true).Build();

        act.Should().NotThrow();
    }

    [Fact]
    public void Load_RequiredFile_Throws_WhenMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".yaml");

        var act = () => new ConfigurationBuilder().AddYamlFile(missing, optional: false).Build();

        act.Should().Throw<FileNotFoundException>();
    }
}
