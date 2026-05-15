using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Steps;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DistSharp.Core.Tests.Steps;

public sealed class LlmJudgeTests
{
    [Fact]
    public async Task PassesThroughRow_WhenScoreAtOrAboveMinScore()
    {
        var provider = new ScoreProvider(() => @"{""score"":4,""reason"":""good""}");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        await input.Writer.WriteAsync(Row.Empty.With("instruction", "do x").With("response", "did x well"));
        input.Writer.Complete();

        var judge = new LlmJudge("j", provider, new LlmJudgeOptions { Workers = 1, MinScore = 3.0f }, NullLogger<LlmJudge>.Instance);
        await judge.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Get<float>("judge_score").Should().Be(4f);
        rows[0].Get<string>("judge_reason").Should().Be("good");
    }

    [Fact]
    public async Task DropsRow_WhenScoreBelowMinScore()
    {
        var provider = new ScoreProvider(() => @"{""score"":2,""reason"":""meh""}");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        await input.Writer.WriteAsync(Row.Empty.With("instruction", "do x").With("response", "tried"));
        input.Writer.Complete();

        var judge = new LlmJudge("j", provider, new LlmJudgeOptions { Workers = 1, MinScore = 3.0f }, NullLogger<LlmJudge>.Instance);
        await judge.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlesUnparseableScore_ByDroppingRow()
    {
        var provider = new ScoreProvider(() => "not json");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        await input.Writer.WriteAsync(Row.Empty.With("instruction", "x").With("response", "y"));
        input.Writer.Complete();

        var judge = new LlmJudge("j", provider, new LlmJudgeOptions { Workers = 1, MinScore = 3.0f }, NullLogger<LlmJudge>.Instance);
        await judge.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().BeEmpty();
    }

    private sealed class ScoreProvider : ILlmProvider
    {
        private readonly Func<string> respond;

        public ScoreProvider(Func<string> respond) => this.respond = respond;

        public string ProviderName => "stub";

        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options, CancellationToken ct)
            => Task.FromResult(this.respond());
    }
}
