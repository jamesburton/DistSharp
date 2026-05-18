using DistSharp.Core.Abstractions;
using DistSharp.Providers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DistSharp.Providers.Tests;

public sealed class LlmProviderFactoryTests
{
    [Theory]
    [InlineData("openai", "openai")]
    [InlineData("OpenAI", "openai")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("azure-openai", "azure-openai")]
    [InlineData("azure", "azure-openai")]
    [InlineData("gemini", "gemini")]
    [InlineData("google", "gemini")]
    [InlineData("ollama", "ollama")]
    [InlineData("lmstudio", "lmstudio")]
    [InlineData("lm-studio", "lmstudio")]
    [InlineData("openai-compatible", "openai-compatible")]
    [InlineData("compatible", "openai-compatible")]
    [InlineData("onnx", "onnx")]
    public void Create_ReturnsExpectedProvider(string input, string expectedName)
    {
        var factory = BuildFactory();

        var provider = factory.Create(input);

        provider.ProviderName.Should().Be(expectedName);
    }

    [Fact]
    public void Create_ThrowsInvalidOperation_ForUnknownProvider()
    {
        var factory = BuildFactory();

        var act = () => factory.Create("unknown-provider");

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown-provider*");
    }

    private static ILlmProviderFactory BuildFactory()
    {
        var services = new ServiceCollection();
        services.AddDistSharpProviders();
        return services.BuildServiceProvider().GetRequiredService<ILlmProviderFactory>();
    }
}
