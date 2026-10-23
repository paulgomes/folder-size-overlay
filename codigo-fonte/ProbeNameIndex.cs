using System;
using System.Collections.Generic;

namespace PastaGBIcons
{
    // The Shell index is owned by one STA. Its immutable name snapshots may be
    // read by the bounded UIA MTA even after a caller timeout or index eviction.
    // Never clear or edit an array already handed to a snapshot.
    internal sealed class ProbeNameIndex
    {
        internal const int MaximumNames = 100000;
        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        private readonly string[] names;
        private readonly ulong[] excluded;
        private ProbeNameSnapshot snapshot;
        private bool changed = true;
        internal int Count { get { return names.Length; } }

        internal ProbeNameIndex(IEnumerable<string> source, int capacityHint = 0)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (capacityHint < 0 || capacityHint > MaximumNames)
                throw new ArgumentOutOfRangeException("capacityHint");
            ICollection<string> collection = source as ICollection<string>;
            int capacity = capacityHint != 0 ? capacityHint :
                collection == null ? 0 : Math.Min(MaximumNames, collection.Count);
            var values = new List<string>(capacity);
            bool sorted = true;
            foreach (string name in source)
            {
                if (String.IsNullOrEmpty(name)) continue;
                if (values.Count >= MaximumNames)
                    throw new ArgumentOutOfRangeException("source", "A visualização excedeu o limite de nomes.");
                if (sorted && values.Count != 0 && Comparer.Compare(values[values.Count - 1], name) > 0)
                    sorted = false;
                values.Add(name);
            }
            names = values.ToArray();
            // Shell enumeration is often already alphabetical. Verify that
            // once while copying instead of sorting the same 100K-name order.
            if (!sorted) Array.Sort(names, Comparer);
            // A duplicate name is ambiguous, not a name to silently deduplicate.
            int write = 0;
            for (int read = 0; read < names.Length;)
            {
                int next = read + 1;
                while (next < names.Length && Comparer.Equals(names[read], names[next])) next++;
                if (next == read + 1) names[write++] = names[read];
                read = next;
            }
            if (write != names.Length) Array.Resize(ref names, write);
            excluded = new ulong[(names.Length + 63) / 64];
        }

        // Conservative retained estimate: array headers, name references and
        // both the mutable exclusion mask and one immutable mask. Strings are
        // already charged to the Shell metadata index and are shared here.
        internal static long EstimateStorage(int nameCount)
        {
            if (nameCount < 0 || nameCount > MaximumNames) throw new ArgumentOutOfRangeException("nameCount");
            return 256L + nameCount * (long)IntPtr.Size + ((nameCount + 63L) / 64L) * 16L;
        }

        internal void Exclude(string name)
        {
            if (String.IsNullOrEmpty(name)) return;
            int index = Array.BinarySearch(names, name, Comparer);
            if (index < 0) return;
            ulong bit = 1UL << (index & 63);
            int word = index >> 6;
            if ((excluded[word] & bit) != 0) return;
            excluded[word] |= bit;
            changed = true;
        }

        internal ProbeNameSnapshot Capture()
        {
            if (snapshot == null || changed)
            {
                snapshot = names.Length == 0 ? ProbeNameSnapshot.Empty :
                    new ProbeNameSnapshot(names, (ulong[])excluded.Clone());
                changed = false;
            }
            return snapshot;
        }
    }

    internal sealed class ProbeNameSnapshot
    {
        internal static readonly ProbeNameSnapshot Empty =
            new ProbeNameSnapshot(new string[0], new ulong[0]);
        private readonly string[] names;
        private readonly ulong[] excluded;

        // Arrays are owned by ProbeNameIndex and never mutated after this call.
        internal ProbeNameSnapshot(string[] names, ulong[] excluded)
        { this.names = names; this.excluded = excluded; }

        internal bool Contains(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            int index = Array.BinarySearch(names, name, StringComparer.OrdinalIgnoreCase);
            return index >= 0 && (excluded[index >> 6] & (1UL << (index & 63))) == 0;
        }
    }
}
