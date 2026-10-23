using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PastaGBIcons;

// Regression checks for the optimizations' decision boundaries. Watch tests
// execute the real filesystem tracker on the current OS, not Windows UI.
internal static class CoordinatorPerformanceTests
{
    private static int assertions;
    private static int Main()
    {
        try
        {
            CoverageDecisionsStayEquivalent();
            PathBoundariesStayEquivalent();
            RepeatedDemandStillRespondsToChanges();
            Console.WriteLine("PASS: " + assertions + " coordinator performance regression assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }

    private static void CoverageDecisionsStayEquivalent()
    {
        DateTime now = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        double[] ages = { -1, 0, 59.999, 60, 60.001, 299.999, 300, 599.999, 600, 1000 };
        int[] intervals = { 0, 60, 300, 600 };
        foreach (SizePhase phase in Enum.GetValues(typeof(SizePhase)))
            foreach (double age in ages)
                foreach (int interval in intervals)
                    foreach (bool delayed in new[] { false, true })
                    {
                        SizeRecord record = new SizeRecord { Path = @"C:\Fixture", Bytes = 1, FailureCount = 0,
                            LastResultPartial = false, VolumeStamp = new long[26], Phase = phase, FinishedUtc = now.AddSeconds(-age),
                            NextAttemptUtc = delayed ? now.AddMilliseconds(1) : DateTime.MinValue };
                        bool query = SizeRefreshPolicy.NeedsCoverage(record, now, interval);
                        bool withWatch = SizeRefreshPolicy.Due(record, now, true, interval);
                        bool noWatch = SizeRefreshPolicy.Due(record, now, false, interval);
                        Check(query || withWatch == noWatch, "Skipped coverage must not alter either decision");
                        Check(SizeRefreshPolicy.Due(record, now, query, interval) == withWatch,
                            "Lazy coverage preserves a healthy watch's deadline");
                    }
        Check(!SizeRefreshPolicy.NeedsCoverage(null, now, 300) && SizeRefreshPolicy.Due(null, now, false, 300),
            "First reads do not require a coverage query");
    }

    private static void PathBoundariesStayEquivalent()
    {
        string[] paths = { null, "", @"C:\", @"c:/", @"C:\A", @"C:\A\", @"C:\A\\",
            @"c:/a/file.txt", @"C:\AB", @"C:\A\B", @"D:\A", @"\\?\C:\A\", @"\\host\share\A",
            @"\\host\share\AB", @"\\host\share\A\child", @"C:\日本語", @"C:\日本語\子" };
        foreach (string first in paths)
            foreach (string second in paths)
            {
                Check(PathRelations.Contains(first, second) == OriginalContains(first, second),
                    "Component containment must keep the previous semantics");
                Check(PathRelations.Overlaps(first, second) ==
                    (OriginalContains(first, second) || OriginalContains(second, first)),
                    "Allocation-free overlap must keep symmetric boundary semantics");
            }
        Check(!PathRelations.Affects(@"C:\A", new[] { @"D:\Target" }, @"D:\Target2\file"),
            "A similarly named sibling must not invalidate linked totals");
        Check(PathRelations.Affects(@"C:\A", new[] { @"D:\Target" }, @"D:\Target\file"),
            "External link dependencies still invalidate totals");
    }

    private static bool OriginalContains(string parent, string child)
    {
        parent = PathRelations.Normalize(parent); child = PathRelations.Normalize(child);
        if (parent.Length == 0 || child.Length == 0) return false;
        if (String.Equals(parent, child, StringComparison.OrdinalIgnoreCase)) return true;
        if (!parent.EndsWith("\\", StringComparison.Ordinal)) parent += "\\";
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void RepeatedDemandStillRespondsToChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "PastaGB-demand-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(root, "A"), second = Path.Combine(root, "B");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        try
        {
            using (FolderChangeTracker tracker = new FolderChangeTracker())
            {
                string[] request = { first };
                tracker.Track(request); Until(() => tracker.IsCovered(first), "initial demand covered");
                for (int i = 0; i < 100; i++) tracker.Track(request);
                Check(tracker.IsCovered(first) && tracker.RecursiveWatcherCount == 1,
                    "Unchanged demand keeps the existing healthy watcher");
                request[0] = second;
                tracker.Track(request);
                Until(() => tracker.IsCovered(second) && !tracker.IsCovered(first), "in-place request mutation changes coverage");
                Check(tracker.RecursiveWatcherCount == 1, "Changing demand releases the obsolete watcher");
                tracker.Track(new string[0]);
                Until(() => tracker.RecursiveWatcherCount == 0 && tracker.GuardWatcherCount == 0, "empty demand stops all watchers");
                Check(!tracker.IsCovered(second), "Empty demand does not retain cached coverage");
                tracker.Track(request); Until(() => tracker.IsCovered(second), "same demand after clear is restored");
                bool threw = false;
                try { tracker.Track(FaultySequence(first)); } catch (InvalidOperationException) { threw = true; }
                Check(threw, "Input enumeration failure is visible to the caller");
                tracker.Track(new[] { first });
                Until(() => tracker.IsCovered(first) && !tracker.IsCovered(second), "failed enumeration cannot poison the request cache");
                Check(tracker.RecursiveWatcherCount == 1, "Retry establishes the intended root after enumeration failure");
                // Invalid relative input is rejected before full-path or native
                // filesystem work. It still counts against retained cache memory.
                tracker.Track(new[] { first, new string('x', 270000) });
                Check(tracker.IsCovered(first) && tracker.RecursiveWatcherCount == 1,
                    "Oversized demand preserves normal selection of valid roots");
                FieldInfo candidatesField = typeof(FolderChangeTracker).GetField("lastCandidates", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo snapshotField = typeof(FolderChangeTracker).GetField("hasCandidateSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
                var retained = (IEnumerable<string>)candidatesField.GetValue(tracker);
                Check(retained.Sum(x => x == null ? 0L : x.Length) <= 262144 && !(bool)snapshotField.GetValue(tracker),
                    "Oversized strings are not retained for demand reuse");
                tracker.Track(new[] { second });
                Until(() => tracker.IsCovered(second) && !tracker.IsCovered(first), "ordinary demand works after an oversized request");
                tracker.Track(new[] { first });
                Until(() => tracker.IsCovered(first) && !tracker.IsCovered(second), "ordinary demand can switch back after oversized request");
                Check((bool)snapshotField.GetValue(tracker), "Bounded demand resumes reuse after oversized input");
                string changed = Path.Combine(first, "after.txt");
                Thread.Sleep(100); tracker.Drain();
                for (int i = 0; i < 20; i++) tracker.Track(new[] { first });
                File.WriteAllText(changed, "still monitored");
                bool eventSeen = false;
                Until(delegate { var batch = tracker.Drain(); eventSeen |= batch.ReconcileAll || batch.Paths.Contains(changed); return eventSeen; },
                    "repeated demand still receives real filesystem events");
                Check(eventSeen, "Demand caching does not cache notification state");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static IEnumerable<string> FaultySequence(string first)
    { yield return first; throw new InvalidOperationException("test enumeration failure"); }
    private static void Until(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new Exception(message); Thread.Sleep(10); }
    }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
}
