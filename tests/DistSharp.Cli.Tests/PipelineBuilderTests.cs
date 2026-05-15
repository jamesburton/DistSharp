using DistSharp.Cli;
using DistSharp.Cli.Progress;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace DistSharp.Cli.Tests;

public sealed class PipelineBuilderTests
{
    [Fact]
    public void Build_CreatesPipelineDefinitionWithMatchingStepCount()
    {
        var config = new PipelineConfig
        {
            Name = "test",
            Solution = new SolutionConfig { Path = "./Sample.sln" },
            Steps = new List<StepConfig>
            {
                new() { Name = "extract", Type = "RoslynSymbolExtractor" },
                new() { Name = "sample", Type = "StratifiedSampler", DependsOn = new List<string> { "extract" }, Config = new Dictionary<string, object?> { ["max_rows"] = 100 } },
            },
        };

        var def = MakeBuilder().Build(config);

        def.Steps.Should().HaveCount(2);
        def.Name.Should().Be("test");
        def.Steps[0].Name.Should().Be("extract");
        def.Steps[1].DependsOn.Should().BeEquivalentTo(new[] { "extract" });
    }

    [Fact]
    public void Build_WrapsEachStepInMetricsTrackingStep()
    {
        var config = new PipelineConfig
        {
            Steps = new List<StepConfig>
            {
                new() { Name = "extract", Type = "RoslynSymbolExtractor" },
            },
        };

        var def = MakeBuilder().Build(config);

        def.Steps[0].Step.Should().BeOfType<MetricsTrackingStep>();
    }

    [Fact]
    public void Build_ThrowsForUnknownStepType()
    {
        var config = new PipelineConfig
        {
            Steps = new List<StepConfig>
            {
                new() { Name = "x", Type = "WhatIsThis" },
            },
        };

        var act = () => MakeBuilder().Build(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*WhatIsThis*");
    }

    [Fact]
    public void Build_BuildsLlmStepWithProviderFromFactory()
    {
        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var providerFactory = Substitute.For<ILlmProviderFactory>();
        var provider = Substitute.For<ILlmProvider>();
        provider.ProviderName.Returns("stub");
        providerFactory.Create("openai").Returns(provider);

        var builder = new PipelineBuilder(analyzer, providerFactory, NullLoggerFactory.Instance);

        var config = new PipelineConfig
        {
            Steps = new List<StepConfig>
            {
                new()
                {
                    Name = "gen",
                    Type = "LlmStep",
                    Config = new Dictionary<string, object?>
                    {
                        ["provider"] = "openai",
                        ["dataset_type"] = "explanation",
                        ["model"] = "gpt-4.1-mini",
                        ["workers"] = 2,
                    },
                },
            },
        };

        var def = builder.Build(config);

        def.Steps.Should().ContainSingle();
        providerFactory.Received(1).Create("openai");
    }

    [Fact]
    public void Build_ThrowsWhenLlmStepHasNoProvider()
    {
        var config = new PipelineConfig
        {
            Steps = new List<StepConfig>
            {
                new() { Name = "gen", Type = "LlmStep" },
            },
        };

        var act = () => MakeBuilder().Build(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*provider*");
    }

    private static PipelineBuilder MakeBuilder()
    {
        var analyzer = Substitute.For<ISolutionAnalyzer>();
        var providerFactory = Substitute.For<ILlmProviderFactory>();
        var provider = Substitute.For<ILlmProvider>();
        providerFactory.Create(Arg.Any<string>()).Returns(provider);
        return new PipelineBuilder(analyzer, providerFactory, NullLoggerFactory.Instance);
    }
}
