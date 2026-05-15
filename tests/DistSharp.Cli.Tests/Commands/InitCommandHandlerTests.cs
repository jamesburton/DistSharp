using DistSharp.Cli.Commands;
using FluentAssertions;
using Spectre.Console.Testing;
using Xunit;

namespace DistSharp.Cli.Tests.Commands;

/// <summary>Tests for <see cref="InitCommandHandler"/>.</summary>
public sealed class InitCommandHandlerTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".yaml");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }

    [Theory]
    [InlineData("dotnet-mixed")]
    [InlineData("dotnet-explanation")]
    [InlineData("dotnet-unit-test")]
    [InlineData("custom")]
    public async Task InvokeAsync_WritesTemplate_ForRecognisedTemplate(string template)
    {
        var handler = new InitCommandHandler(new TestConsole());
        var options = new InitCommandOptions { Template = template, OutputPath = this.path, PipelineName = "my-pipe" };

        var exitCode = await handler.InvokeAsync(options, CancellationToken.None);

        exitCode.Should().Be(0);
        File.Exists(this.path).Should().BeTrue();
        var content = await File.ReadAllTextAsync(this.path);
        content.Should().Contain("my-pipe");
    }

    [Fact]
    public async Task InvokeAsync_ReturnsErrorCode_ForUnknownTemplate()
    {
        var handler = new InitCommandHandler(new TestConsole());
        var options = new InitCommandOptions { Template = "unknown", OutputPath = this.path };

        var exitCode = await handler.InvokeAsync(options, CancellationToken.None);

        exitCode.Should().Be(2);
        File.Exists(this.path).Should().BeFalse();
    }
}
