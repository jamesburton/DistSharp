namespace DistSharp.Core.Abstractions;

/// <summary>Thrown when an <see cref="ILlmProvider"/> call fails after exhausting retries.</summary>
public sealed class LlmProviderException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="LlmProviderException"/> class.</summary>
    /// <param name="providerName">The provider that produced the error.</param>
    /// <param name="message">A human-readable error description.</param>
    /// <param name="statusCode">HTTP status code from the failed response, or <see langword="null"/> if the failure was not HTTP-related.</param>
    /// <param name="responseBody">The response body returned by the provider, if any.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public LlmProviderException(string providerName, string message, int? statusCode = null, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        this.ProviderName = providerName;
        this.StatusCode = statusCode;
        this.ResponseBody = responseBody;
    }

    /// <summary>Gets the provider name (e.g. <c>openai</c>, <c>anthropic</c>).</summary>
    public string ProviderName { get; }

    /// <summary>Gets the HTTP status code if the failure was a response, otherwise <see langword="null"/>.</summary>
    public int? StatusCode { get; }

    /// <summary>Gets the response body returned by the provider, if available.</summary>
    public string? ResponseBody { get; }
}
