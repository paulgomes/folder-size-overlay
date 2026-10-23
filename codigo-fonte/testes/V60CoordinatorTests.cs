using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using PastaGB;
using PastaGBIcons;

// Extends CoordinatorV5Tests' controlled host. Compile both with
// V60_COORDINATOR_TESTS; production ScanLoop and its wait policy are unchanged.
internal static class V60CoordinatorTests
{
    private static int assertions;

    internal static void Run()
    {
        IdleDoesNotRepublishOrRebuild();
        NotificationWakesIdleImmediately();
        WakeStormIsBounded();
        ContinuousWritesHaveBoundedDebounce();
        ManualRefreshBypassesDebounce();
        ResumeReconcilesUnobservedContent();
        ReplacedSnapshotRebuildsCorrectDemand();
        LateCacheAttachesToRunningRead();
        LateCacheSurvivesCancellationAndRejectsWrongPath();
        Console.WriteLine("PASS: " + assertions + " V6 coordinator assertions; real threads/timers, fake processes/notifications; no Windows UI.");
    }

    private static void IdleDoesNotRepublishOrRebuild()
    {
        ProcessScanSession.Reset();
        string[] paths = Enumerable.Range(0, 256).Select(i => @"C:\Idle\Folder" + i).ToArray();
        using (IconContext host = new IconContext(true, paths))
        {
            Until(host, () => host.LastWait == 1000, "idle host reaches event wait");
            long before = host.Passes;
            Thread.Sleep(2250);
            Check(host.Passes - before <= 3, "idle coordinator does at most one safety pass per second");
            Check(host.Publications == 0, "unchanged ready totals are never periodically republished");
            Check(host.DemandBuilds == 1 && host.WatchTrackCalls == 1, "unchanged demand/watch plan are built once");
            Check(ProcessScanSession.Requests.Length == 0 && paths.All(x => host.Read(x).Phase == SizePhase.Ready),
                "idle optimization keeps all ready values without starting reads");
            for (int i = 0; i < 12; i++)
            {
                long pass = host.Passes; host.SignalOnly();
                Until(host, () => host.Passes > pass, "explicit wake is consumed");
            }
            Check(host.DemandBuilds == 1 && host.WatchTrackCalls == 1 && host.Publications == 0,
                "redundant wake signals reuse demand and do not publish identical state");
        }
    }

    private static void NotificationWakesIdleImmediately()
    {
        ProcessScanSession.Reset(); string path = @"C:\EventWake";
        using (IconContext host = new IconContext(true, path))
        {
            Until(host, () => host.LastWait == 1000, "host waits idle");
            Thread.Sleep(75);
            Stopwatch timer = Stopwatch.StartNew(); host.NotifyContent(path + @"\file.dat");
            Until(host, () => host.Read(path).Phase == SizePhase.Stale, "notification marks value stale");
            Check(timer.ElapsedMilliseconds < 600, "content notification wakes before the one-second fallback");
            Check(host.Read(path).Bytes == 1000 && host.DemandBuilds == 1, "notification keeps numeric value and reuses demand");
        }
    }

    private static void ContinuousWritesHaveBoundedDebounce()
    {
        ProcessScanSession.Reset(); string path = @"C:\BusyWriter";
        using (IconContext host = new IconContext(true, path))
        {
            Until(host, () => host.LastWait == 1000, "continuous-write fixture starts idle");
            Stopwatch timer = Stopwatch.StartNew();
            while (ProcessScanSession.Requests.Length == 0 && timer.ElapsedMilliseconds < 6500)
            {
                host.NotifyContent(path + @"\file.dat");
                Thread.Sleep(140); host.CheckFailure();
            }
            Check(ProcessScanSession.Requests.Length == 1, "continuous writes cannot postpone refresh indefinitely");
            Check(timer.ElapsedMilliseconds >= 4500 && timer.ElapsedMilliseconds < 6500,
                "continuous-write eligibility is bounded near five seconds without an 800 ms quiet gap");
            Check(host.Publications <= 2, "dirty bursts publish only stale and reading transitions");
            Until(host, () => host.Read(path).Phase == SizePhase.Refreshing, "bounded refresh becomes visible");
            Check(host.Read(path).Bytes == 1000, "continuous updates preserve last known size");
        }
    }

    private static void WakeStormIsBounded()
    {
        ProcessScanSession.Reset();
        using (IconContext host = new IconContext(true, @"C:\WakeStorm"))
        {
            Until(host, () => host.LastWait == 1000, "wake-storm fixture starts idle");
            Thread.Sleep(70);
            long before = host.Passes;
            Stopwatch duration = Stopwatch.StartNew();
            while (duration.ElapsedMilliseconds < 450) { host.SignalOnly(); Thread.Sleep(1); }
            long cycles = host.Passes - before;
            Check(cycles >= 2 && cycles <= duration.ElapsedMilliseconds / CoordinatorWaitPolicy.MinimumNotificationMilliseconds + 3,
                "a storm of signals cannot turn the coordinator into an unbounded busy loop");
            Check(host.Publications == 0 && host.DemandBuilds == 1, "coalesced wake storm keeps unchanged display/demand cached");
        }
    }

    private static void ManualRefreshBypassesDebounce()
    {
        ProcessScanSession.Reset(); string path = @"C:\Manual";
        using (IconContext host = new IconContext(true, path))
        {
            Until(host, () => host.LastWait == 1000, "manual fixture starts idle");
            host.NotifyContent(path + @"\file.dat");
            Until(host, () => host.Read(path).Phase == SizePhase.Stale, "manual fixture is debouncing");
            Stopwatch timer = Stopwatch.StartNew(); host.RequestManualRefresh();
            Until(host, () => ProcessScanSession.Requests.Length == 1, "manual refresh starts immediately");
            Check(timer.ElapsedMilliseconds < 600, "manual refresh bypasses the ordinary debounce deadline");
        }
    }

    private static void ResumeReconcilesUnobservedContent()
    {
        ProcessScanSession.Reset(); string path = @"C:\PauseResume";
        using (IconContext host = new IconContext(true, path))
        {
            Until(host, () => host.LastWait == 1000, "pause/resume fixture starts idle");
            host.Pause();
            Until(host, () => host.TrackedPaths.Length == 0, "paused fixture releases watch demand");
            long builds = host.DemandBuilds, passes = host.Passes, publications = host.Publications;
            Thread.Sleep(1200);
            Check(host.Passes - passes <= 2 && host.DemandBuilds == builds && host.Publications == publications,
                "paused idle work stays bounded and does not republish or reconstruct demand");
            Check(ProcessScanSession.Requests.Length == 0, "paused ready data is not scanned");
            host.Resume();
            Until(host, () => ProcessScanSession.Requests.Length == 1, "resume validates content changed while monitors were paused");
            Until(host, () => host.Read(path).Phase == SizePhase.Refreshing, "resume retains prior total while validating");
            Check(host.Read(path).Bytes == 1000 && host.TrackedPaths.Contains(path), "resume restores watches and last value");
        }
    }

    private static void ReplacedSnapshotRebuildsCorrectDemand()
    {
        ProcessScanSession.Reset(); string first = @"C:\Before", second = @"C:\After";
        using (IconContext host = new IconContext(true, first))
        {
            Until(host, () => host.LastWait == 1000, "snapshot fixture starts idle");
            host.ReplaceVisibleWithoutRevision(second);
            Until(host, () => ProcessScanSession.Requests.Length == 1, "new snapshot is detected even without a revision signal");
            Check(ProcessScanSession.Requests[0].Path == second && host.DemandBuilds == 2,
                "cached demand never attaches prior path's value to a replacement path");
            Check(!host.TrackedPaths.Contains(first) && host.TrackedPaths.Contains(second), "watch demand follows replaced snapshot");
        }
    }

    private static void LateCacheAttachesToRunningRead()
    {
        ProcessScanSession.Reset(); string path = @"C:\LargeFolder";
        using (IconContext host = new IconContext(path))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1 && host.Read(path) != null &&
                host.Read(path).Phase == SizePhase.Scanning, "uncached large folder starts its normal read");
            ProcessScanSession work = ProcessScanSession.Requests[0];
            host.OfferCachedValue(path, path, 123456);
            Until(host, () => host.Read(path).Phase == SizePhase.Refreshing, "late restore appears during the ongoing read");
            Check(host.Read(path).Bytes == 123456 && ProcessScanSession.Requests.Length == 1 && !work.CancelRequested,
                "late cache shows an updating value without replacing or restarting the reader");
            work.Finish(Result(path, 234567));
            Until(host, () => host.Read(path).Phase == SizePhase.Ready && host.Read(path).Bytes == 234567,
                "current scan replaces the asynchronous cached total");
            Check(ProcessScanSession.Requests.Length == 1, "restoration does not schedule a duplicate read");
        }
    }

    private static void LateCacheSurvivesCancellationAndRejectsWrongPath()
    {
        ProcessScanSession.Reset(); string path = @"C:\CacheCancel";
        using (IconContext host = new IconContext(path))
        {
            Until(host, () => ProcessScanSession.Requests.Length == 1 && host.Read(path) != null,
                "cancel fixture starts reading");
            long before = host.Passes;
            host.OfferCachedValue(path, @"C:\OtherFolder", 999);
            Until(host, () => host.Passes > before, "mismatched restore is considered");
            Thread.Sleep(50);
            Check(!host.Read(path).HasValue, "cached total for a different folder is never attached");
            host.OfferCachedValue(path, path, 765432);
            Until(host, () => host.Read(path).Phase == SizePhase.Refreshing, "valid cached total attaches");
            ProcessScanSession work = ProcessScanSession.Requests[0];
            host.Pause(); Until(host, () => work.CancelRequested, "pause cancels cached in-flight read");
            work.Finish(Result(path, 333333));
            Until(host, () => work.Disposed && host.Read(path).Phase == SizePhase.Stale,
                "cancelled read restores cached previous value");
            Check(host.Read(path).Bytes == 765432 && !host.EverPublishedValue(path, 333333),
                "cancel keeps validated last-known total and rejects late completion");
        }
    }

    private static ScanResult Result(string path, long bytes)
    {
        return new ScanResult { RootPath = path, TotalBytes = bytes, FileCount = 1,
            StartedUtc = DateTime.UtcNow.AddMilliseconds(-10), EndedUtc = DateTime.UtcNow,
            Children = new List<FolderSummary>(), Subtotals = new List<FolderSummary>(),
            DependencyPaths = new List<string> { path }, Issues = new List<ScanIssue>(), TopFiles = new List<FileSummary>() };
    }

    private static void Until(IconContext host, Func<bool> condition, string message)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(7);
        while (!condition())
        { host.CheckFailure(); if (DateTime.UtcNow > until) throw new Exception(message + "; " + host.Activity); Thread.Sleep(10); }
        host.CheckFailure();
    }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
}

namespace PastaGBIcons
{
    internal sealed partial class IconContext
    {
        private readonly Dictionary<string, SizeRecord> offeredCache = new Dictionary<string, SizeRecord>(StringComparer.OrdinalIgnoreCase);

        private bool TryRestoreFake(string path, out SizeRecord record)
        {
            lock (stateGate)
            {
                if (!offeredCache.TryGetValue(path, out record)) return false;
                offeredCache.Remove(path); return true;
            }
        }
        internal void OfferCachedValue(string requested, string recordPath, long bytes)
        {
            lock (stateGate) offeredCache[requested] = new SizeRecord { Path = recordPath, Bytes = bytes,
                Phase = SizePhase.Stale, FinishedUtc = DateTime.UtcNow.AddHours(-1),
                VolumeStamp = volumes.Capture(), Dependencies = new[] { recordPath } };
            ScanDemandChanged();
        }

        internal IconContext(bool ready, params string[] paths)
        {
            DesktopSnapshot snapshot = new DesktopSnapshot();
            foreach (string path in paths)
            {
                snapshot.Folders.Add(new HeadlessFolder { Path = path });
                if (ready) sizes[path] = new SizeRecord { Path = path, Bytes = 1000, Phase = SizePhase.Ready,
                    FinishedUtc = DateTime.UtcNow, VolumeStamp = volumes.Capture(), Dependencies = new[] { path } };
            }
            surfaces.Add(new SurfaceState { Snapshot = snapshot,
                Explorer = new ExplorerSurface { RootHandle = new IntPtr(1), Location = @"C:\" } });
            coordinator = new Thread(delegate()
            { try { ScanLoop(); } catch (Exception ex) { lock (stateGate) failure = ex; } }) { IsBackground = true };
            coordinator.Start();
        }
        internal long Passes { get { return Interlocked.Read(ref coordinatorPasses); } }
        internal long Publications { get { return Interlocked.Read(ref coordinatorPublications); } }
        internal long DemandBuilds { get { return Interlocked.Read(ref coordinatorDemandBuilds); } }
        internal int LastWait { get { return Volatile.Read(ref coordinatorLastWait); } }
        internal int WatchTrackCalls { get { return Volatile.Read(ref changes.TrackCalls); } }
        internal void SignalOnly() { WakeScanner(); }
        internal void RequestManualRefresh() { Interlocked.Increment(ref refreshGeneration); WakeScanner(); }
        internal void Resume() { paused = false; ScanDemandChanged(); }
        internal void ReplaceVisibleWithoutRevision(string path)
        {
            var snapshot = new DesktopSnapshot(); snapshot.Folders.Add(new HeadlessFolder { Path = path });
            lock (stateGate) surfaces[0] = new SurfaceState { Snapshot = snapshot,
                Explorer = new ExplorerSurface { RootHandle = new IntPtr(1), Location = @"C:\" } };
            WakeScanner();
        }
    }
}
