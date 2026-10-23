using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using PastaGB;
using PastaGBIcons;

// Compiles the production coordinator unchanged. Only UI, transport, watch
// services and its sleep interval are controlled adapters. No disk/UI speed
// claim can be inferred from this scheduling-only, Linux/.NET 8 benchmark.
internal static class CoordinatorPerformanceBench
{
    private static int Main(string[] args)
    {
        try
        {
            string scenario = args.Length > 0 ? args[0] : "fresh";
            int count = args.Length > 1 ? Int32.Parse(args[1]) : 2048;
            int iterations = args.Length > 2 ? Int32.Parse(args[2]) : 200;
            IconContext host = new IconContext(scenario, count, iterations);
            host.Run();
            Console.WriteLine(host.Report());
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

namespace PastaGB
{
    public sealed class ProcessScanSession : IDisposable
    {
        private static readonly List<ProcessScanSession> sessions = new List<ProcessScanSession>();
        private bool disposed;
        public static int Started;
        public static int AvailableSlots { get { return 2 - sessions.Count(x => !x.disposed); } }
        public static int QuarantinedProcessCount { get { return 0; } }
        public bool Completed { get { return false; } }
        public ScanResult Result { get { return null; } }
        public string Diagnostic { get { return "benchmark adapter"; } }
        public static ProcessScanSession Start(string path, ScanOptions options, int seconds, int cpu)
        {
            if (AvailableSlots <= 0) throw new Exception("Reader limit exceeded");
            ProcessScanSession session = new ProcessScanSession(); sessions.Add(session); Started++; return session;
        }
        public ScanProgress GetProgress() { return new ScanProgress { StartedUtc = DateTime.UtcNow }; }
        public void Cancel() { }
        public void Dispose() { disposed = true; }
    }
}

namespace PastaGBIcons
{
    internal enum SizeUnits { Automatic, Gigabytes }
    internal enum PerformanceMode { Balanced, Economical }
    // Same scheduling configuration as balanced production; zero sleep lets
    // the harness measure work per pass instead of a deliberate 175 ms wait.
    internal sealed class AppPreferences
    {
        internal PerformanceMode Performance = PerformanceMode.Balanced;
        internal bool FollowDirectoryLinks = true, CrossVolumeLinks = true;
        internal int MaximumReaders { get { return 2; } }
        internal int ReaderCpuPercent { get { return 15; } }
        internal int EntriesPerSecond { get { return 12000; } }
        internal int ReconcileSeconds { get { return 300; } }
        internal int ScanInterval { get { return 0; } }
    }
    internal sealed class FolderChangeBatch
    {
        internal bool ReconcileAll = false;
        internal string[] Paths = new string[0];
        internal long Revision = 0;
    }
    internal sealed class FolderChangeTracker
    {
        internal long CoverageCalls;
        internal long Revision { get { return 0; } }
        internal void Track(IEnumerable<string> paths) { }
        internal bool IsCovered(string path)
        { CoverageCalls++; return PathRelations.Contains(@"C:\Perf", path); }
        internal FolderChangeBatch Drain() { return new FolderChangeBatch(); }
    }
    internal sealed class HeadlessFolder { internal string Path; }
    internal sealed class DesktopSnapshot { internal List<HeadlessFolder> Folders = new List<HeadlessFolder>(); }
    internal sealed class ExplorerSurface { internal IntPtr RootHandle; internal string Location; }
    internal sealed class SurfaceState { internal DesktopSnapshot Snapshot; internal ExplorerSurface Explorer; }
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
    internal sealed partial class IconContext
    {
        private readonly object stateGate = new object();
        private readonly VolumeRevisions volumes = new VolumeRevisions();
        private readonly FolderChangeTracker changes = new FolderChangeTracker();
        private AppPreferences preferences = new AppPreferences();
        private int policyGeneration = 0, refreshGeneration = 0;
        private volatile bool stopped = false, paused = false;
        private string scannerError, scannerActivity;
        private Dictionary<string, SizeRecord> sizes = new Dictionary<string, SizeRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SurfaceState> surfaces = new List<SurfaceState>();
        private readonly string scenario;
        private readonly int count, iterations;
        private int cycle;
        private const int Warmup = 40;
        private Stopwatch clock = new Stopwatch();
        private long allocatedStart, allocated, coverageStart, coverage;
        private double elapsed;

        internal IconContext(string scenario, int count, int iterations)
        {
            this.scenario = scenario; this.count = count; this.iterations = iterations;
            var snapshot = new DesktopSnapshot();
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < count; i++)
            {
                string path = @"C:\Perf\Folder" + i.ToString("D5");
                snapshot.Folders.Add(new HeadlessFolder { Path = path });
                if (scenario != "busy") sizes[path] = new SizeRecord
                {
                    Path = path, Phase = SizePhase.Ready, Bytes = i + 1,
                    FinishedUtc = scenario == "monitored" ? now.AddSeconds(-90) : now,
                    VolumeStamp = volumes.Capture(), Dependencies = new[] { path }
                };
            }
            surfaces.Add(new SurfaceState { Snapshot = snapshot,
                Explorer = new ExplorerSurface { RootHandle = new IntPtr(1), Location = @"C:\Perf" } });
        }
        internal void Run()
        {
            ScanLoop();
            if (scenario != "busy")
            {
                if (ProcessScanSession.Started != 0 || sizes.Count != count ||
                    sizes.Values.Any(x => x.Phase != SizePhase.Ready) ||
                    sizes.Values.Sum(x => x.Bytes) != (long)count * (count + 1) / 2)
                    throw new Exception("Ready totals or scheduler semantics changed");
            }
            else if (ProcessScanSession.Started != 2) throw new Exception("Busy fixture must keep exactly two readers");
            if (ProcessScanSession.AvailableSlots != 2) throw new Exception("Final reader cleanup failed");
        }
        private IntPtr GetForegroundWindow() { return new IntPtr(1); }
        private List<SurfaceState> CurrentSurfaces(bool rendering)
        {
            cycle++;
            if (cycle == Warmup)
            {
                allocatedStart = GC.GetAllocatedBytesForCurrentThread();
                coverageStart = changes.CoverageCalls; clock.Restart();
            }
            if (cycle == Warmup + iterations)
            {
                clock.Stop(); elapsed = clock.Elapsed.TotalMilliseconds;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
                coverage = changes.CoverageCalls - coverageStart;
                stopped = true;
            }
            return surfaces.ToList();
        }
        private void RecordEvent(string value) { }
        private void RequestCachedSizes(IEnumerable<string> wanted, AppPreferences settings, int policy) { }
        private bool TryRestoreCachedSize(string path, AppPreferences settings, int policy, out SizeRecord record)
        { record = null; return false; }
        private void RememberCompletedSize(SizeRecord record, ScanResult result, AppPreferences settings, int policy) { }
        private void PublishSizes(Dictionary<string, SizeRecord> source, int policy)
        { sizes = new Dictionary<string, SizeRecord>(source, StringComparer.OrdinalIgnoreCase); }
        internal string Report()
        {
            return "{\"scenario\":\"" + scenario + "\",\"folders\":" + count +
                ",\"iterations\":" + iterations + ",\"elapsed_ms\":" + elapsed.ToString("0.000", CultureInfo.InvariantCulture) +
                ",\"cpu_ms\":null" +
                ",\"allocated_bytes\":" + allocated + ",\"coverage_checks\":" + coverage +
                ",\"reads_started\":" + ProcessScanSession.Started + ",\"correctness_passed\":true}";
        }
    }
}
