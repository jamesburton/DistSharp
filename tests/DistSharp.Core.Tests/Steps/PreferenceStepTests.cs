using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Steps;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DistSharp.Core.Tests.Steps;

public sealed class PreferenceStepTests
{
    // -------------------------------------------------------------------------
    // Row emission shape
    // -------------------------------------------------------------------------
    [Fact]
    public async Task EmitsRow_WithAllRequiredPreferenceFields()
    {
        /* Provider: first two calls return response text; third (judge) returns winner=1, margin=3. */
        var callCount = 0;
        var provider = new DelegateProvider((_, _) =>
        {
            callCount++;
            return callCount switch
            {
                1 => "chosen text",
                2 => "rejected text",
                _ => @"{""winner"":1,""margin"":3,""reason"":""first is better""}",
            };
        });

        var (output, _) = await RunSingleRowAsync(provider, new PreferenceStepOptions
        {
            Workers = 1,
            DatasetType = "explanation",
            JudgeEnabled = true,
        });

        output.Should().ContainSingle();
        var row = output[0];
        row.Get<string>("prompt").Should().NotBeNullOrEmpty();
        row.Get<string>("chosen").Should().Be("chosen text");
        row.Get<string>("rejected").Should().Be("rejected text");
        row.Get<float>("margin").Should().Be(3f);
        row.Get<float>("score_chosen").Should().BeGreaterThan(0f);
        row.Get<float>("score_rejected").Should().BeGreaterThan(0f);
        row.Get<string>("rejection_strategy").Should().Be("higher_temperature");
        row.Get<string>("dataset_type").Should().Be("explanation");
        row.Get<string>("symbol_fqn").Should().Be("TestNs.TestClass.TestMethod");
    }

    [Fact]
    public async Task EmitsRow_WithoutJudge_WhenJudgeDisabled()
    {
        /* Only 2 calls expected when judge is disabled. */
        var callCount = 0;
        var provider = new DelegateProvider((_, _) =>
        {
            callCount++;
            return callCount == 1 ? "chosen text" : "rejected text";
        });

        var (output, _) = await RunSingleRowAsync(provider, new PreferenceStepOptions
        {
            Workers = 1,
            DatasetType = "explanation",
            JudgeEnabled = false,
        });

        output.Should().ContainSingle();
        callCount.Should().Be(2);

        var row = output[0];
        row.Get<string>("chosen").Should().Be("chosen text");
        row.Get<string>("rejected").Should().Be("rejected text");
        row.Get<float>("margin").Should().Be(0f);
    }

    // -------------------------------------------------------------------------
    // Winner swap: judge picks response 2 as winner
    // -------------------------------------------------------------------------
    [Fact]
    public async Task SwapsChosenAndRejected_WhenJudgePicksWinnerTwo()
    {
        var callCount = 0;
        var provider = new DelegateProvider((_, _) =>
        {
            callCount++;
            return callCount switch
            {
                1 => "low-temp response",
                2 => "high-temp response",
                _ => @"{""winner"":2,""margin"":2,""reason"":""second is better""}",
            };
        });

        var (output, _) = await RunSingleRowAsync(provider, new PreferenceStepOptions
        {
            Workers = 1,
            DatasetType = "explanation",
            JudgeEnabled = true,
        });

        output.Should().ContainSingle();

        /* When winner=2, the high-temp response becomes chosen and low-temp becomes rejected. */
        output[0].Get<string>("chosen").Should().Be("high-temp response");
        output[0].Get<string>("rejected").Should().Be("low-temp response");
    }

    // -------------------------------------------------------------------------
    // Strategy mode
    // -------------------------------------------------------------------------
    [Fact]
    public async Task PassesCorrectTemperatures_ForHigherTemperatureStrategy()
    {
        var capturedTemps = new List<float?>();
        var callCount = 0;
        var provider = new DelegateProvider((_, opts) =>
        {
            capturedTemps.Add(opts.Temperature);
            callCount++;
            return callCount switch
            {
                1 => "chosen text",
                2 => "rejected text",
                _ => @"{""winner"":1,""margin"":2,""reason"":""ok""}",
            };
        });

        await RunSingleRowAsync(provider, new PreferenceStepOptions
        {
            Workers = 1,
            DatasetType = "explanation",
            ChosenTemperature = 0.7f,
            RejectedTemperature = 1.4f,
            JudgeEnabled = true,
        });

        capturedTemps.Should().HaveCount(3);
        capturedTemps[0].Should().Be(0.7f);   /* chosen call */
        capturedTemps[1].Should().Be(1.4f);   /* rejected call */
        capturedTemps[2].Should().Be(0f);     /* judge call — deterministic */
    }

    [Fact]
    public void Constructor_ThrowsNotSupportedException_ForUnrecognisedStrategy()
    {
        var act = () => new PreferenceStep(
            "s",
            new DelegateProvider((_, _) => string.Empty),
            new PreferenceStepOptions { RejectionStrategy = "smaller_model" },
            NullLogger<PreferenceStep>.Instance);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*smaller_model*");
    }

    // -------------------------------------------------------------------------
    // Margin extraction
    // -------------------------------------------------------------------------
    [Fact]
    public async Task ExtractsMargin_FromJudgeResponse()
    {
        var callCount = 0;
        var provider = new DelegateProvider((_, _) =>
        {
            callCount++;
            return callCount switch
            {
                1 => "response A",
                2 => "response B",
                _ => @"{""winner"":1,""margin"":4,""reason"":""decisive""}",
            };
        });

        var (output, _) = await RunSingleRowAsync(provider, new PreferenceStepOptions
        {
            Workers = 1,
            DatasetType = "explanation",
            JudgeEnabled = true,
        });

        output.Should().ContainSingle();
        output[0].Get<float>("margin").Should().Be(4f);
    }

    [Fact]
    public async Task ExtractsMargin_WhenJudgeJsonHasCodeFences()
    {
        var callCount = 0;
        var provider = new DelegateProvider((_, _) =>
        {
            callCount++;
            if (callCount <= 2)
            {
                return "response";
            }

            return "```json\n{\"winner\":2,\"margin\":1,\"reason\":\"slight\"}\n```";
        });

        var (output, _) = await RunSingleRowAsync(provider, new PreferenceStepOptions
        {
            Workers = 1,
            DatasetType = "explanation",
            JudgeEnabled = true,
        });

        output.Should().ContainSingle();
        output[0].Get<float>("margin").Should().Be(1f);
    }

    // -------------------------------------------------------------------------
    // Drop behaviour
    // -------------------------------------------------------------------------
    [Fact]
    public async Task DropsRow_WhenJudgeResponseDoesNotParse()
    {
        var callCount = 0;
        var provider = new DelegateProvider((_, _) =>
        {
            callCount++;
            return callCount <= 2 ? "response" : "not json";
        });

        var (output, _) = await RunSingleRowAsync(provider, new PreferenceStepOptions
        {
            Workers = 1,
            DatasetType = "explanation",
            JudgeEnabled = true,
        });

        output.Should().BeEmpty();
    }

    [Fact]
    public async Task DropsRow_WhenSymbolFieldMissing()
    {
        var provider = new DelegateProvider((_, _) => "ok");
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();

        await input.Writer.WriteAsync(Row.Empty); /* row with no 'symbol' field */
        input.Writer.Complete();

        var step = new PreferenceStep(
            "pref",
            provider,
            new PreferenceStepOptions { Workers = 1 },
            NullLogger<PreferenceStep>.Instance);

        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        rows.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static async Task<(List<Row> Output, int CallCount)> RunSingleRowAsync(
        DelegateProvider provider,
        PreferenceStepOptions options)
    {
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();

        await input.Writer.WriteAsync(Row.Empty.With("symbol", Sym("TestNs.TestClass.TestMethod")));
        input.Writer.Complete();

        var step = new PreferenceStep("pref", provider, options, NullLogger<PreferenceStep>.Instance);
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = await output.Reader.ReadAllAsync().ToListAsync();
        return (rows, provider.TotalCallCount);
    }

    private static ExtractedSymbol Sym(string fqn) => new()
    {
        FullyQualifiedName = fqn,
        SignatureText = "void TestMethod()",
        BodyText = "// body",
        ContainingType = "TestClass",
        Namespace = "TestNs",
        FilePath = "Test.cs",
        Complexity = 1,
        Kind = "method",
    };

    private sealed class DelegateProvider : ILlmProvider
    {
        private readonly Func<IReadOnlyList<ChatMessage>, LlmRequestOptions, string> respond;
        private int totalCallCount;

        public DelegateProvider(Func<IReadOnlyList<ChatMessage>, LlmRequestOptions, string> respond)
            => this.respond = respond;

        public string ProviderName => "stub";

        public int TotalCallCount => this.totalCallCount;

        public Task<string> CompleteAsync(
            IReadOnlyList<ChatMessage> messages,
            LlmRequestOptions options,
            CancellationToken ct)
        {
            Interlocked.Increment(ref this.totalCallCount);
            return Task.FromResult(this.respond(messages, options));
        }
    }
}
