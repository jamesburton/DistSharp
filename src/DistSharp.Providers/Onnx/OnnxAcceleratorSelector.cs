namespace DistSharp.Providers.Onnx;

/// <summary>Selects the execution provider (EP) for ONNX Runtime GenAI.</summary>
/// <remarks>Phase 1 always returns <c>cpu</c>. CUDA, DirectML, and Vulkan probing will be added in Phase 2.</remarks>
public static class OnnxAcceleratorSelector
{
    /// <summary>Returns the execution provider string to use for ONNX Runtime GenAI session creation.</summary>
    /// <returns>The execution provider identifier, e.g. <c>"cpu"</c>.</returns>
    public static string SelectAccelerator()
    {
        // TODO (Phase 2): probe for CUDA, DirectML, and Vulkan availability and return the best EP.
        return "cpu";
    }
}
