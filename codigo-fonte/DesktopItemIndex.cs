using System;
using System.Collections.Generic;
using System.Drawing;

namespace PastaGBIcons
{
    internal sealed class DesktopGeometryItem
    {
        internal string Name;
        internal Rectangle Bounds;
        internal Rectangle HitBounds;
        internal bool Selected;
        internal bool Editing;
    }

    // A capture-local index. It never outlives the observed geometry and never
    // chooses a folder by name alone when the Shell supplied a position.
    internal sealed class DesktopItemIndex
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        private static readonly IComparer<DesktopGeometryItem> Order = new NameOrder();
        private readonly DesktopGeometryItem[] candidates;

        internal DesktopItemIndex(IList<DesktopGeometryItem> items, ISet<string> relevantNames)
        {
            if (items == null) throw new ArgumentNullException("items");
            var values = new List<DesktopGeometryItem>(relevantNames == null ? items.Count :
                Math.Min(items.Count, relevantNames.Count));
            foreach (DesktopGeometryItem item in items)
            {
                if (item != null && !String.IsNullOrEmpty(item.Name) &&
                    (relevantNames == null || relevantNames.Contains(item.Name))) values.Add(item);
            }
            candidates = values.ToArray();
            Array.Sort(candidates, Order);
        }

        internal DesktopGeometryItem Match(string name, bool hasPosition, Point position,
                                           int shellNameCount, int dpi)
        {
            if (String.IsNullOrEmpty(name)) return null;
            int low = 0, high = candidates.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (Names.Compare(candidates[middle].Name, name) < 0) low = middle + 1;
                else high = middle;
            }
            if (low >= candidates.Length || !Names.Equals(candidates[low].Name, name)) return null;
            if (!hasPosition)
                return shellNameCount == 1 && (low + 1 == candidates.Length ||
                    !Names.Equals(candidates[low + 1].Name, name)) ? candidates[low] : null;
            DesktopGeometryItem match = null;
            int tolerance = Math.Max(3, dpi / 24);
            for (int index = low; index < candidates.Length && Names.Equals(candidates[index].Name, name); index++)
            {
                DesktopGeometryItem candidate = candidates[index];
                Rectangle near = candidate.HitBounds;
                near.Inflate(tolerance, tolerance);
                if (!near.Contains(position)) continue;
                if (match != null) return null; // duplicate position remains ambiguous
                match = candidate;
            }
            return match;
        }

        private sealed class NameOrder : IComparer<DesktopGeometryItem>
        { public int Compare(DesktopGeometryItem left, DesktopGeometryItem right) { return Names.Compare(left.Name, right.Name); } }
    }
}
