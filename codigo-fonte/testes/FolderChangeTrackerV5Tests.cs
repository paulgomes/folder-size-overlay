using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PastaGBIcons;

// Real filesystem notification tests. Linux execution proves watcher/guard
// coordination there; it is not evidence of Windows Explorer or Win32 events.
public static class FolderChangeTrackerV5Tests
{
    private static int assertions;
    private static string fixture;
    public static int Main()
    {
        fixture = Path.Combine(Path.GetTempPath(), "PastaGB-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            TestContentAndSharedGuard();
            TestRootRenameAndReplacement();
            TestAliasReplacement();
            TestWatcherBudget();
            TestQueueOverflow();
            Console.WriteLine("PASS: " + assertions + " real filesystem watcher assertions on " + Environment.OSVersion.Platform + ".");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { try { Directory.Delete(fixture, true); } catch { } }
    }

    private static void TestContentAndSharedGuard()
    {
        string parent = Folder(fixture, "shared-parent");
        string first = Folder(parent, "First"), second = Folder(parent, "Second");
        using (FolderChangeTracker tracker = new FolderChangeTracker())
        {
            tracker.Track(new[] { first, second });
            Until(() => tracker.IsCovered(first) && tracker.IsCovered(second), "root and replacement guard become covered");
            Check(tracker.RecursiveWatcherCount == 2 && tracker.GuardWatcherCount == 1, "sibling roots share one shallow parent guard");
            DrainQuietly(tracker);
            string sibling = Folder(parent, "Unrelated"); File.WriteAllText(Path.Combine(sibling, "unrelated.txt"), "x");
            Thread.Sleep(150);
            FolderChangeBatch ignored = tracker.Drain();
            Check(!ignored.ReconcileAll && ignored.Paths.Length == 0, "unrelated siblings do not invalidate either root");
            string child = Path.Combine(first, "payload.txt"); File.WriteAllText(child, "payload");
            FolderChangeBatch found = AwaitPath(tracker, child);
            Check(found.Paths.Contains(child), "content callback returns exact changed child path");
            Check(!found.Paths.Contains(parent), "guard never enqueues a broad parent dependency");
        }
    }

    private static void TestRootRenameAndReplacement()
    {
        string parent = Folder(fixture, "root-rename");
        string root = Folder(parent, "Root"), moved = Path.Combine(parent, "Moved");
        using (FolderChangeTracker tracker = new FolderChangeTracker())
        {
            tracker.Track(new[] { root }); Until(() => tracker.IsCovered(root), "initial root covered");
            DrainQuietly(tracker);
            Directory.Move(root, moved);
            FolderChangeBatch renamed = AwaitPath(tracker, root);
            Check(renamed.Paths.Contains(root) || renamed.ReconcileAll, "renaming the watched root invalidates the original name");
            Until(() => !tracker.IsCovered(root), "old native watcher cannot claim coverage after root rename");
            Directory.CreateDirectory(root);
            Until(() => tracker.IsCovered(root), "guard rebuilds watcher for replacement at original root path");
            DrainQuietly(tracker);
            string newFile = Path.Combine(root, "new.txt"); File.WriteAllText(newFile, "new");
            Check(AwaitPath(tracker, newFile).Paths.Contains(newFile), "replacement root is watched for new changes");
            DrainQuietly(tracker);
            File.WriteAllText(Path.Combine(moved, "old.txt"), "old"); Thread.Sleep(100);
            FolderChangeBatch stale = tracker.Drain();
            Check(!stale.Paths.Any(x => x.EndsWith("old.txt", StringComparison.Ordinal)), "retired root watcher cannot publish old directory events");
        }
    }

    private static void TestAliasReplacement()
    {
        MethodInfo create = typeof(Directory).GetMethod("CreateSymbolicLink", new[] { typeof(string), typeof(string) });
        if (create == null) { Console.WriteLine("SKIP: runtime has no symbolic-link fixture API."); return; }
        string parent = Folder(fixture, "alias-replace");
        string targetA = Folder(parent, "TargetA"), targetB = Folder(parent, "TargetB"), alias = Path.Combine(parent, "Alias");
        try { create.Invoke(null, new object[] { alias, targetA }); }
        catch (TargetInvocationException ex)
        {
            Console.WriteLine("SKIP: symbolic-link fixture creation unavailable: " + ex.InnerException.Message); return;
        }
        try
        {
            using (FolderChangeTracker tracker = new FolderChangeTracker())
            {
                tracker.Track(new[] { alias, targetA });
                Until(() => tracker.IsCovered(alias) && tracker.IsCovered(targetA), "initial alias plus target covered");
                DrainQuietly(tracker);
                Directory.Delete(alias);
                create.Invoke(null, new object[] { alias, targetB });
                FolderChangeBatch changed = AwaitPath(tracker, alias);
                Check(changed.ReconcileAll || changed.Paths.Contains(alias), "replacing alias invalidates its own path");
                Until(() => tracker.IsCovered(alias), "replacement alias watch is established");
                DrainQuietly(tracker);
                string targetFile = Path.Combine(targetB, "after.txt"), logical = Path.Combine(alias, "after.txt");
                File.WriteAllText(targetFile, "new target");
                FolderChangeBatch content = AwaitPath(tracker, logical);
                Check(content.Paths.Contains(logical), "replacement alias observes its new target");
            }
        }
        finally { if (Directory.Exists(alias)) Directory.Delete(alias); }
    }

    private static void TestWatcherBudget()
    {
        string parent = Folder(fixture, "budgets"); string[] roots = new string[18];
        for (int i = 0; i < roots.Length; i++) roots[i] = Folder(Folder(parent, "P" + i), "Root");
        FolderChangeTracker tracker = new FolderChangeTracker();
        try
        {
            tracker.Track(roots);
            Until(() => tracker.IsCovered(roots[0]) && tracker.IsCovered(roots[11]), "first twelve roots become covered");
            Check(tracker.RecursiveWatcherCount == 12 && tracker.GuardWatcherCount == 12, "at most twelve recursive plus twelve guard handles");
            Check(!tracker.IsCovered(roots[12]), "unwatched root explicitly falls back to periodic reconciliation");
            tracker.Track(new[] { parent });
            Until(() => tracker.IsCovered(parent) && tracker.RecursiveWatcherCount == 1 && tracker.GuardWatcherCount == 1,
                "ancestor coalescing releases obsolete root and guard watches");
            Check(tracker.IsCovered(roots[17]), "healthy recursive ancestor covers descendants with its own guard");
        }
        finally { tracker.Dispose(); }
        Until(() => tracker.RecursiveWatcherCount == 0 && tracker.GuardWatcherCount == 0, "shutdown releases all native watches");
        Check(!tracker.IsCovered(parent), "disposed tracker never claims coverage");
    }

    private static void TestQueueOverflow()
    {
        string root = Folder(fixture, "overflow");
        using (FolderChangeTracker tracker = new FolderChangeTracker())
        {
            tracker.Track(new[] { root }); Until(() => tracker.IsCovered(root), "overflow fixture covered");
            DrainQuietly(tracker);
            for (int i = 0; i < 400; i++) File.WriteAllText(Path.Combine(root, i.ToString("D3") + ".txt"), "x");
            Thread.Sleep(250);
            FolderChangeBatch batch = tracker.Drain();
            Check(batch.ReconcileAll && batch.Paths.Length <= 256, "pending notification overflow requests global reconciliation within bounded memory");
            Check(batch.Revision > 0, "notification revisions advance for active scan invalidation");
        }
    }

    private static FolderChangeBatch AwaitPath(FolderChangeTracker tracker, string path)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until)
        {
            FolderChangeBatch batch = tracker.Drain();
            if (batch.ReconcileAll || batch.Paths.Contains(path)) return batch;
            Thread.Sleep(10);
        }
        throw new Exception("Missing filesystem event: " + path + "; " + tracker.Diagnostic);
    }
    private static void Until(Func<bool> condition, string message)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(6);
        while (!condition()) { if (DateTime.UtcNow > until) throw new Exception(message); Thread.Sleep(10); }
    }
    private static void DrainQuietly(FolderChangeTracker tracker) { Thread.Sleep(100); tracker.Drain(); }
    private static string Folder(string parent, string name) { string path = Path.Combine(parent, name); Directory.CreateDirectory(path); return path; }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
}
