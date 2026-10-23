using System;
using System.Collections.Generic;
using System.Drawing;
using System.Security.Cryptography;

namespace PastaGBIcons
{
    internal struct VisualRowAnchor : IEquatable<VisualRowAnchor>
    {
        internal string Name;
        internal Rectangle Bounds;
        internal bool Editor;

        public bool Equals(VisualRowAnchor other)
        { return Bounds == other.Bounds && Editor == other.Editor && String.Equals(Name, other.Name, StringComparison.Ordinal); }
        public override bool Equals(object value)
        { return value is VisualRowAnchor && Equals((VisualRowAnchor)value); }
        public override int GetHashCode()
        {
            unchecked { return ((Name == null ? 0 : Name.GetHashCode()) * 397 ^ Bounds.GetHashCode()) * 397 ^ Editor.GetHashCode(); }
        }
    }

    internal static class VisualSnapshotPolicy
    {
        internal static List<int> WindowVisitOrder(int count, int cursor, int priority)
        {
            if (count < 0 || count > 128) throw new ArgumentOutOfRangeException("count");
            List<int> result = new List<int>(count);
            if (count == 0) return result;
            if (priority >= 0 && priority < count) result.Add(priority);
            int first = ((cursor % count) + count) % count;
            for (int offset = 0; offset < count; offset++)
            {
                int index = (first + offset) % count;
                if (index != priority) result.Add(index);
            }
            return result;
        }

        internal static string CreateStamp(IntPtr host, Rectangle viewport, int dpi,
                                           IList<VisualRowAnchor> rows)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            // Sort value records, not newly formatted strings. The original
            // capture stays immutable and duplicate multiplicity is preserved.
            VisualRowAnchor[] ordered = new VisualRowAnchor[rows.Count];
            rows.CopyTo(ordered, 0);
            Array.Sort(ordered, RowComparer.Instance);
            using (StampWriter writer = new StampWriter(ordered.Length))
            {
                writer.WriteInt32(1); // Internal binary schema, not persisted.
                writer.WriteInt64(host.ToInt64());
                writer.WriteRectangle(viewport);
                writer.WriteInt32(dpi);
                writer.WriteInt32(ordered.Length);
                foreach (VisualRowAnchor row in ordered)
                {
                    // Length + raw UTF-16 code units prevent delimiter or lossy
                    // encoder collisions, including unmatched surrogates.
                    writer.WriteString(row.Name ?? String.Empty);
                    writer.WriteRectangle(row.Bounds);
                    writer.WriteInt32(row.Editor ? 1 : 0);
                }
                return writer.Complete();
            }
        }

        private sealed class RowComparer : IComparer<VisualRowAnchor>
        {
            internal static readonly RowComparer Instance = new RowComparer();
            public int Compare(VisualRowAnchor first, VisualRowAnchor second)
            {
                int value = StringComparer.Ordinal.Compare(first.Name ?? String.Empty, second.Name ?? String.Empty);
                if (value != 0) return value;
                value = first.Bounds.X.CompareTo(second.Bounds.X); if (value != 0) return value;
                value = first.Bounds.Y.CompareTo(second.Bounds.Y); if (value != 0) return value;
                value = first.Bounds.Width.CompareTo(second.Bounds.Width); if (value != 0) return value;
                value = first.Bounds.Height.CompareTo(second.Bounds.Height); if (value != 0) return value;
                return first.Editor.CompareTo(second.Editor);
            }
        }

        // A staging buffer capped at 4 KiB replaces the per-row strings, large
        // StringBuilder, complete character array and complete payload array.
        // Each capture owns its SHA instance, so desktop and Explorer workers
        // never share mutable hash state or retain a large previous frame.
        private sealed class StampWriter : IDisposable
        {
            private static readonly byte[] Empty = new byte[0];
            private const string Hex = "0123456789ABCDEF";
            private readonly SHA256 sha = SHA256.Create();
            private readonly byte[] buffer;
            private int used;

            internal StampWriter(int rows)
            { buffer = new byte[rows >= 64 ? 4096 : Math.Max(128, rows * 64)]; }

            private void Ensure(int count)
            {
                if (buffer.Length - used >= count) return;
                sha.TransformBlock(buffer, 0, used, buffer, 0);
                used = 0;
            }

            internal void WriteInt32(int value)
            {
                Ensure(4);
                unchecked
                {
                    buffer[used++] = (byte)value;
                    buffer[used++] = (byte)(value >> 8);
                    buffer[used++] = (byte)(value >> 16);
                    buffer[used++] = (byte)(value >> 24);
                }
            }

            internal void WriteInt64(long value)
            { WriteInt32(unchecked((int)value)); WriteInt32(unchecked((int)(value >> 32))); }

            internal void WriteRectangle(Rectangle value)
            { WriteInt32(value.X); WriteInt32(value.Y); WriteInt32(value.Width); WriteInt32(value.Height); }

            internal void WriteString(string value)
            {
                WriteInt32(value.Length);
                for (int i = 0; i < value.Length; i++)
                {
                    Ensure(2);
                    buffer[used++] = unchecked((byte)value[i]);
                    buffer[used++] = (byte)(value[i] >> 8);
                }
            }

            internal string Complete()
            {
                if (used != 0) sha.TransformBlock(buffer, 0, used, buffer, 0);
                sha.TransformFinalBlock(Empty, 0, 0);
                byte[] hash = sha.Hash;
                char[] result = new char[hash.Length * 2];
                for (int i = 0; i < hash.Length; i++)
                { result[i * 2] = Hex[hash[i] >> 4]; result[i * 2 + 1] = Hex[hash[i] & 15]; }
                return new String(result);
            }

            public void Dispose() { sha.Dispose(); }
        }

        // Compare a multiset, not provider enumeration order. This rejects scroll,
        // rename, deletion, new editors and movement of uniquely named rows,
        // while harmless enumeration-order jitter preserves a stable frame.
        internal static bool SameRows(IList<VisualRowAnchor> before, IList<VisualRowAnchor> after)
        {
            if (before == null || after == null || before.Count != after.Count) return false;
            // The normal case is an unchanged provider enumeration. Compare it
            // directly without allocating a hash table on every polling pass.
            int firstDifference = 0;
            while (firstDifference < before.Count && before[firstDifference].Equals(after[firstDifference]))
                firstDifference++;
            if (firstDifference == before.Count) return true;
            // Equal prefix occurrences cancel in the multiset. Preserve the
            // full fallback for reordering, duplicates and genuine changes.
            Dictionary<VisualRowAnchor, int> counts = new Dictionary<VisualRowAnchor, int>(before.Count - firstDifference);
            for (int i = firstDifference; i < before.Count; i++)
            {
                VisualRowAnchor row = before[i];
                int count;
                counts.TryGetValue(row, out count);
                counts[row] = count + 1;
            }
            for (int i = firstDifference; i < after.Count; i++)
            {
                VisualRowAnchor row = after[i];
                int count;
                if (!counts.TryGetValue(row, out count) || count <= 0) return false;
                if (count == 1) counts.Remove(row); else counts[row] = count - 1;
            }
            return counts.Count == 0;
        }
    }
}
