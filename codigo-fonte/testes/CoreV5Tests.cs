using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using PastaGB;
using PastaGBIcons;

internal static class CoreV5Tests
{
    private static int checks;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; }
    private static readonly DateTime Now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static void ValuesAndFreshness()
    {
        Check(SizePresentation.Bytes(0, SizeUnits.Gigabytes) == "0 GB", "An empty folder is exactly zero");
        Check(SizePresentation.Bytes(1, SizeUnits.Gigabytes) == "<0,01 GB", "A nonempty small folder never looks empty");
        Check(SizePresentation.Bytes(9999999, SizeUnits.Gigabytes) == "<0,01 GB", "Small-value boundary remains explicit");
        Check(SizePresentation.Bytes(10000000, SizeUnits.Gigabytes) == "0,01 GB", "GB threshold is decimal and localized");
        Check(SizePresentation.Bytes(1250000000, SizeUnits.Gigabytes) == "1,25 GB", "GB uses one billion bytes");
        Check(SizePresentation.Bytes(999999999, SizeUnits.Automatic) == "1 GB", "Rounded unit boundary does not show1000MB");
        Check(SizePresentation.Bytes(Int64.MaxValue, SizeUnits.Automatic) == "9,22 EB", "Large valid values do not overflow");
        bool rejected = false;
        try { SizePresentation.Bytes(-1, SizeUnits.Gigabytes); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Negative sizes cannot appear as valid");
        var original = new SizeRecord { Path = @"C:\Fotos", Bytes = 1250000000, Phase = SizePhase.Ready,
            FinishedUtc = Now.AddSeconds(-10), Dependencies = new[] { @"C:\Fotos" } };
        SizeRecord stale = original.Invalidate("arquivo alterado");
        Check(original.Phase == SizePhase.Ready && stale.Phase == SizePhase.Stale, "Invalidation preserves the immutable displayed snapshot");
        Check(stale.Bytes == original.Bytes && stale.HasValue, "Last valid total survives an update");
        Check(SizePresentation.Caption(stale, SizeUnits.Gigabytes, true) == "1,25 GB ↻", "Update marker remains attached to old total");
        stale.LastResultPartial = true;
        Check(SizePresentation.Caption(stale, SizeUnits.Gigabytes, true) == "1,25 GB* ↻", "Partial total cannot lose partial state while refreshing");
        Check(SizeRefreshPolicy.Due(stale, Now, true, 300), "Content changes bypass ordinary freshness interval");
        stale.NextAttemptUtc = Now.AddMilliseconds(800);
        Check(!SizeRefreshPolicy.Due(stale, Now, false, 300), "A burst is debounced even without a watcher");
        Check(SizeRefreshPolicy.Due(stale, Now.AddMilliseconds(800), true, 300), "Debounce expires at its deadline");
        stale.Phase = SizePhase.Refreshing;
        Check(!SizeRefreshPolicy.Due(stale, Now.AddHours(1), false, 300), "An active refresh cannot launch duplicate reading");
        Check(stale.Invalidate("mais mudanças").Phase == SizePhase.Refreshing, "New event cannot erase an active read");
        original.FinishedUtc = Now.AddSeconds(-90);
        Check(SizeRefreshPolicy.Due(original, Now, false, 300), "Unmonitored folders reconcile within60seconds");
        Check(!SizeRefreshPolicy.Due(original, Now, true, 300), "Monitored unchanged folders avoid repeated traversal");
        Check(SizeRefreshPolicy.Due(original, Now.AddSeconds(210), true, 300), "Periodic reconciliation handles missed notifications");
        Check(!SizeRefreshPolicy.Due(original, Now.AddSeconds(210), true, 600), "Economical mode lengthens only the monitored interval");
        Check(SizePresentation.Caption(new SizeRecord { Phase = SizePhase.Queued }, SizeUnits.Gigabytes, true) == "aguardando…", "Queue is distinct from work in progress");
        Check(SizePresentation.Caption(new SizeRecord { Phase = SizePhase.Scanning }, SizeUnits.Gigabytes, true) == "calculando…", "Active scan has explicit state");
    }

    private static void AssociationAndSharing()
    {
        var volumes = new VolumeRevisions();
        long[] stamp = volumes.Capture();
        string root = @"C:\Fotos";
        var result = new ScanResult { RootPath = root, TotalBytes = 2000000000,
            DependencyPaths = new List<string> { root, @"E:\Arquivo", @"e:\arquivo" } };
        SizeRecord ready = SizeResultPolicy.Create(root, stamp, result, Now, false, null);
        Check(ready.Phase == SizePhase.Ready && ready.Dependencies.Length == 2, "Verified root and normalized dependency deduplication");
        Check(volumes.MatchesDependencies(stamp, root, ready.Dependencies), "Current external target can back a local alias");
        var child = new FolderSummary { Path = @"C:\Fotos\2026", Bytes = 400, FileCount = 3 };
        SizeRecord shared = SizeResultPolicy.Subtotal(ready, child);
        Check(shared != null && shared.Bytes == 400 && shared.Path == child.Path, "A completed subtree supplies only its own value");
        Check(shared.FinishedUtc == Now && shared.Dependencies.Length == 2, "Shared value keeps the scan age and link dependencies");
        Check(SizeResultPolicy.Subtotal(ready, new FolderSummary { Path = @"C:\FotosAntigas\2026", Bytes = 999 }) == null,
            "A textual prefix is not a descendant");
        Check(SizeResultPolicy.Subtotal(ready, new FolderSummary { Path = root, Bytes = 999 }) == null, "A child cannot overwrite its parent total");
        Check(SizeResultPolicy.Subtotal(ready.Invalidate("changed"), child) == null, "Changed traversal cannot seed fresh subtotals");
        var independent = SizeResultPolicy.Create(root, stamp, new ScanResult { RootPath = root, TotalBytes = 9 }, Now, false, null);
        SizeRecord narrow = SizeResultPolicy.Subtotal(independent, child);
        Check(narrow.Dependencies.Length == 1 && narrow.Dependencies[0] == child.Path, "Ordinary subtree does not depend on every sibling");
        volumes.Changed(1u << 4);
        Check(!volumes.MatchesDependencies(stamp, root, ready.Dependencies), "Disconnecting an external target invalidates the local alias");
        Check(!volumes.MatchesDependencies(stamp, child.Path, shared.Dependencies), "Subtotals cannot survive a replaced dependency drive");
        Check(volumes.MatchesDependencies(stamp, child.Path, narrow.Dependencies), "Unrelated local folder keeps its value");
        Check(!volumes.MatchesAll(stamp), "In-flight scans reject changed media before all dependencies are known");
        Check(!volumes.MatchesDependencies(null, root, null) && !volumes.MatchesAll(new long[1]), "Missing or malformed stamps cannot authorize values");
        Check(!volumes.MatchesDependencies(volumes.Capture(), root, new[] { @"\\server\share" }), "Unsupported dependency cannot be mistaken for verified local media");
        Check(PathRelations.Affects(root, ready.Dependencies, @"E:\Arquivo\a.dat"), "Changes through external target dirty the alias");
        Check(!PathRelations.Affects(root, ready.Dependencies, @"E:\Arquivo2\a.dat"), "Prefix sibling does not invalidate target");
        Check(PathRelations.Contains(@"C:\", @"c:/Fotos/a.dat"), "Drive roots and separator variants compare correctly");
        Check(PathRelations.Contains(@"\\?\C:\Fotos\", @"C:\Fotos\2026"), "Extended local paths use the same identity");
        Check(PathRelations.Affects(@"C:\Fotos\2026", null, @"C:\Fotos"), "Ancestor replacement invalidates descendants");
        var wrong = new ScanResult { RootPath = @"C:\Outra", TotalBytes = 999, DependencyPaths = new List<string> { @"Z:\Fake" } };
        SizeRecord rejected = SizeResultPolicy.Create(root, stamp, wrong, Now, false, null);
        Check(rejected.Phase == SizePhase.Unavailable && !rejected.HasValue && rejected.Dependencies.Length == 1,
            "A late or unrelated result cannot transfer either bytes or dependencies");
        result.ErrorCount = 1;
        SizeRecord partial = SizeResultPolicy.Create(root, stamp, result, Now, false, null);
        Check(partial.Phase == SizePhase.Partial && partial.Bytes == result.TotalBytes, "Read errors preserve an explicitly partial sum");
        Check(SizeResultPolicy.Subtotal(partial, child).Phase == SizePhase.Ready, "Completed safe child is usable despite a sibling error");
        child.SkippedLinks = 1;
        Check(SizeResultPolicy.Subtotal(partial, child).Phase == SizePhase.Partial, "A partial child's warning travels with its subtotal");
        Check(SizeResultPolicy.Create(root, stamp, result, Now, true, null).LastResultPartial, "Changed partial read preserves both conditions");
        result.FatalError = "acesso negado";
        SizeRecord failed = SizeResultPolicy.Create(root, stamp, result, Now, false, null);
        Check(failed.Phase == SizePhase.Unavailable && SizePresentation.Caption(failed, SizeUnits.Gigabytes, true) == "indisponível",
            "Root access failure is never displayed as0GB");
        Check(!SizeRefreshPolicy.Due(failed, Now.AddSeconds(4), true, 300), "Failure backoff prevents a retry storm");
        Check(SizeRefreshPolicy.Due(failed, Now.AddSeconds(5), true, 300), "Automatic retry becomes eligible");
        for (int i = 0; i < 25; i++) failed = SizeResultPolicy.Create(root, stamp, null, Now, false, failed);
        Check((failed.NextAttemptUtc - Now).TotalSeconds == 120 && failed.FailureCount == 20, "Repeated crashes have bounded backoff and counters");
        Check(SizeResultPolicy.Create(root, stamp, new ScanResult { RootPath = root, Cancelled = true }, Now, false, null).Phase == SizePhase.Unavailable,
            "A cancelled result never becomes complete even through defensive fallback");
        Check(SizeResultPolicy.Create(root, stamp, new ScanResult { RootPath = root, TotalBytes = -1 }, Now, false, null).Phase == SizePhase.Unavailable,
            "Negative protocol data cannot create a valid size");
    }

    private static void Preferences()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pasta-gb-prefs-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "preferencias.ini"), error;
        try
        {
            AppPreferences defaults = AppPreferences.Load(path, out error);
            Check(error == null && defaults.Units == SizeUnits.Gigabytes && defaults.MaximumReaders == 2, "New installations use GB and bounded balanced readers");
            var selected = new AppPreferences { Units = SizeUnits.Automatic, Theme = AppearanceTheme.Dark,
                TextScale = 1.15f, Performance = PerformanceMode.Economical, FollowDirectoryLinks = false, CrossVolumeLinks = false };
            selected.Save(path);
            AppPreferences loaded = AppPreferences.Load(path, out error);
            Check(error == null && loaded.Units == selected.Units && loaded.Theme == selected.Theme && loaded.TextScale == selected.TextScale &&
                loaded.Performance == selected.Performance && !loaded.FollowDirectoryLinks && !loaded.CrossVolumeLinks, "All selected preferences survive restart");
            loaded.Units = SizeUnits.Gigabytes; loaded.Save(path);
            Check(AppPreferences.Load(path, out error).Units == SizeUnits.Gigabytes && Directory.GetFiles(dir).Length == 1, "Atomic replacement leaves no extra preference files");
            File.WriteAllText(path, "units=99\ntheme=-1\nperformance=9\ntextScale=NaN\nfollowDirectoryLinks=no\nunknown=valid\n");
            loaded = AppPreferences.Load(path, out error);
            Check(loaded.Units == SizeUnits.Gigabytes && loaded.Theme == AppearanceTheme.Automatic && loaded.TextScale == 1f && loaded.FollowDirectoryLinks,
                "Corrupt values retain safe defaults");
            File.WriteAllText(path, "textScale=100\n");
            Check(AppPreferences.Load(path, out error).TextScale == 1.3f, "Oversized font preference is bounded");
            File.WriteAllText(path, "textScale=-100\n");
            Check(AppPreferences.Load(path, out error).TextScale == .85f, "Undersized font preference is bounded");
            File.WriteAllText(path, new String('x', 16385));
            loaded = AppPreferences.Load(path, out error);
            Check(error != null && loaded.Units == SizeUnits.Gigabytes, "Oversized preferences fail to defaults with a diagnostic");
            Check(selected.MaximumReaders == 1 && selected.ReconcileSeconds > defaults.ReconcileSeconds && selected.ScanInterval > defaults.ScanInterval,
                "Economical mode reduces concurrent work and polling");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static void Recovery()
    {
        int attempts = 0, reports = 0, stopping = 0;
        var identities = new HashSet<int>();
        var ready = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using (var worker = new ManagedWorker("TesteRecuperacao", delegate {
            lock (identities) identities.Add(Thread.CurrentThread.ManagedThreadId);
            int attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1) throw new IOException("falha simulada");
            if (attempt == 2) return;
            ready.Set(); release.Wait();
        }, delegate(string message) { Interlocked.Increment(ref reports); }, delegate { return Volatile.Read(ref stopping) != 0; }, false, 5))
        {
            Check(ready.Wait(3000), "Exception and unexpected return both recover without user action");
            Check(attempts == 3 && reports == 2 && identities.Count == 1, "Recovery reuses one thread and waits for unwind");
            Check(worker.Diagnostic.Contains("recuperações=2"), "Recovery count is visible in diagnostics");
            Interlocked.Exchange(ref stopping, 1); release.Set();
        }
        ready.Dispose(); release.Dispose();
        var entered = new ManualResetEventSlim(false); var nativeRelease = new ManualResetEventSlim(false);
        var finished = new ManualResetEventSlim(false);
        var blocked = new ManagedWorker("TesteRetornoNativo", delegate { entered.Set(); nativeRelease.Wait(); finished.Set(); },
            delegate(string message) { }, delegate { return false; }, false, 5);
        Check(entered.Wait(2000), "Blocked component fixture actually entered its call");
        Stopwatch watch = Stopwatch.StartNew(); blocked.Dispose();
        Check(watch.ElapsedMilliseconds < 1000, "Disposal never blocks UI indefinitely on a native-like call");
        nativeRelease.Set(); Check(finished.Wait(2000), "Late native return can exit without disposed synchronization state");
        blocked.Dispose(); entered.Dispose(); nativeRelease.Dispose(); finished.Dispose();
    }

    public static int Main()
    {
        try { ValuesAndFreshness(); AssociationAndSharing(); Preferences(); Recovery();
            Console.WriteLine("PASS: " + checks + " checks for states, association, dependencies, refresh, preferences and thread recovery. Linux; Windows UI not executed."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL after " + checks + " checks: " + ex); return 1; }
    }
}
