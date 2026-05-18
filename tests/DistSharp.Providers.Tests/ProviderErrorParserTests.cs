using DistSharp.Providers.Internal;
using FluentAssertions;
using Xunit;

namespace DistSharp.Providers.Tests;

public sealed class ProviderErrorParserTests
{
    [Theory]
    [InlineData(@"{""error"":{""message"":""The model `gpt5.4` does not exist""}}", "The model `gpt5.4` does not exist")]
    [InlineData(@"{""error"":""model 'foo' not found""}", "model 'foo' not found")]
    [InlineData(@"{""message"":""rate limited""}", "rate limited")]
    public void ExtractMessage_PicksOutCommonShapes(string raw, string expected)
    {
        ProviderErrorParser.ExtractMessage(raw).Should().Be(expected);
    }

    [Fact]
    public void ExtractMessage_HandlesPlainStringErrorWithoutThrowing()
    {
        // Regression: previous version threw InvalidOperationException when "error" was a plain string.
        var raw = @"{""error"":""down""}";
        ProviderErrorParser.ExtractMessage(raw).Should().Be("down");
    }

    [Fact]
    public void ExtractMessage_ReturnsTrimmedRawBody_WhenJsonIsUnstructured()
    {
        ProviderErrorParser.ExtractMessage("just some text").Should().Be("just some text");
    }

    [Fact]
    public void ExtractMessage_ReturnsNull_ForNullOrWhitespace()
    {
        ProviderErrorParser.ExtractMessage(null).Should().BeNull();
        ProviderErrorParser.ExtractMessage(string.Empty).Should().BeNull();
        ProviderErrorParser.ExtractMessage("   ").Should().BeNull();
    }

    [Theory]
    [InlineData(404, @"{""error"":{""code"":""model_not_found"",""message"":""The model X does not exist""}}", true)]
    [InlineData(400, @"{""error"":{""message"":""invalid_model name""}}", true)]
    [InlineData(404, @"{""error"":""model not found""}", true)]
    [InlineData(404, @"{""error"":{""message"":""something else""}}", false)]
    [InlineData(429, @"{""error"":{""code"":""model_not_found""}}", false)]
    [InlineData(500, "", false)]
    public void IsModelNotFound_RecognisesCommonProviderSignals(int status, string raw, bool expected)
    {
        ProviderErrorParser.IsModelNotFound(status, raw).Should().Be(expected);
    }
}
