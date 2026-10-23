using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PastaGB;
using PastaGBIcons;

// Compile the unchanged production IconCoordinator.cs with this headless host.
// ProcessScanSession and notifications below are explicit controllable fakes.
// This suite exercises coordinator state/scheduling, not native UI or real IPC.
public static class CoordinatorV5Tests
{
    private static int assertions;
    public static int Main()
    {
        try
        {
            ParentChildSharesCompletedSubtotal();
            ForegroundChildDoesNotWaitForBackgroundParent();
            LinkPolicyRejectsLateResult();
            MediaReplacementRejectsLateResult();
            ContentChangePreservesValueDuringRefresh();
            EconomicalProfileDrainsThenUsesOneLimitedReader();
            PauseCancelsWithoutRestarting();
            Console.WriteLine("PASS: " + assertions + " assertions against production IconCoordinator.cs with fake process/notification adapters.");
#if V60_COORDINATOR_TESTS
            V60CoordinatorTests.Run();
#endif
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }

    private static void ParentChildSharesCompletedSubtotal()
    {
        ProcessScanSession.Reset(); string parent = @"C:\Projects", child = @"C:\Projects\Client";
        using (IconContext host = new IconContext(parent, child))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1, "parent begins reading");
            Check(ProcessScanSession.Requests[0].Path == parent, "equally-prioritized parent is selected before its child");
            Thread.Sleep(380);
            Check(ProcessScanSession.Requests.Length == 1, "parent and child are not enumerated in parallel");
            ScanResult result = Result(parent, 2000);
            result.Subtotals.Add(new FolderSummary { Path = child, Name = "Client", Bytes = 700, FileCount = 3 });
            ProcessScanSession.Requests[0].Finish(result);
            Until(host, () => Ready(host, parent, 2000) && Ready(host, child, 700), "parent completion populates child subtotal");
            Thread.Sleep(230);
            Check(ProcessScanSession.Requests.Length == 1, "ready child subtotal avoids a redundant child process");
            Check(host.Read(child).Dependencies.SequenceEqual(new[] { child }), "ordinary child subtotal tracks its own subtree");
            Check(host.SharedSubtotals == 1, "coordinator records subtotal reuse");
        }
    }

    private static void LinkPolicyRejectsLateResult()
    {
        ProcessScanSession.Reset(); string path = @"C:\Links";
        using (IconContext host = new IconContext(path))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1, "initial policy read starts");
            ProcessScanSession first = ProcessScanSession.Requests[0];
            host.SetLinkPolicy(false, false);
            Until(host, () => first.CancelRequested, "changing link policy cancels old work");
            first.Finish(Result(path, 999)); // deliberately ignores cancellation
            Until(host, () => ProcessScanSession.Requests.Length == 2, "new policy gets a new reader");
            ProcessScanSession second = ProcessScanSession.Requests[1];
            Check(!second.Options.FollowDirectoryLinks && !second.Options.CrossVolumeLinks, "new request uses the new link policy");
            Check(!host.EverPublishedValue(path, 999), "late old-policy result never becomes a displayed value");
            second.Finish(Result(path, 55));
            Until(host, () => Ready(host, path, 55), "new policy result is accepted");
            Check(host.Read(path).Bytes == 55 && first.Disposed, "old reader is disposed while new result remains associated");
        }
    }

    private static void ForegroundChildDoesNotWaitForBackgroundParent()
    {
        ProcessScanSession.Reset(); string parent = @"C:\LargeTree", child = @"C:\LargeTree\Visible";
        using (IconContext host = new IconContext(parent))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1, "background-overlap fixture begins parent read");
            ProcessScanSession first = ProcessScanSession.Requests[0];
            host.ShowForegroundChild(parent, child);
            Until(host, () => first.CancelRequested, "foreground child must be able to preempt one overlapping background reader despite a free second slot");
            Check(ProcessScanSession.Requests.Length == 1, "foreground child waits for parent cancellation before entering same subtree");
            first.Finish(Result(parent, 500));
            Until(host, () => ProcessScanSession.Requests.Length == 2, "foreground child begins after overlapping parent drains");
            Check(ProcessScanSession.Requests[1].Path == child, "free reader is assigned to the currently used child folder");
        }
    }

    private static void MediaReplacementRejectsLateResult()
    {
        ProcessScanSession.Reset(); string path = @"E:\Photos";
        using (IconContext host = new IconContext(path))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1, "first removable-media read starts");
            ProcessScanSession first = ProcessScanSession.Requests[0];
            host.ChangeMedia('E');
            Until(host, () => first.CancelRequested, "media revision cancels the old reader");
            first.Finish(Result(path, 888));
            Until(host, () => ProcessScanSession.Requests.Length == 2, "replacement media starts its own read");
            Check(!host.EverPublishedValue(path, 888), "same-letter late result is rejected");
            ProcessScanSession.Requests[1].Finish(Result(path, 77));
            Until(host, () => Ready(host, path, 77), "replacement media result is accepted");
            Check(host.Read(path).VolumeStamp[4] == 1, "accepted result carries new E volume revision");
        }
    }

    private static void ContentChangePreservesValueDuringRefresh()
    {
        ProcessScanSession.Reset(); string path = @"C:\Docs";
        using (IconContext host = new IconContext(path))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1, "initial value read starts");
            ProcessScanSession.Requests[0].Finish(Result(path, 1000000000));
            Until(host, () => Ready(host, path, 1000000000), "initial value is ready");
            host.NotifyContent(@"C:\Docs\changed.bin");
            Until(host, () => host.Read(path) != null && host.Read(path).Phase == SizePhase.Stale, "content change marks last value stale");
            SizeRecord stale = host.Read(path);
            Check(stale.HasValue && stale.Bytes == 1000000000, "dirty state retains the last numeric value");
            Check(SizePresentation.Caption(stale, SizeUnits.Gigabytes, true).Contains("↻"), "dirty value has the updating indicator");
            Until(host, () => ProcessScanSession.Requests.Length == 2, "coalesced change triggers a refresh");
            // The fake Start records its request before ScanLoop publishes the
            // corresponding UI state. Await that publication independently; a
            // launch-side effect is not a publication barrier on another thread.
            Until(host, () => host.Read(path) != null && host.Read(path).Phase == SizePhase.Refreshing,
                "refresh phase reaches the published snapshot");
            Check(host.Read(path).Phase == SizePhase.Refreshing && host.Read(path).Bytes == 1000000000,
                "active refresh also retains the last numeric value");
            ProcessScanSession.Requests[1].Finish(Result(path, 1250000000));
            Until(host, () => Ready(host, path, 1250000000), "refresh updates the ready total");
            Check(!SizePresentation.Caption(host.Read(path), SizeUnits.Gigabytes, true).Contains("↻"), "updating indicator clears after valid completion");
        }
    }

    private static void EconomicalProfileDrainsThenUsesOneLimitedReader()
    {
        ProcessScanSession.Reset(); string firstPath = @"C:\A", secondPath = @"D:\B";
        using (IconContext host = new IconContext(firstPath, secondPath))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 2, "balanced profile starts two independent readers");
            ProcessScanSession[] old = ProcessScanSession.Requests;
            Check(old.All(x => x.CpuPercent == 15 && x.Options.MaxEntriesPerSecond == 12000), "balanced request limits are applied");
            host.SetEconomical();
            Until(host, () => old.All(x => x.CancelRequested), "profile change cancels both old readers");
            old[0].Finish(Result(old[0].Path, 123));
            Until(host, () => old[0].Disposed, "first old reader drains");
            Thread.Sleep(340);
            Check(ProcessScanSession.Requests.Length == 2, "eco does not start a second reader while one old reader remains");
            old[1].Finish(Result(old[1].Path, 456));
            Until(host, () => ProcessScanSession.Requests.Length == 3, "eco starts after remaining old work drains");
            ProcessScanSession eco = ProcessScanSession.Requests[2];
            Check(eco.CpuPercent == 10 && eco.Options.MaxEntriesPerSecond == 3000, "eco applies CPU10 and3000 metadata entries per second");
            Check(eco.Options.MaxDirectories == 75000 && eco.Options.MaxSubtotals == 1024, "eco uses bounded traversal/detail settings");
            Thread.Sleep(650);
            Check(ProcessScanSession.ActiveCount == 1 && ProcessScanSession.Requests.Length == 3, "eco holds exactly one pending reader");
            Check(!host.EverPublishedValue(old[0].Path, 123) && !host.EverPublishedValue(old[1].Path, 456),
                "cancelled old-profile completions are not published as new totals");
        }
    }

    private static void PauseCancelsWithoutRestarting()
    {
        ProcessScanSession.Reset(); string path = @"C:\Paused";
        using (IconContext host = new IconContext(path))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1, "pause fixture starts one reader");
            ProcessScanSession first = ProcessScanSession.Requests[0];
            host.Pause(); Until(host, () => first.CancelRequested, "pause cancels active work");
            first.Finish(Result(path, 200));
            Until(host, () => first.Disposed && host.TrackedPaths.Length == 0, "pause retires completed reader and stops watches");
            Thread.Sleep(650);
            Check(ProcessScanSession.Requests.Length == 1 && ProcessScanSession.ActiveCount == 0, "paused coordinator does not restart work");
            Check(!host.EverPublishedValue(path, 200), "late paused result does not replace a folder value");
        }
    }

    private static bool Ready(IconContext host, string path, long bytes)
    { SizeRecord value = host.Read(path); return value != null && value.Phase == SizePhase.Ready && value.Bytes == bytes; }
    private static ScanResult Result(string path, long bytes)
    {
        return new ScanResult
        {
            RootPath = path, TotalBytes = bytes, FileCount = 1,
            StartedUtc = DateTime.UtcNow.AddMilliseconds(-10), EndedUtc = DateTime.UtcNow,
            Children = new List<FolderSummary>(), Subtotals = new List<FolderSummary>(),
            DependencyPaths = new List<string> { path }, Issues = new List<ScanIssue>(), TopFiles = new List<FileSummary>()
        };
    }
    private static void Until(IconContext host, Func<bool> condition, string message)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(7);
        while (!condition())
        {
            host.CheckFailure();
            if (DateTime.UtcNow > until) throw new Exception(message + "; " + host.Activity);
            Thread.Sleep(10);
        }
        host.CheckFailure();
    }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
}

namespace PastaGB
{
    // Controllable transport adapter; no processes, native limits, or file I/O.
    public sealed class ProcessScanSession : IDisposable
    {
        private static readonly object RegistryGate = new object();
        private static readonly List<ProcessScanSession> registry = new List<ProcessScanSession>();
        private readonly object gate = new object();
        internal string Path;
        internal ScanOptions Options;
        internal int CpuPercent;
        private bool completed, cancelled, disposed;
        private ScanResult result;
        private DateTime startedUtc = DateTime.UtcNow;

        internal static ProcessScanSession[] Requests { get { lock (RegistryGate) return registry.ToArray(); } }
        internal static int ActiveCount { get { lock (RegistryGate) return registry.Count(x => !x.Disposed); } }
        public static int AvailableSlots { get { return Math.Max(0, 2 - ActiveCount); } }
        public static int QuarantinedProcessCount { get { return 0; } }
        public bool Completed { get { lock (gate) return completed; } }
        public ScanResult Result { get { lock (gate) return result; } }
        internal bool CancelRequested { get { lock (gate) return cancelled; } }
        internal bool Disposed { get { lock (gate) return disposed; } }
        public string Diagnostic { get { return "FAKE process adapter"; } }

        public static ProcessScanSession Start(string path, ScanOptions options, int seconds, int cpu)
        {
            lock (RegistryGate)
            {
                if (registry.Count(x => !x.Disposed) >= 2) throw new Exception("Coordinator exceeded fake process slots.");
                ProcessScanSession result = new ProcessScanSession { Path = path, Options = options.Snapshot(), CpuPercent = cpu };
                registry.Add(result); return result;
            }
        }
        internal static void Reset()
        {
            lock (RegistryGate)
            {
                if (registry.Any(x => !x.Disposed)) throw new Exception("Previous coordinator did not release a reader.");
                registry.Clear();
            }
        }
        internal void Finish(ScanResult value) { lock (gate) { result = value; completed = true; } }
        public void Cancel() { lock (gate) cancelled = true; }
        public ScanProgress GetProgress() { return new ScanProgress { RootPath = Path, CurrentPath = Path, StartedUtc = startedUtc }; }
        public void Dispose() { lock (gate) disposed = true; }
    }
}

namespace PastaGBIcons
{
    internal sealed class FolderChangeBatch
    {
        internal bool ReconcileAll;
        internal string[] Paths = new string[0];
        internal long Revision;
    }
    internal sealed class FolderChangeTracker
    {
        private readonly object gate = new object();
        private readonly List<string> pending = new List<string>();
        private long revision;
        internal int TrackCalls;
        private string[] tracked = new string[0];
        internal long Revision { get { lock (gate) return revision; } }
        internal string[] Tracked { get { lock (gate) return tracked.ToArray(); } }
        internal void Emit(string path) { lock (gate) { pending.Add(path); revision++; } }
        internal void Track(IEnumerable<string> paths)
        { lock (gate) { tracked = paths.ToArray(); Interlocked.Increment(ref TrackCalls); } }
        internal bool IsCovered(string path) { return true; }
        internal FolderChangeBatch Drain()
        {
            lock (gate)
            {
                var batch = new FolderChangeBatch { Paths = pending.ToArray(), Revision = revision, ReconcileAll = false };
                pending.Clear(); return batch;
            }
        }
    }
    internal sealed class HeadlessFolder { internal string Path; }
    internal sealed class DesktopSnapshot { internal List<HeadlessFolder> Folders = new List<HeadlessFolder>(); }
    internal sealed class ExplorerSurface { internal IntPtr RootHandle; internal string Location; }
    internal sealed class SurfaceState { internal DesktopSnapshot Snapshot; internal ExplorerSurface Explorer; }

    // ScanWork is the data adapter otherwise declared beside the WinForms host.
    internal sealed class ScanWork
    {
        internal string Path;
        internal ProcessScanSession Session;
        internal int Generation, PolicyGeneration;
        internal long[] VolumeStamp;
        internal DateTime LastWantedUtc;
        internal SizeRecord Previous;
        internal bool CancelRequested, ProtectedFirstRead, ChangedDuringRead;
        internal long ChangeRevision;
    }

    internal sealed partial class IconContext : IDisposable
    {
        private readonly object stateGate = new object();
        private readonly VolumeRevisions volumes = new VolumeRevisions();
        private readonly FolderChangeTracker changes = new FolderChangeTracker();
        private AppPreferences preferences = new AppPreferences();
        private int policyGeneration, refreshGeneration;
        private volatile bool stopped, paused;
        private string scannerError, scannerActivity;
        private Dictionary<string, SizeRecord> sizes = new Dictionary<string, SizeRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SurfaceState> surfaces = new List<SurfaceState>();
        private readonly List<SizeRecord> history = new List<SizeRecord>();
        private readonly Thread coordinator;
        private Exception failure;

        internal IconContext(params string[] paths)
        {
            DesktopSnapshot snapshot = new DesktopSnapshot();
            foreach (string path in paths) snapshot.Folders.Add(new HeadlessFolder { Path = path });
            surfaces.Add(new SurfaceState { Snapshot = snapshot, Explorer = new ExplorerSurface { RootHandle = new IntPtr(1), Location = @"C:\" } });
            coordinator = new Thread(delegate()
            {
                try { ScanLoop(); } catch (Exception ex) { lock (stateGate) failure = ex; }
            }) { IsBackground = true };
            coordinator.Start();
        }
        private IntPtr GetForegroundWindow() { return new IntPtr(1); }
        private List<SurfaceState> CurrentSurfaces(bool rendering) { lock (stateGate) return surfaces.ToList(); }
        private void RecordEvent(string message) { }
        private void RequestCachedSizes(IEnumerable<string> wanted, AppPreferences settings, int policy) { }
        private bool TryRestoreCachedSize(string path, AppPreferences settings, int policy, out SizeRecord record)
        {
#if V60_COORDINATOR_TESTS
            return TryRestoreFake(path, out record);
#else
            record = null; return false;
#endif
        }
        private void RememberCompletedSize(SizeRecord record, ScanResult result, AppPreferences settings, int policy) { }
        private void PublishSizes(Dictionary<string, SizeRecord> source, int expectedPolicy)
        {
            lock (stateGate)
            {
                if (policyGeneration != expectedPolicy || stopped) return;
                sizes = new Dictionary<string, SizeRecord>(source, StringComparer.OrdinalIgnoreCase);
                history.AddRange(source.Values.Select(x => x.Copy()));
            }
        }
        internal SizeRecord Read(string path)
        { lock (stateGate) { SizeRecord result; return sizes.TryGetValue(path, out result) ? result.Copy() : null; } }
        internal string Activity { get { lock (stateGate) return scannerActivity + " " + scannerError; } }
        internal long SharedSubtotals { get { return Interlocked.Read(ref sharedSubtotals); } }
        internal string[] TrackedPaths { get { return changes.Tracked; } }
        internal bool EverPublishedValue(string path, long bytes)
        { lock (stateGate) return history.Any(x => x.Path == path && x.HasValue && x.Bytes == bytes); }
        internal void CheckFailure() { lock (stateGate) if (failure != null) throw new Exception("Coordinator threw.", failure); }
        internal void NotifyContent(string path) { changes.Emit(path); WakeScanner(); }
        internal void ChangeMedia(char letter) { volumes.Changed(1U << (Char.ToUpperInvariant(letter) - 'A')); WakeScanner(); }
        internal void SetLinkPolicy(bool follow, bool cross)
        {
            lock (stateGate)
            {
                AppPreferences next = preferences.Copy(); next.FollowDirectoryLinks = follow; next.CrossVolumeLinks = cross;
                preferences = next; policyGeneration++; sizes.Clear(); Interlocked.Increment(ref refreshGeneration);
            }
            WakeScanner();
        }
        internal void SetEconomical()
        { lock (stateGate) { AppPreferences next = preferences.Copy(); next.Performance = PerformanceMode.Economical; preferences = next; } WakeScanner(); }
        internal void Pause() { paused = true; ScanDemandChanged(); }
        internal void ShowForegroundChild(string parent, string child)
        {
            lock (stateGate)
            {
                surfaces.Clear();
                DesktopSnapshot background = new DesktopSnapshot(); background.Folders.Add(new HeadlessFolder { Path = parent });
                DesktopSnapshot foreground = new DesktopSnapshot(); foreground.Folders.Add(new HeadlessFolder { Path = child });
                surfaces.Add(new SurfaceState { Snapshot = background, Explorer = new ExplorerSurface { RootHandle = new IntPtr(2), Location = @"C:\" } });
                surfaces.Add(new SurfaceState { Snapshot = foreground, Explorer = new ExplorerSurface { RootHandle = new IntPtr(1), Location = parent } });
            }
            ScanDemandChanged();
        }
        public void Dispose()
        {
            stopped = true;
            WakeScanner();
            if (!coordinator.Join(2000)) throw new Exception("Headless coordinator did not stop.");
            CheckFailure();
        }
    }
}
