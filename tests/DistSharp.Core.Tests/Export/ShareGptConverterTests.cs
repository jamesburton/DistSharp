using System.Text.Json;
using DistSharp.Core.Export;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Export;

public sealed class ShareGptConverterTests
{
    [Fact]
    public void ConvertsExplanationRow_ToTwoTurnConversation()
    {
        var row = Parse(@"{""instruction"":""Explain X"",""context"":""ctx"",""response"":""resp""}");

        var result = ShareGptConverter.Convert(row);

        var conversations = (Dictionary<string, object?>[])result["conversations"]!;
        conversations.Should().HaveCount(2);
        conversations[0]["from"].Should().Be("human");
        ((string)conversations[0]["value"]!).Should().Contain("Explain X");
        ((string)conversations[0]["value"]!).Should().Contain("ctx");
        conversations[1]["from"].Should().Be("gpt");
        conversations[1]["value"].Should().Be("resp");
    }

    [Fact]
    public void HandlesArchitectureQaRow()
    {
        var row = Parse(@"{""question"":""Q"",""answer"":""A""}");

        var result = ShareGptConverter.Convert(row);

        var conversations = (Dictionary<string, object?>[])result["conversations"]!;
        ((string)conversations[0]["value"]!).Should().Be("Q");
        conversations[1]["value"].Should().Be("A");
    }

    private static Dictionary<string, JsonElement> Parse(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
