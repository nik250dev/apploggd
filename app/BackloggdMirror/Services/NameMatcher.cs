using System;
using System.Collections.Generic;

namespace BackloggdMirror.Services;

/// <summary>Exact match first, then containment capped at 30% unexplained text. Shared by window titles and ROM names.</summary>
internal static class NameMatcher
{
    private const double MaxUnexplainedRatio = 0.30;

    /// <summary>The input must come normalized and lowered; the closest length wins.</summary>
    internal static (string? IdIgdb, string? MatchedName, bool Exact, int LengthDiff) FindBest(
        IEnumerable<(string NormalizedName, string? IdIgdb)> index,
        string normalizedInput)
    {
        if (string.IsNullOrWhiteSpace(normalizedInput))
            return (null, null, false, int.MaxValue);

        foreach (var (normalizedName, idIgdb) in index)
        {
            if (normalizedName.Equals(normalizedInput, StringComparison.OrdinalIgnoreCase))
                return (idIgdb, normalizedName, true, 0);
        }

        string? bestIdIgdb = null;
        string? bestMatchName = null;
        int bestLengthDiff = int.MaxValue;

        foreach (var (normalizedName, idIgdb) in index)
        {
            string nameLower = normalizedName.ToLowerInvariant();

            if (!normalizedInput.Contains(nameLower) && !nameLower.Contains(normalizedInput))
                continue;

            int lengthDiff = Math.Abs(normalizedInput.Length - nameLower.Length);
            int maxLength = Math.Max(normalizedInput.Length, nameLower.Length);

            if (maxLength == 0 || (double)lengthDiff / maxLength > MaxUnexplainedRatio)
                continue;

            if (lengthDiff < bestLengthDiff)
            {
                bestLengthDiff = lengthDiff;
                bestIdIgdb = idIgdb;
                bestMatchName = normalizedName;
            }
        }

        return bestIdIgdb != null
            ? (bestIdIgdb, bestMatchName, false, bestLengthDiff)
            : (null, null, false, int.MaxValue);
    }
}
