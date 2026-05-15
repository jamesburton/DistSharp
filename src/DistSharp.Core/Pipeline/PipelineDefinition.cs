namespace DistSharp.Core.Pipeline;

/// <summary>The validated, instantiated form of a pipeline: a named DAG of <see cref="StepDefinition"/> nodes.</summary>
public sealed class PipelineDefinition
{
    /// <summary>Initializes a new instance of the <see cref="PipelineDefinition"/> class.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <param name="steps">The step definitions.</param>
    public PipelineDefinition(string name, IReadOnlyList<StepDefinition> steps)
    {
        this.Name = name;
        this.Steps = steps;
    }

    /// <summary>Gets the pipeline name (used in logs and checkpoint IDs).</summary>
    public string Name { get; }

    /// <summary>Gets the ordered list of step definitions. Order does not imply execution order — the executor topologically sorts them.</summary>
    public IReadOnlyList<StepDefinition> Steps { get; }
}
