namespace DistSharp.Cli.Commands;

/// <summary>Options bound from the <c>inspect</c> command line.</summary>
public sealed class InspectCommandOptions
{
    /// <summary>Gets or sets the solution/project path.</summary>
    public string Solution { get; set; } = string.Empty;

    /// <summary>Gets or sets the path to write a JSON report to. <see langword="null"/> means stdout-only summary.</summary>
    public string? Report { get; set; }

    /// <summary>Gets or sets a value indicating whether each included file is listed.</summary>
    public bool ShowFiles { get; set; }

    /// <summary>Gets or sets a value indicating whether each included symbol is listed.</summary>
    public bool ShowSymbols { get; set; }

    /// <summary>Gets or sets a value indicating whether test projects are included.</summary>
    public bool IncludeTests { get; set; }
}
