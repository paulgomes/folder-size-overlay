using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using PastaGBIcons;

// Linux microbenchmark of the actual managed helpers. It deliberately does not
// time simulated Win32, UIA, GDI, disk enumeration or an Explorer process.
internal static class V60VisualBench
{
    private sealed class Sample
    {
        internal double Milliseconds;
        internal long Allocated;
        internal long Digest;
    }

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly List<string> json = new List<string>();
    private static string[] names;
    private static V60VisualBaseline.MetadataIndex metadata;

    private static IEnumerable<string> EligibleNames()
    {
        foreach (KeyValuePair<string, List<V60VisualBaseline.ShellEntry>> pair in metadata.Names)
            if (pair.Value.Count == 1 && (!pair.Value[0].Resolution.Resolved ||
                !String.IsNullOrEmpty(pair.Value[0].Path))) yield return pair.Key;
    }

    private static Sample Measure(Func<long> action)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch clock = Stopwatch.StartNew();
        long digest = action();
        clock.Stop();
        return new Sample { Milliseconds = clock.Elapsed.TotalMilliseconds,
            Allocated = GC.GetAllocatedBytesForCurrentThread() - allocated, Digest = digest };
    }

    private static void Compare(string name, int iterations,
        Func<Func<long>> beforeFactory, Func<Func<long>> afterFactory)
    {
        long warmBefore = beforeFactory()(), warmAfter = afterFactory()();
        if (warmBefore != warmAfter) throw new Exception("Warmup digest mismatch: " + name);
        var before = new List<Sample>(); var after = new List<Sample>();
        for (int trial = 0; trial < 7; trial++)
        {
            Sample first, second;
            if (trial % 2 == 0)
            { first = Measure(beforeFactory()); second = Measure(afterFactory()); }
            else
            { second = Measure(afterFactory()); first = Measure(beforeFactory()); }
            if (first.Digest != second.Digest) throw new Exception("Result mismatch: " + name);
            before.Add(first); after.Add(second);
        }
        double[] beforeMs = new double[7], afterMs = new double[7];
        long[] beforeBytes = new long[7], afterBytes = new long[7];
        for (int i = 0; i < 7; i++)
        {
            beforeMs[i] = before[i].Milliseconds; afterMs[i] = after[i].Milliseconds;
            beforeBytes[i] = before[i].Allocated; afterBytes[i] = after[i].Allocated;
        }
        Array.Sort(beforeMs); Array.Sort(afterMs); Array.Sort(beforeBytes); Array.Sort(afterBytes);
        json.Add("{\"case\":\"" + name + "\",\"iterations\":" + iterations +
            ",\"trials\":7,\"baseline_median_ms\":" + beforeMs[3].ToString("F4", Culture) +
            ",\"current_median_ms\":" + afterMs[3].ToString("F4", Culture) +
            ",\"baseline_median_allocated_bytes\":" + beforeBytes[3] +
            ",\"current_median_allocated_bytes\":" + afterBytes[3] +
            ",\"digest\":" + before[0].Digest + "}");
    }

    private static Func<long> NamePass(bool optimized, int captures, bool cold, bool changed)
    {
        foreach (List<V60VisualBaseline.ShellEntry> entry in metadata.Names.Values)
            entry[0].Resolution.Resolved = false;
        ProbeNameIndex index = optimized && !cold ? new ProbeNameIndex(EligibleNames(), metadata.Names.Count) : null;
        if (index != null) index.Capture();
        var queries = new string[200];
        for (int i = 0; i < queries.Length; i++) queries[i] = names[(i * 431) % names.Length];
        return delegate() {
            long digest = 0;
            for (int frame = 0; frame < captures; frame++)
            {
                if (changed)
                    for (int file = frame * 10; file < (frame + 1) * 10; file++)
                    {
                        metadata.Names[names[file]][0].Resolution.Resolved = true;
                        if (optimized) index.Exclude(names[file]);
                    }
                if (optimized)
                {
                    if (cold) index = new ProbeNameIndex(EligibleNames(), metadata.Names.Count);
                    ProbeNameSnapshot snapshot = index.Capture();
                    foreach (string query in queries) if (snapshot.Contains(query)) digest++;
                }
                else
                {
                    HashSet<string> snapshot = V60VisualBaseline.RelevantNames(metadata);
                    foreach (string query in queries) if (snapshot.Contains(query)) digest++;
                }
            }
            return digest;
        };
    }

    private static Func<long> DesktopPass(bool optimized, int captures, int itemCount, int folderCount)
    {
        var rows = new List<DesktopGeometryItem>();
        var entries = new List<V60VisualBaseline.ShellEntry>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < itemCount; i++)
        {
            string name = "Item " + i.ToString("D5");
            Rectangle bounds = new Rectangle(i % 40 * 100, i / 40 * 100, 85, 90);
            rows.Add(new DesktopGeometryItem { Name = name, Bounds = bounds, HitBounds = bounds });
            if (i % (itemCount / folderCount) == 0 && entries.Count < folderCount)
            {
                entries.Add(new V60VisualBaseline.ShellEntry { Name = name, HasPosition = true,
                    Position = bounds.Location, Path = @"C:\Bench\" + name });
                folders.Add(name);
            }
        }
        rows.Reverse();
        return delegate() {
            long digest = 0;
            for (int frame = 0; frame < captures; frame++)
            {
                DesktopItemIndex index = optimized ? new DesktopItemIndex(rows, folders) : null;
                foreach (V60VisualBaseline.ShellEntry entry in entries)
                {
                    DesktopGeometryItem found = optimized ? index.Match(entry.Name, true, entry.Position, 1, 144) :
                        V60VisualBaseline.FindMatch(entry, rows, 1, 144);
                    if (found != null) digest += 1 + found.Bounds.Left + found.Bounds.Top;
                }
            }
            return digest;
        };
    }

    private static void Main()
    {
        names = new string[100000]; metadata = new V60VisualBaseline.MetadataIndex();
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = "Folder " + i.ToString("D6");
            metadata.Names.Add(names[i], new List<V60VisualBaseline.ShellEntry> {
                new V60VisualBaseline.ShellEntry { Name = names[i] } });
        }
        Compare("explorer_cold_index_100000_sorted_names_200_lookups", 6,
            () => NamePass(false, 6, true, false), () => NamePass(true, 6, true, false));
        // A shuffled Shell index exercises the sort rather than relying on
        // alphabetical enumeration, which is common but not guaranteed.
        var order = new List<KeyValuePair<string, List<V60VisualBaseline.ShellEntry>>>(metadata.Names);
        var random = new Random(600051);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int other = random.Next(i + 1);
            KeyValuePair<string, List<V60VisualBaseline.ShellEntry>> saved = order[i];
            order[i] = order[other]; order[other] = saved;
        }
        metadata.Names = new Dictionary<string, List<V60VisualBaseline.ShellEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<V60VisualBaseline.ShellEntry>> pair in order) metadata.Names.Add(pair.Key, pair.Value);
        Compare("explorer_cold_index_100000_shuffled_names_200_lookups", 6,
            () => NamePass(false, 6, true, false), () => NamePass(true, 6, true, false));
        Compare("explorer_stable_100000_names_200_lookups", 120,
            () => NamePass(false, 120, false, false), () => NamePass(true, 120, false, false));
        Compare("explorer_10_new_exclusions_per_frame_100000_names", 60,
            () => NamePass(false, 60, false, true), () => NamePass(true, 60, false, true));
        Compare("desktop_1000_items_200_folders_including_index_build", 60,
            () => DesktopPass(false, 60, 1000, 200), () => DesktopPass(true, 60, 1000, 200));
        Compare("desktop_50_items_10_folders_including_index_build", 300,
            () => DesktopPass(false, 300, 50, 10), () => DesktopPass(true, 300, 50, 10));
        Console.WriteLine("{\"scope\":\"Managed helper microbenchmark on .NET8/Linux. No Windows UIA, GDI, native UI, disk I/O or whole-application CPU measurement.\"," +
            "\"baseline\":\"5.1 algorithms copied without logic changes from qa/baseline-v51\",\"results\":[" +
            String.Join(",", json.ToArray()) + "]}");
    }
}
