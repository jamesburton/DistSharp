namespace DistSharp.Core.Sync;

/// <summary>A description of the sync work to be done, returned in dry-run mode.</summary>
/// <param name="NewCount">Number of new symbols that will be generated.</param>
/// <param name="StaleCount">Number of stale rows that will be regenerated.</param>
/// <param name="UnchangedCount">Number of unchanged rows that will be skipped.</param>
/// <param name="OrphanCount">Number of orphaned rows subject to the orphan policy.</param>
public sealed record SyncPlan(int NewCount, int StaleCount, int UnchangedCount, int OrphanCount)
{
    /// <summary>Gets the estimated number of LLM calls required to complete the sync.</summary>
    public int EstimatedLlmCalls => this.NewCount + this.StaleCount;
}
