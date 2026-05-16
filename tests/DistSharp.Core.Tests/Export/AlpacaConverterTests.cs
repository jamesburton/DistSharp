using System.Text.Json;
using DistSharp.Core.Export;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Export;

public sealed class AlpacaConverterTests
{
    [Fact]
    public void ConvertsExplanationRow()
    {
        var row = Parse(@"{""instruction"":""Explain X"",""context"":""code"",""response"":""resp""}");

        var result = AlpacaConverter.Convert(row);

        result["instruction"].Should().Be("Explain X");
        result["input"].Should().Be("code");
        result["output"].Should().Be("resp");
    }

    [Fact]
    public void ConvertsBugFixRow_CombiningFixedCodeAndExplanation()
    {
        var row = Parse(@"{""instruction"":""Find the bug"",""buggy_code"":""x"",""fixed_code"":""y"",""explanation"":""off-by-one""}");

        var result = AlpacaConverter.Convert(row);

        ((string)result["output"]!).Should().Contain("y");
        ((string)result["output"]!).Should().Contain("off-by-one");
    }

    [Fact]
    public void ConvertsArchitectureQaRow()
    {
        var row = Parse(@"{""question"":""What pattern?"",""answer"":""DI""}");

        var result = AlpacaConverter.Convert(row);

        result["instruction"].Should().Be("What pattern?");
        result["output"].Should().Be("DI");
    }

    private static Dictionary<string, JsonElement> Parse(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
