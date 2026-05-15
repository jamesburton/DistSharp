namespace DistSharp.Core.Pipeline;

/// <summary>Describes a step's position in a <see cref="PipelineDefinition"/> DAG.</summary>
public sealed class StepDefinition
{
    /// <summary>Initializes a new instance of the <see cref="StepDefinition"/> class.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="step">The step implementation.</param>
    /// <param name="dependsOn">Upstream step names. Pass <see langword="null"/> or empty for source steps.</param>
    public StepDefinition(string name, IStep step, IReadOnlyList<string>? dependsOn = null)
    {
        this.Name = name;
        this.Step = step;
        this.DependsOn = dependsOn ?? new List<string>();
    }

    /// <summary>Gets the unique step name (matches <see cref="IStep.Name"/>).</summary>
    public string Name { get; }

    /// <summary>Gets the step implementation to run.</summary>
    public IStep Step { get; }

    /// <summary>Gets the names of steps whose output this step depends on. Empty for source steps.</summary>
    public IReadOnlyList<string> DependsOn { get; }
}
