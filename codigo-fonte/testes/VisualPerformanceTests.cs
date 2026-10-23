using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PastaGBIcons;

internal static class VisualPerformanceTests
{
    private static int assertions;
    private static int sink;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTime { internal long Seconds; internal long Nanoseconds; }
    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ReadClock(int clock, out NativeTime time);

    private static double CpuMilliseconds()
    {
        // /proc is unavailable in some sandboxed Linux runners; the process CPU
        // clock does not require that filesystem. Benchmark binaries run here
        // under a 64-bit .NET8 runtime; production never references this API.
        if (Environment.OSVersion.Platform == PlatformID.Unix && IntPtr.Size == 8)
        {
            NativeTime time;
            if (ReadClock(2, out time) != 0) throw new InvalidOperationException("Cannot read process CPU clock");
            return time.Seconds * 1000d + time.Nanoseconds / 1000000d;
        }
        using (Process process = Process.GetCurrentProcess()) return process.TotalProcessorTime.TotalMilliseconds;
    }

    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); assertions++; }

    private static VisualRowAnchor Row(string name, int x, int y, bool editor)
    { return new VisualRowAnchor { Name = name, Bounds = new Rectangle(x, y, 94, 22), Editor = editor }; }

    private static int Compare(VisualRowAnchor a, VisualRowAnchor b)
    {
        int value = StringComparer.Ordinal.Compare(a.Name ?? String.Empty, b.Name ?? String.Empty);
        if (value == 0) value = a.Bounds.X.CompareTo(b.Bounds.X);
        if (value == 0) value = a.Bounds.Y.CompareTo(b.Bounds.Y);
        if (value == 0) value = a.Bounds.Width.CompareTo(b.Bounds.Width);
        if (value == 0) value = a.Bounds.Height.CompareTo(b.Bounds.Height);
        if (value == 0) value = a.Editor.CompareTo(b.Editor);
        return value;
    }

    // Independent whole-buffer reference checks the new incremental binary
    // serialization at every flush boundary, without using production helpers.
    private static string ReferenceStamp(IntPtr host, Rectangle viewport, int dpi, IList<VisualRowAnchor> rows)
    {
        List<VisualRowAnchor> ordered = new List<VisualRowAnchor>(rows);
        ordered.Sort(Compare);
        using (MemoryStream stream = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.Unicode, true))
            {
                writer.Write(1); writer.Write(host.ToInt64());
                writer.Write(viewport.X); writer.Write(viewport.Y); writer.Write(viewport.Width); writer.Write(viewport.Height);
                writer.Write(dpi); writer.Write(ordered.Count);
                foreach (VisualRowAnchor row in ordered)
                {
                    string name = row.Name ?? String.Empty;
                    writer.Write(name.Length);
                    foreach (char c in name) writer.Write((ushort)c);
                    writer.Write(row.Bounds.X); writer.Write(row.Bounds.Y);
                    writer.Write(row.Bounds.Width); writer.Write(row.Bounds.Height);
                    writer.Write(row.Editor ? 1 : 0);
                }
            }
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", String.Empty);
        }
    }

    private static bool ReferenceSameRows(IList<VisualRowAnchor> first, IList<VisualRowAnchor> second)
    {
        if (first == null || second == null || first.Count != second.Count) return false;
        bool[] consumed = new bool[second.Count];
        foreach (VisualRowAnchor item in first)
        {
            bool found = false;
            for (int i = 0; i < second.Count; i++)
                if (!consumed[i] && item.Equals(second[i])) { consumed[i] = true; found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    private static void Correctness()
    {
        IntPtr host = new IntPtr(-1234567);
        Rectangle viewport = new Rectangle(Int32.MinValue, -1080, Int32.MaxValue, 1080);
        int[] lengths = { 0, 1, 2025, 2026, 2027, 2047, 2048, 2049, 4095, 4096, 8193 };
        foreach (int length in lengths)
        {
            var rows = new[] { Row(new String('\ud800', length), -1920, 0, true),
                Row("A\0:B|C,\r\n\udfff", 1, Int32.MinValue, false), Row(null, 2, 1, false), Row(String.Empty, 2, 1, false) };
            Check(VisualSnapshotPolicy.CreateStamp(host, viewport, 144, rows) == ReferenceStamp(host, viewport, 144, rows),
                  "Binary stamp preserves raw UTF-16 and flush boundaries at length " + length);
            Check(rows[0].Name.Length == length && rows[1].Name.StartsWith("A"), "Stamp does not reorder caller-owned rows");
        }
        var empty = new VisualRowAnchor[0];
        Check(VisualSnapshotPolicy.CreateStamp(host, viewport, 96, empty) == ReferenceStamp(host, viewport, 96, empty),
              "Empty frame includes host, viewport, DPI and count");
        var first = new[] { Row("same", 1, 1, false), Row("same", 1, 1, false), Row("other", 1, 1, false) };
        var changed = new[] { first[0], first[2], first[2] };
        Check(!VisualSnapshotPolicy.SameRows(first, changed), "Equal prefix cannot hide changed duplicate multiplicity");
        Check(VisualSnapshotPolicy.SameRows(first, new[] { first[0], first[2], first[1] }),
              "Equal prefix can be cancelled before reordered suffix comparison");
        Check(!VisualSnapshotPolicy.SameRows(null, empty) && !VisualSnapshotPolicy.SameRows(empty, null), "Missing snapshots rejected");
        Check(VisualSnapshotPolicy.SameRows(empty, empty), "Empty ordered fast path accepted");
        Random random = new Random(20261020);
        for (int sample = 0; sample < 400; sample++)
        {
            List<VisualRowAnchor> a = new List<VisualRowAnchor>();
            for (int i = 0, count = random.Next(1, 35); i < count; i++)
                a.Add(Row("Pasta " + random.Next(5), random.Next(4), random.Next(3), random.Next(5) == 0));
            List<VisualRowAnchor> b = new List<VisualRowAnchor>(a);
            int swaps = random.Next(10);
            for (int i = 0; i < swaps; i++)
            {
                int left = random.Next(b.Count), right = random.Next(b.Count);
                VisualRowAnchor value = b[left]; b[left] = b[right]; b[right] = value;
            }
            if (sample % 3 == 0)
                b[random.Next(b.Count)] = Row("mutated", random.Next(4), random.Next(3), false);
            Check(VisualSnapshotPolicy.SameRows(a, b) == ReferenceSameRows(a, b), "Multiset reference sample " + sample);
            if (sample % 3 != 0)
                Check(VisualSnapshotPolicy.CreateStamp(host, viewport, 96, a) == VisualSnapshotPolicy.CreateStamp(host, viewport, 96, b),
                      "Binary canonical order sample " + sample);
        }
        Console.WriteLine("{\"suite\":\"visual-performance-regressions\",\"passed\":true,\"assertions\":" + assertions + "}");
    }

    private static void Measure(string name, int iterations, Action action)
    {
        for (int i = 0; i < 40; i++) action();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        {
            Stopwatch clock = new Stopwatch();
            double cpu = CpuMilliseconds();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            clock.Start();
            for (int i = 0; i < iterations; i++) action();
            clock.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double elapsedCpu = CpuMilliseconds() - cpu;
            Console.WriteLine("{\"case\":\"" + name + "\",\"iterations\":" + iterations +
                ",\"elapsed_ms\":" + clock.Elapsed.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture) +
                ",\"cpu_ms\":" + elapsedCpu.ToString("0.000", CultureInfo.InvariantCulture) +
                ",\"allocated_bytes\":" + bytes + "}");
        }
    }

    private static void Benchmark()
    {
        var rows = new VisualRowAnchor[1000];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = Row("\0folder:C:\\Dados\\Projeto " + (i % 400) + "\0Documentação " + i,
                -1920 + i % 20 * 90, i / 20 * 25, i % 17 == 0);
        var same = (VisualRowAnchor[])rows.Clone();
        var reverse = (VisualRowAnchor[])rows.Clone(); Array.Reverse(reverse);
        Measure("stamp_1000_anchors", 250, delegate {
            sink ^= VisualSnapshotPolicy.CreateStamp(new IntPtr(1234), new Rectangle(-1920, 0, 1920, 1080), 144, rows).GetHashCode();
        });
        Measure("reconcile_1000_ordered_anchors", 2000, delegate { if (VisualSnapshotPolicy.SameRows(rows, same)) sink++; });
        Measure("reconcile_1000_reordered_anchors", 400, delegate { if (VisualSnapshotPolicy.SameRows(rows, reverse)) sink++; });
        Measure("format_automatic_caption", 100000, delegate { sink ^= SizeCaption.Format(123456789).GetHashCode(); });
        if (sink == Int32.MinValue) Console.Error.WriteLine(sink);
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--benchmark") Benchmark(); else Correctness();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
