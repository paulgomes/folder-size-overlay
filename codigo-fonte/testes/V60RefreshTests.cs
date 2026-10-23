using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using PastaGBIcons;

internal static class V60RefreshTests
{
    private static int assertions;
    private static void Check(bool result, string message)
    { assertions++; if (!result) throw new Exception(message); }

    public static int Main()
    {
        try
        {
            var policy = new AdaptiveRefreshPolicy();
            Check(policy.NextDelay(true, false, true, 250, 2000) == 250, "changed view stays responsive");
            int previous = 0;
            for (int i = 0; i < 16; i++)
            {
                int delay = policy.NextDelay(false, false, true, 250, 2000);
                Check(delay >= previous && delay <= 2000, "quiet view backs off within safety ceiling"); previous = delay;
            }
            Check(previous == 2000, "idle ceiling reached");
            Check(policy.NextDelay(false, true, true, 250, 2000) == 250, "pending identity keeps active cadence");
            Check(policy.NextDelay(false, false, false, 250, 2000) == 2000, "pause removes active polling");
            policy.Reset();
            Check(policy.NextDelay(false, false, true, 250, 2000) == 250, "native event resets idle backoff");
            Check(policy.NextDelay(true, false, true, -1, 2000) >= 50, "invalid settings cannot busy-spin");
            Check(AdaptiveRefreshPolicy.DrawingLeaseSeconds(2000, 1400) >= 4.4, "lease covers capture plus idle plus margin");
            Check(AdaptiveRefreshPolicy.DrawingLeaseSeconds(2000, 3000, 2) >= 9,
                "rotating Explorer visits account for first-to-last traversal across two rounds");
            Check(AdaptiveRefreshPolicy.DrawingLeaseSeconds(Int32.MaxValue, Int64.MaxValue) <= 10, "stalled capture cannot keep geometry indefinitely");

            var signal = new RefreshSignal();
            long observed = signal.Version;
            signal.Signal(); // arrived during capture, before Wait
            var clock = Stopwatch.StartNew();
            Check(signal.Wait(2000, observed) && clock.ElapsedMilliseconds < 1500, "wake before wait is not lost");
            observed = signal.Version;
            Check(!signal.Wait(1, observed), "coalesced event does not force a second stale capture");
            var ready = new ManualResetEvent(false);
            var thread = new Thread(delegate() { ready.Set(); Thread.Sleep(30); signal.Signal(); });
            thread.Start(); ready.WaitOne(); observed = signal.Version;
            Check(signal.Wait(2000, observed), "event wakes sleeping capture");
            thread.Join(); ready.Dispose();

            DesktopSnapshot a = View("C:\\A", 10), b = View("C:\\A", 10);
            Check(SurfaceSnapshotPolicy.SameDisplay(a, b), "identical snapshots reusable");
            Check(SurfaceSnapshotPolicy.SameDemand(a, b), "same demand reused");
            b.TextColor = 0x00123456;
            Check(!SurfaceSnapshotPolicy.SameDisplay(a, b), "appearance change requests redraw");
            b = View("C:\\A", 40);
            Check(!SurfaceSnapshotPolicy.SameDisplay(a, b) && SurfaceSnapshotPolicy.SameDemand(a, b), "scroll changes geometry without changing size identity");
            b = View("C:\\B", 10);
            Check(!SurfaceSnapshotPolicy.SameDisplay(a, b) && !SurfaceSnapshotPolicy.SameDemand(a, b), "same bounds never identify a replacement folder");
            b = View("C:\\A", 10); b.Visible = false;
            Check(!SurfaceSnapshotPolicy.SameDisplay(a, b) && !SurfaceSnapshotPolicy.SameDemand(a, b), "hidden host is not reused");
            b = View("C:\\A", 10); b.GeometryStamp = null;
            Check(!SurfaceSnapshotPolicy.SameDisplay(a, b), "missing proof cannot reuse display");
            Check(SurfaceSnapshotPolicy.SameDisplay(new DesktopSnapshot(), new DesktopSnapshot()),
                "two invisible unavailable snapshots can back off without a geometry stamp");
            DateTime now = DateTime.UtcNow;
            Check(SurfaceSnapshotPolicy.IsFresh(now.AddSeconds(-1), now, 3) &&
                !SurfaceSnapshotPolicy.IsFresh(now.AddSeconds(-5), now, 3), "bounded drawing eligibility");
            Check(!SurfaceSnapshotPolicy.IsFresh(now.AddSeconds(60), now, 3), "wall clock rollback cannot keep a future capture alive");

            using (var metrics = new RuntimeMetrics())
            {
                Check(RuntimeMetrics.CpuPercent(1, 2, 4) == 12.5, "CPU normalized to machine capacity");
                Check(RuntimeMetrics.CpuPercent(-1, 2, 4) == 0 && RuntimeMetrics.CpuPercent(1, 0, 4) == 0, "invalid counters cannot report invalid CPU");
                Check(RuntimeMetrics.CpuPercent(50, 1, 1) == 100, "counter saturation bounded");
                metrics.RecordCapture(false, 10); metrics.RecordCapture(true, 20); metrics.RecordRender(1);
                metrics.RecordRender(Double.NaN);
                Check(metrics.Summary.Contains("Capturas desktop: 1") && metrics.Summary.Contains("Passagens de desenho: 1"), "diagnostic counters and invalid-duration filtering");
                for (int i = 0; i < 1000; i++) metrics.AddSample(new RuntimeSample
                { Utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i), Renders = i });
                string csv = metrics.ExportCsv();
                Check(csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length == RuntimeMetrics.MaximumSamples + 1,
                    "measurement history bounded under long sessions");
                Check(csv.Contains(",999\r\n") && !csv.Contains(",main_process,0.0000,0,0,0,0,0,0,0,0\r\n"), "oldest metrics evicted and newest retained");
                Check(csv.StartsWith("utc,scope,") && csv.Contains("main_process"), "export declares measurement scope");
            }
            Console.WriteLine("PASS: " + assertions + " refresh, snapshot identity and bounded diagnostic checks; native Windows not executed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static DesktopSnapshot View(string path, int x)
    {
        var value = new DesktopSnapshot { HostHandle = new IntPtr(12), Visible = true, HostScreenBounds = new Rectangle(0, 0, 1000, 800) };
        value.Folders.Add(new DesktopFolderIcon { Path = path, Name = path, Bounds = new Rectangle(x, 10, 80, 40), ItemBounds = new Rectangle(x, 10, 80, 80) });
        value.ComputeGeometryStamp(null); return value;
    }
}
