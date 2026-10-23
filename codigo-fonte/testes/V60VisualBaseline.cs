// Reference algorithms copied verbatim from qa/baseline-v51, except the
// desktop method accessibility. This adapter supplies only the old data shapes.
// It intentionally retains the old O(folders*items) loop and per-capture set.
using System;
using System.Collections.Generic;
using System.Drawing;
using AccessibleItem = PastaGBIcons.DesktopGeometryItem;

internal static class V60VisualBaseline
{
    internal sealed class ResolutionState { internal bool Resolved; }
    internal sealed class ShellEntry
    {
        internal string Name;
        internal bool HasPosition;
        internal Point Position;
        internal string Path;
        internal ResolutionState Resolution = new ResolutionState();
    }
    internal sealed class MetadataIndex
    {
        internal Dictionary<string, List<ShellEntry>> Names =
            new Dictionary<string, List<ShellEntry>>(StringComparer.OrdinalIgnoreCase);
    }

    internal static AccessibleItem FindMatch(ShellEntry entry, List<AccessibleItem> items, int shellNameCount, int dpi)
        {
            AccessibleItem soleName = null, solePosition = null;
            int nameMatches = 0, positionMatches = 0;
            foreach (AccessibleItem candidate in items)
            {
                if (!String.Equals(entry.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)) continue;
                nameMatches++;
                soleName = candidate;
                if (!entry.HasPosition) continue;
                Rectangle near = candidate.HitBounds;
                int tolerance = Math.Max(3, dpi / 24);
                near.Inflate(tolerance, tolerance);
                if (near.Contains(entry.Position)) { positionMatches++; solePosition = candidate; }
            }
            // Position cross-checks prevent using a mixed snapshot after dragging.
            if (entry.HasPosition) return positionMatches == 1 ? solePosition : null;
            return nameMatches == 1 && shellNameCount == 1 ? soleName : null;
        }

    internal static HashSet<string> RelevantNames(MetadataIndex index)
    {
        HashSet<string> relevantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (index != null)
                    foreach (KeyValuePair<string, List<ShellEntry>> name in index.Names)
                        if (name.Value.Count == 1 && (!name.Value[0].Resolution.Resolved ||
                            !String.IsNullOrEmpty(name.Value[0].Path))) relevantNames.Add(name.Key);
        return relevantNames;
    }
}
