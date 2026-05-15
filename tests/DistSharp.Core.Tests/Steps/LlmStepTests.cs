using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Steps;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DistSharp.Core.Tests.Steps;

public sealed class LlmStepTests
{
    [Fact]
    public async Task AttachesResponseField_OnSuccessfulCompletion()
    {
        var provider = new StubProvider(_ => "explanation text");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        await input.Writer.WriteAsync(Row.Empty.With("symbol", Sym("A")));
        input.Writer.Complete();

        var step = new LlmStep("gen", provider, new LlmStepOptions { Workers = 1, DatasetType = "explanation" }, NullLogger<LlmStep>.Instance);
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Get<string>("response").Should().Be("explanation text");
        rows[0].Get<string>("instruction").Should().Contain("Explain");
    }

    [Fact]
    public async Task DropsRow_WhenResponseFailsToParse()
    {
        var provider = new StubProvider(_ => "not json");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        await input.Writer.WriteAsync(Row.Empty.With("symbol", Sym("A")));
        input.Writer.Complete();

        var step = new LlmStep("gen", provider, new LlmStepOptions { Workers = 1, DatasetType = "bug-fix", DropOnError = true }, NullLogger<LlmStep>.Instance);
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PassesThroughErrorRow_WhenDropOnErrorFalse()
    {
        var provider = new StubProvider(_ => "not json");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        await input.Writer.WriteAsync(Row.Empty.With("symbol", Sym("A")));
        input.Writer.Complete();

        var step = new LlmStep("gen", provider, new LlmStepOptions { Workers = 1, DatasetType = "bug-fix", DropOnError = false }, NullLogger<LlmStep>.Instance);
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Get<string>("error").Should().NotBeNull();
    }

    [Fact]
    public async Task RunsMultipleWorkersInParallel()
    {
        var provider = new StubProvider(_ => "ok");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        for (var i = 0; i < 20; i++)
        {
            await input.Writer.WriteAsync(Row.Empty.With("symbol", Sym($"S{i}")));
        }

        input.Writer.Complete();

        var step = new LlmStep("gen", provider, new LlmStepOptions { Workers = 4, DatasetType = "explanation" }, NullLogger<LlmStep>.Instance);
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        provider.CallCount.Should().Be(20);
        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task AppliesSystemPromptOverride()
    {
        IReadOnlyList<ChatMessage>? captured = null;
        var provider = new StubProvider(messages =>
        {
            captured = messages;
            return "ok";
        });
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        await input.Writer.WriteAsync(Row.Empty.With("symbol", Sym("A")));
        input.Writer.Complete();

        var step = new LlmStep(
            "gen",
            provider,
            new LlmStepOptions { Workers = 1, DatasetType = "explanation", SystemPromptOverride = "be brief" },
            NullLogger<LlmStep>.Instance);
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);

        captured.Should().NotBeNull();
        captured![0].Role.Should().Be(ChatRole.System);
        captured[0].Content.Should().Be("be brief");
    }

    private static ExtractedSymbol Sym(string name) => new()
    {
        FullyQualifiedName = name,
        SignatureText = "void M()",
        BodyText = "// body",
        ContainingType = "C",
        Namespace = "N",
        FilePath = "x.cs",
        Complexity = 1,
        Kind = "method",
    };

    private sealed class StubProvider : ILlmProvider
    {
        private readonly Func<IReadOnlyList<ChatMessage>, string> respond;
        private int callCount;

        public StubProvider(Func<IReadOnlyList<ChatMessage>, string> respond) => this.respond = respond;

        public string ProviderName => "stub";

        public int CallCount => this.callCount;

        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref this.callCount);
            return Task.FromResult(this.respond(messages));
        }
    }
}
