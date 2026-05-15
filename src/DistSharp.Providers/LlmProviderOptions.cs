namespace DistSharp.Providers;

/// <summary>Shared options for all LLM providers.</summary>
public class LlmProviderOptions
{
    /// <summary>Gets or sets the API key for the provider. Provider-specific environment variables are read by default.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets the base URL for the provider's HTTP API.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets the default model identifier used when <see cref="DistSharp.Core.Models.LlmRequestOptions.Model"/> is <see langword="null"/>.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Gets or sets the HTTP request timeout. Default: 2 minutes.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets the maximum number of retries on transient failures. Default: 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Gets or sets the initial retry delay; doubled on each subsequent retry. Default: 1 second.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
