namespace DistSharp.Core.Abstractions;

/// <summary>Creates <see cref="ILlmProvider"/> instances from configuration.</summary>
public interface ILlmProviderFactory
{
    /// <summary>Returns the <see cref="ILlmProvider"/> for the given <paramref name="providerName"/>.</summary>
    /// <param name="providerName">Provider identifier, e.g. <c>openai</c>, <c>anthropic</c>.</param>
    /// <returns>The configured provider.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="providerName"/> is not registered.</exception>
    ILlmProvider Create(string providerName);
}
