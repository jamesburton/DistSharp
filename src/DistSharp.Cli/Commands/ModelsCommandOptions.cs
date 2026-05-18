namespace DistSharp.Cli.Commands;

/// <summary>Options bound from the <c>models</c> command line.</summary>
public sealed class ModelsCommandOptions
{
    /// <summary>Gets or sets the LLM provider name (e.g. <c>openai</c>, <c>anthropic</c>, <c>gemini</c>, <c>ollama</c>).</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Gets or sets a case-insensitive substring filter; only model IDs containing this text are listed.</summary>
    public string? Filter { get; set; }
}
