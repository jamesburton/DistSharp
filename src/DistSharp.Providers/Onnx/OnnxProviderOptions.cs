namespace DistSharp.Providers.Onnx;

/// <summary>Options for the ONNX Runtime GenAI provider.</summary>
public sealed class OnnxProviderOptions : LlmProviderOptions
{
    /// <summary>Gets or sets the execution provider to use. Supported values in Phase 1: <c>cpu</c> (default).</summary>
    /// <remarks>CUDA, DirectML, and Vulkan will be supported in Phase 2. See <c>docs/superpowers/specs/2026-05-18-onnx-provider-design.md</c>.</remarks>
    public string? Accelerator { get; set; }

    /// <summary>Gets or sets the model variant subdirectory within the Hugging Face snapshot, e.g. <c>cpu-int4-rtn-block-32-acc-level-4</c>. When <see langword="null"/>, variant priority order is used.</summary>
    public string? ModelVariant { get; set; }

    /// <summary>Gets or sets the Hugging Face cache root directory. When <see langword="null"/>, resolved from <c>HF_HOME</c> env var or the platform default.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>Gets or sets the maximum number of concurrent ONNX Runtime sessions. Default: 1.</summary>
    public int MaxConcurrentSessions { get; set; } = 1;

    /// <summary>Validates the options, throwing if unsupported values are present.</summary>
    /// <exception cref="NotSupportedException">Thrown when <see cref="Accelerator"/> is not <c>cpu</c>.</exception>
    public void Validate()
    {
        if (this.Accelerator is not null && !this.Accelerator.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Accelerator '{this.Accelerator}' is not supported in Phase 1. " +
                "Phase 1 supports CPU only — see docs/superpowers/specs/2026-05-18-onnx-provider-design.md");
        }
    }
}
