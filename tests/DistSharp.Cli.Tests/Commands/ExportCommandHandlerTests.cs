using DistSharp.Cli.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using Xunit;

namespace DistSharp.Cli.Tests.Commands;

public sealed class ExportCommandHandlerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ExportCommandHandlerTests()
    {
        Directory.CreateDirectory(this.dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportsToAlpacaFormat_LocalOnly()
    {
        var inputFile = Path.Combine(this.dir, "dataset.jsonl");
        await File.WriteAllTextAsync(inputFile, @"{""instruction"":""Q1"",""response"":""A1""}");

        var handler = new ExportCommandHandler(new TestConsole(), new StubHttpClientFactory(), NullLogger<ExportCommandHandler>.Instance);
        var outDir = Path.Combine(this.dir, "out");
        var options = new ExportCommandOptions { DatasetDir = this.dir, Format = "alpaca", OutDir = outDir };

        var exitCode = await handler.InvokeAsync(options, CancellationToken.None);

        exitCode.Should().Be(0);
        var produced = Directory.GetFiles(outDir, "*.jsonl");
        produced.Should().ContainSingle();
        var content = await File.ReadAllTextAsync(produced[0]);
        content.Should().Contain("\"instruction\":\"Q1\"");
        content.Should().Contain("\"output\":\"A1\"");
    }

    [Fact]
    public async Task ReturnsError_WhenDatasetDirMissing()
    {
        var handler = new ExportCommandHandler(new TestConsole(), new StubHttpClientFactory(), NullLogger<ExportCommandHandler>.Instance);
        var options = new ExportCommandOptions { DatasetDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) };

        var exitCode = await handler.InvokeAsync(options, CancellationToken.None);

        exitCode.Should().Be(2);
    }

    [Fact]
    public async Task ReturnsError_WhenUploadRequestedWithoutToken()
    {
        var inputFile = Path.Combine(this.dir, "dataset.jsonl");
        await File.WriteAllTextAsync(inputFile, @"{""instruction"":""Q"",""response"":""A""}");

        var savedToken = Environment.GetEnvironmentVariable("HF_TOKEN");
        Environment.SetEnvironmentVariable("HF_TOKEN", null);
        try
        {
            var handler = new ExportCommandHandler(new TestConsole(), new StubHttpClientFactory(), NullLogger<ExportCommandHandler>.Instance);
            var options = new ExportCommandOptions
            {
                DatasetDir = this.dir,
                Format = "jsonl",
                OutDir = Path.Combine(this.dir, "out"),
                HfRepo = "myorg/myrepo",
                HfToken = null,
            };

            var exitCode = await handler.InvokeAsync(options, CancellationToken.None);

            exitCode.Should().Be(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HF_TOKEN", savedToken);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
