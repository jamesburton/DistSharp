namespace DistSharp.Cli.Progress;

/// <summary>Mutable counters tracking a step's runtime progress.</summary>
public sealed class StepMetrics
{
    private long rowsIn;
    private long rowsOut;

    /// <summary>Gets the step name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the number of rows read from the input channel.</summary>
    public long RowsIn => Interlocked.Read(ref this.rowsIn);

    /// <summary>Gets the number of rows written to the output channel.</summary>
    public long RowsOut => Interlocked.Read(ref this.rowsOut);

    /// <summary>Gets or sets the current status: <c>pending</c>, <c>running</c>, <c>complete</c>, <c>cancelled</c>, or <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Gets or sets an error message if the step failed.</summary>
    public string? Error { get; set; }

    /// <summary>Atomically increments the <see cref="RowsIn"/> counter.</summary>
    public void IncrementRowsIn() => Interlocked.Increment(ref this.rowsIn);

    /// <summary>Atomically increments the <see cref="RowsOut"/> counter.</summary>
    public void IncrementRowsOut() => Interlocked.Increment(ref this.rowsOut);
}
