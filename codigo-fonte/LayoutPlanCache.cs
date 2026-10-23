using System;
using System.Collections.Generic;
using System.Drawing;

namespace PastaGBIcons
{
    internal struct LabelPlacement
    {
        internal Rectangle Bounds;
        internal bool Compact, AlignRight;
        internal string SuppressionReason;
        internal string LabelKey;
        internal bool Placed { get { return SuppressionReason == null && Bounds.Width > 0 && Bounds.Height > 0; } }
    }

    // UI-thread only. Reuse collision decisions, never a license to draw: the
    // renderer must still validate current host, geometry, media and policy.
    // All placements reserve their rectangle even if one native label fails,
    // so another label cannot jump into its place and overlap on recovery.
    internal sealed class LayoutPlanCache
    {
        private const int MaximumSurfaces = 16, MaximumItems = 2048;
        private const long MaximumEstimatedBytes = 2L * 1024 * 1024;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private int retainedItems;
        private long estimatedBytes, hits, misses;

        private struct Anchor
        {
            internal string Path;
            internal Rectangle Bounds, ItemBounds;
            internal bool Editing;
            internal bool Matches(DesktopFolderIcon folder)
            {
                return folder != null && String.Equals(Path, folder.Path, StringComparison.Ordinal) &&
                    Bounds == folder.Bounds && ItemBounds == folder.ItemBounds && Editing == folder.IsEditing;
            }
        }

        private sealed class Entry
        {
            internal string Stamp;
            internal uint Mode;
            internal float TextScale;
            internal Anchor[] Anchors;
            internal LabelPlacement[] Placements;
            internal DateTime LastUsedUtc;
            internal long EstimatedBytes;

            internal bool Matches(string stamp, uint mode, float textScale, IList<DesktopFolderIcon> folders)
            {
                if (Stamp != stamp || Mode != mode || TextScale != textScale || Anchors.Length != folders.Count) return false;
                // Geometry stamps intentionally ignore provider order. The plan
                // is indexed in provider order, so explicitly verify that too.
                for (int i = 0; i < Anchors.Length; i++) if (!Anchors[i].Matches(folders[i])) return false;
                return true;
            }
        }

        internal int Count { get { return entries.Count; } }
        internal int RetainedItems { get { return retainedItems; } }
        internal long EstimatedBytes { get { return estimatedBytes; } }
        internal long Hits { get { return hits; } }
        internal long Misses { get { return misses; } }

        internal LabelPlacement[] GetOrCreate(string surfaceKey, string stamp, uint mode, float textScale,
            IList<DesktopFolderIcon> folders, DateTime now,
            Func<DesktopFolderIcon, IList<Rectangle>, LabelPlacement> place)
        {
            if (folders == null) throw new ArgumentNullException("folders");
            if (place == null) throw new ArgumentNullException("place");
            textScale = LabelLayout.NormalizeTextScale(textScale);
            Entry previous;
            bool cacheable = !String.IsNullOrEmpty(surfaceKey) && !String.IsNullOrEmpty(stamp);
            if (cacheable && entries.TryGetValue(surfaceKey, out previous) && previous.Matches(stamp, mode, textScale, folders))
            {
                previous.LastUsedUtc = now; hits++;
                return previous.Placements;
            }
            misses++;
            var placements = new LabelPlacement[folders.Count];
            var used = new List<Rectangle>(folders.Count);
            long size = 256L + (surfaceKey == null ? 0 : surfaceKey.Length * 2L) + (stamp == null ? 0 : stamp.Length * 2L);
            for (int i = 0; i < folders.Count; i++)
            {
                DesktopFolderIcon folder = folders[i];
                if (folder == null) placements[i].SuppressionReason = "identidade não confirmada";
                else placements[i] = place(folder, used);
                if (placements[i].Placed)
                {
                    used.Add(placements[i].Bounds);
                    placements[i].LabelKey = surfaceKey + "\u001F" + folder.Path;
                }
                size += 224L + (folder == null || folder.Path == null ? 0 : folder.Path.Length * 4L) +
                    (surfaceKey == null ? 0 : surfaceKey.Length * 2L);
            }
            if (!cacheable) return placements;
            Remove(surfaceKey);
            if (folders.Count > MaximumItems || size > MaximumEstimatedBytes) return placements;
            while (entries.Count >= MaximumSurfaces || retainedItems + folders.Count > MaximumItems || estimatedBytes + size > MaximumEstimatedBytes)
            {
                string oldestKey = null; DateTime oldest = DateTime.MaxValue;
                foreach (var pair in entries)
                    if (oldestKey == null || pair.Value.LastUsedUtc < oldest)
                    { oldestKey = pair.Key; oldest = pair.Value.LastUsedUtc; }
                if (oldestKey == null) return placements;
                Remove(oldestKey);
            }
            var anchors = new Anchor[folders.Count];
            for (int i = 0; i < folders.Count; i++)
            {
                DesktopFolderIcon folder = folders[i];
                if (folder != null) anchors[i] = new Anchor { Path = folder.Path, Bounds = folder.Bounds,
                    ItemBounds = folder.ItemBounds, Editing = folder.IsEditing };
            }
            entries.Add(surfaceKey, new Entry { Stamp = stamp, Mode = mode, TextScale = textScale,
                Anchors = anchors, Placements = placements, LastUsedUtc = now, EstimatedBytes = size });
            retainedItems += folders.Count; estimatedBytes += size;
            return placements;
        }

        internal void Trim(DateTime now)
        {
            // At most sixteen entries: avoid allocating a temporary list every
            // render frame; removal is done after each enumerator has finished.
            while (true)
            {
                string stale = null;
                foreach (var pair in entries)
                    if ((now - pair.Value.LastUsedUtc).TotalSeconds > 30) { stale = pair.Key; break; }
                if (stale == null) break;
                Remove(stale);
            }
        }

        private void Remove(string key)
        {
            Entry value;
            if (!entries.TryGetValue(key, out value)) return;
            retainedItems -= value.Anchors.Length; estimatedBytes -= value.EstimatedBytes;
            entries.Remove(key);
        }

        internal void Clear() { entries.Clear(); retainedItems = 0; estimatedBytes = 0; }
    }
}
