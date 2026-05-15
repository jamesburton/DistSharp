using System.Collections.Generic;
using DistSharp.Core.Models;

namespace DistSharp.Core.Writers;

/// <summary>Converts a <see cref="Row"/> to a flat dictionary suitable for writing to disk.</summary>
internal static class RowSerializer
{
    /// <summary>Fields included when <c>WriteMetadata = false</c>. All others are dropped.</summary>
    public static readonly HashSet<string> CoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "instruction", "input", "output",
        "prompt", "completion", "response",
        "question", "answer", "context",
        "code", "buggy_code", "fixed_code",
        "original_code", "refactored_code",
        "explanation",
    };

    /// <summary>Returns a dictionary of <paramref name="row"/>'s fields, filtered by <paramref name="writeMetadata"/>.</summary>
    /// <param name="row">The row to serialize.</param>
    /// <param name="writeMetadata">If <see langword="false"/>, only fields in <see cref="CoreFields"/> are kept.</param>
    public static Dictionary<string, object?> ToDictionary(Row row, bool writeMetadata)
    {
        var result = new Dictionary<string, object?>(row.Fields.Count, StringComparer.Ordinal);
        foreach (var (key, value) in row.Fields)
        {
            if (key.StartsWith('_'))
            {
                continue;
            }

            if (!writeMetadata && !CoreFields.Contains(key))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }
}
