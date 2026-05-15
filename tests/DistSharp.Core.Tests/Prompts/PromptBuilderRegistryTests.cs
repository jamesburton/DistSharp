using DistSharp.Core.Models;
using DistSharp.Core.Prompts;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Prompts;

public sealed class PromptBuilderRegistryTests
{
    [Theory]
    [InlineData("explanation")]
    [InlineData("completion")]
    [InlineData("bug-fix")]
    [InlineData("unit-test")]
    [InlineData("docstring")]
    [InlineData("refactor")]
    [InlineData("architecture-qa")]
    public void Build_ReturnsMessagesAndParser_ForEverySupportedType(string type)
    {
        var result = PromptBuilderRegistry.Build(type, Sym());

        result.Messages.Should().NotBeEmpty();
        result.ParseResponse.Should().NotBeNull();
    }

    [Fact]
    public void Build_ThrowsForUnknownType()
    {
        var act = () => PromptBuilderRegistry.Build("unknown", Sym());
        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");
    }

    [Fact]
    public void ExplanationBuilder_PutsInstructionInPreparedFields()
    {
        var result = ExplanationPromptBuilder.Build(Sym());
        result.PreparedFields.Should().ContainKey("instruction");
        result.PreparedFields.Should().ContainKey("context");
    }

    [Fact]
    public void CompletionBuilder_SplitsBodyText_AndStoresPromptField()
    {
        var result = CompletionPromptBuilder.Build(Sym());
        result.PreparedFields.Should().ContainKey("prompt");
        var parsed = result.ParseResponse("some completion");
        parsed.Should().NotBeNull();
        parsed!.Should().ContainKey("completion");
    }

    [Fact]
    public void BugFixBuilder_ParsesValidJson()
    {
        var result = BugFixPromptBuilder.Build(Sym());
        var parsed = result.ParseResponse(@"{""buggy_code"":""a"",""fixed_code"":""b"",""explanation"":""off-by-one""}");
        parsed.Should().NotBeNull();
        parsed!["buggy_code"].Should().Be("a");
        parsed["fixed_code"].Should().Be("b");
        parsed["explanation"].Should().Be("off-by-one");
    }

    [Fact]
    public void BugFixBuilder_ReturnsNull_ForInvalidJson()
    {
        var result = BugFixPromptBuilder.Build(Sym());
        result.ParseResponse("not valid json at all").Should().BeNull();
    }

    [Fact]
    public void BugFixBuilder_StripsCodeFences_BeforeParsing()
    {
        var result = BugFixPromptBuilder.Build(Sym());
        var raw = "```json\n{\"buggy_code\":\"a\",\"fixed_code\":\"b\",\"explanation\":\"e\"}\n```";
        var parsed = result.ParseResponse(raw);
        parsed.Should().NotBeNull();
        parsed!["buggy_code"].Should().Be("a");
    }

    [Fact]
    public void RefactorBuilder_RequiresBothKeys()
    {
        var result = RefactorPromptBuilder.Build(Sym(complexity: 12));
        result.ParseResponse(@"{""refactored_code"":""x""}").Should().BeNull();
        result.ParseResponse(@"{""refactored_code"":""x"",""explanation"":""y""}").Should().NotBeNull();
    }

    [Fact]
    public void ArchitectureQaBuilder_ParsesQuestionAndAnswer()
    {
        var result = ArchitectureQaPromptBuilder.Build(Sym(kind: "class"));
        var parsed = result.ParseResponse(@"{""question"":""why?"",""answer"":""because""}");
        parsed.Should().NotBeNull();
        parsed!["question"].Should().Be("why?");
        parsed["answer"].Should().Be("because");
    }

    private static ExtractedSymbol Sym(string kind = "method", int complexity = 5) => new()
    {
        FullyQualifiedName = "MyApp.OrderService.PlaceOrderAsync",
        SignatureText = "public Task<int> PlaceOrderAsync(string a)",
        BodyText = "if (a == null) throw new ArgumentNullException(nameof(a));\nreturn Task.FromResult(1);",
        ContainingType = "OrderService",
        Namespace = "MyApp",
        FilePath = "OrderService.cs",
        Complexity = complexity,
        Kind = kind,
    };
}
