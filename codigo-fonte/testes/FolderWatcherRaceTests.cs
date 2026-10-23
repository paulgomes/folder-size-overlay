using System;
using System.IO;
using System.Linq;
using System.Threading;
using PastaGBIcons;

// The dedicated WATCHER_RACE_TESTS symbol enables only the failure-boundary
// hook. Native filesystem notifications and the tracker are otherwise real.
internal static class FolderWatcherRaceTests
{
    private static int assertions;
    private static string fixture;

    private static int Main()
    {
        fixture = Path.Combine(Path.GetTempPath(), "PastaGB-watch-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            NewerCreationBeatsEarlierFailure();
            UnrelatedCreationDoesNotCancelBackoff();
            Console.WriteLine("PASS: " + assertions + " deterministic watcher race assertions; real events on " + Environment.OSVersion.Platform + ".");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { try { Directory.Delete(fixture, true); } catch { } }
    }

    private static void NewerCreationBeatsEarlierFailure()
    {
        string parent = Path.Combine(fixture, "replace-during-failure"); Directory.CreateDirectory(parent);
        string root = Path.Combine(parent, "Root");
        FolderChangeTracker tracker = null;
        int failures = 0, guardObserved = 0;
        Exception hookFailure = null;
        tracker = new FolderChangeTracker(null, 5000, delegate(string path, bool recursive)
        {
            if (!recursive || path != root || Interlocked.Increment(ref failures) != 1) return;
            try
            {
                long before = tracker.Revision;
                // The constructor has already failed for this missing name.
                // Deliver creation through the active real parent guard before
                // that older failure is allowed to schedule its backoff.
                Directory.CreateDirectory(root);
                Until(() => tracker.Revision > before, "creation must reach the parent guard while failure handling is paused");
                Interlocked.Exchange(ref guardObserved, 1);
            }
            catch (Exception ex) { hookFailure = ex; throw; }
        });
        using (tracker)
        {
            tracker.Track(new[] { root });
            Until(() => tracker.IsCovered(root),
                "newer root creation must win over the older failed registration's 30-second backoff", tracker);
            Check(hookFailure == null && Interlocked.CompareExchange(ref guardObserved, 0, 0) == 1,
                "Real guard notification occurred before the stale failure outcome");
            Check(Interlocked.CompareExchange(ref failures, 0, 0) == 1,
                "The replacement root registers without another failed attempt");
            Check(tracker.RecursiveWatcherCount == 1 && tracker.GuardWatcherCount == 1,
                "Race recovery retains only one root and its guard");
            Thread.Sleep(50); tracker.Drain();
            string child = Path.Combine(root, "after.txt"); File.WriteAllText(child, "new root");
            bool seen = false;
            Until(delegate { FolderChangeBatch batch = tracker.Drain(); seen |= batch.Paths.Contains(child); return seen; },
                "Recovered watcher must observe new content at the replacement path", tracker);
            Check(seen, "Recovered watch is attached to the current root");
        }
    }

    private static void UnrelatedCreationDoesNotCancelBackoff()
    {
        string parent = Path.Combine(fixture, "independent-paths"); Directory.CreateDirectory(parent);
        string missing = Path.Combine(parent, "Missing"), other = Path.Combine(parent, "Other");
        FolderChangeTracker tracker = null;
        int missingFailures = 0, guardObserved = 0;
        tracker = new FolderChangeTracker(null, 5000, delegate(string path, bool recursive)
        {
            if (!recursive || path != missing || Interlocked.Increment(ref missingFailures) != 1) return;
            long before = tracker.Revision;
            Directory.CreateDirectory(other);
            Until(() => tracker.Revision > before, "other root creation reaches the shared parent guard");
            Interlocked.Exchange(ref guardObserved, 1);
        });
        using (tracker)
        {
            tracker.Track(new[] { missing, other });
            Until(() => tracker.IsCovered(other), "unrelated root becomes covered", tracker);
            Check(Interlocked.CompareExchange(ref guardObserved, 0, 0) == 1 && !tracker.IsCovered(missing),
                "Unrelated topology changes do not claim coverage of a missing root");
            Thread.Sleep(1200);
            Check(Interlocked.CompareExchange(ref missingFailures, 0, 0) == 1,
                "Per-path generations preserve backoff for an independently missing root");
            Check(tracker.RecursiveWatcherCount == 1 && tracker.GuardWatcherCount == 1,
                "Shared guard remains bounded during independent failure");
        }
    }

    private static void Until(Func<bool> condition, string message, FolderChangeTracker tracker = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception(message + (tracker == null ? "" : "; " + tracker.Diagnostic));
            Thread.Sleep(5);
        }
    }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
}
