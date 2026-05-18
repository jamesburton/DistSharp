using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using CoreChatMessage = DistSharp.Core.Models.ChatMessage;
using CoreChatRole = DistSharp.Core.Models.ChatRole;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace DistSharp.Providers.Onnx;

/// <summary>Provider for local ONNX Runtime GenAI models sourced from the Hugging Face Hub cache.</summary>
/// <remarks>Phase 1 supports CPU execution only. GPU EPs (CUDA, DirectML, Vulkan) are planned for Phase 2.</remarks>
public sealed class OnnxProvider : ILlmProvider, IDisposable
{
    private readonly OnnxProviderOptions options;
    private readonly HuggingFaceModelCache cache;
    private readonly ILogger<OnnxProvider> logger;
    private readonly SemaphoreSlim sessionSemaphore;

    // Lazily-created chat client; null until first CompleteAsync call resolves the model path.
    private IChatClient? chatClient;
    private bool disposed;

    /// <summary>Initializes a new instance of the <see cref="OnnxProvider"/> class.</summary>
    /// <param name="options">Provider options.</param>
    /// <param name="cache">The HF Hub cache helper used to locate model files.</param>
    /// <param name="logger">The logger.</param>
    public OnnxProvider(OnnxProviderOptions options, HuggingFaceModelCache cache, ILogger<OnnxProvider> logger)
    {
        options.Validate();
        this.options = options;
        this.cache = cache;
        this.logger = logger;
        this.sessionSemaphore = new SemaphoreSlim(options.MaxConcurrentSessions, options.MaxConcurrentSessions);
    }

    /// <inheritdoc/>
    public string ProviderName => "onnx";

    /// <inheritdoc/>
    public async Task<string> CompleteAsync(
        IReadOnlyList<CoreChatMessage> messages,
        LlmRequestOptions options,
        CancellationToken cancellationToken)
    {
        var repoId = options.Model ?? this.options.DefaultModel
            ?? throw new InvalidOperationException("onnx: model is not specified. Set --model or configure OnnxProviderOptions.DefaultModel.");

        var client = await this.GetOrCreateChatClientAsync(repoId, cancellationToken).ConfigureAwait(false);

        var meaiMessages = messages.Select(ToMeaiMessage).ToList();
        var chatOptions = BuildChatOptions(options);

        await this.sessionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.logger.LogDebug("ONNX: sending {Count} messages to {Model}", messages.Count, repoId);
            var response = await client.GetResponseAsync(meaiMessages, chatOptions, cancellationToken).ConfigureAwait(false);
            return response.Text ?? string.Empty;
        }
        finally
        {
            this.sessionSemaphore.Release();
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var repos = this.cache.ListCachedRepos().OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult<IReadOnlyList<string>>(repos);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        (this.chatClient as IDisposable)?.Dispose();
        this.sessionSemaphore.Dispose();
    }

    private static MeaiChatMessage ToMeaiMessage(CoreChatMessage m) =>
        new(ToMeaiRole(m.Role), m.Content);

    private static MeaiChatRole ToMeaiRole(CoreChatRole role) => role switch
    {
        CoreChatRole.System => MeaiChatRole.System,
        CoreChatRole.User => MeaiChatRole.User,
        CoreChatRole.Assistant => MeaiChatRole.Assistant,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static ChatOptions? BuildChatOptions(LlmRequestOptions options)
    {
        if (options.Temperature is null && options.MaxTokens is null)
        {
            return null;
        }

        var chatOptions = new ChatOptions();
        if (options.Temperature is { } t)
        {
            chatOptions.Temperature = t;
        }

        if (options.MaxTokens is { } mt)
        {
            chatOptions.MaxOutputTokens = mt;
        }

        return chatOptions;
    }

    private async Task<IChatClient> GetOrCreateChatClientAsync(string repoId, CancellationToken cancellationToken)
    {
        if (this.chatClient is not null)
        {
            return this.chatClient;
        }

        var variantDir = await this.cache.ResolveVariantAsync(
            repoId, this.options.ModelVariant, cancellationToken).ConfigureAwait(false);

        this.logger.LogInformation("ONNX: loading model from {Path}", variantDir);

        // OnnxRuntimeGenAIChatClient takes the path to the model directory and implements IChatClient.
        this.chatClient = new OnnxRuntimeGenAIChatClient(variantDir);
        return this.chatClient;
    }
}
