namespace DistSharp.Core.Sync;

/// <summary>Determines what happens to manifest rows whose source symbol no longer exists in the solution.</summary>
public enum OrphanPolicy
{
    /// <summary>Remove orphaned rows from the dataset and manifest.</summary>
    Drop,

    /// <summary>Leave orphaned rows in the dataset and manifest unchanged.</summary>
    Keep,

    /// <summary>Move orphaned rows to <c>_distsharp/archive/{date}.jsonl</c> and remove from the active manifest.</summary>
    Archive,
}
