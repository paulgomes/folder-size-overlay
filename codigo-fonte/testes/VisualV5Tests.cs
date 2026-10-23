using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using PastaGBIcons;

internal static class VisualV5Tests
{
    private static int passed;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); passed++; }
    private static VisualRowAnchor Row(string name, int x, int y)
    { return new VisualRowAnchor { Name = name, Bounds = new Rectangle(x, y, 80, 20) }; }

    private static void SnapshotCases()
    {
        var a = Row("Fotos", 20, 40); var b = Row("Documentos", 20, 60);
        Check(VisualSnapshotPolicy.SameRows(new[] { a, b }, new[] { b, a }), "Enumeration order alone is harmless");
        Check(!VisualSnapshotPolicy.SameRows(new[] { a, b }, new[] { a, Row("Documentos", 20, 80) }), "Scroll rejects old positions");
        Check(!VisualSnapshotPolicy.SameRows(new[] { a }, new[] { Row("Vídeos", 20, 40) }), "Rename rejects old name mapping");
        Check(!VisualSnapshotPolicy.SameRows(new[] { a, b }, new[] { a, a }), "Duplicate multiplicity cannot hide a missing row");
        Check(!VisualSnapshotPolicy.SameRows(new[] { a }, new VisualRowAnchor[0]), "Empty folder replaces old content");
        Check(VisualSnapshotPolicy.SameRows(new VisualRowAnchor[0], new VisualRowAnchor[0]), "Stable empty snapshot accepted");
        VisualRowAnchor editor = Row(String.Empty, 20, 40); editor.Editor = true;
        Check(!VisualSnapshotPolicy.SameRows(new[] { a }, new[] { a, editor }), "New rename editor rejects unprotected frame");
        Check(!VisualSnapshotPolicy.SameRows(new[] { a }, null), "Missing provider snapshot is not an empty folder");
        Check(VisualSnapshotPolicy.SameRows(new[] { a, a }, new[] { a, a }), "Duplicate rows preserve occupancy only");
        Check(!VisualSnapshotPolicy.SameRows(new[] { a, editor }, new[] { a, Row(String.Empty, 20, 40) }), "Editor role preserved");
        Check(VisualSnapshotPolicy.SameRows(new[] { Row("A", -1920, 100) }, new[] { Row("A", -1920, 100) }), "Negative monitor coordinates preserved");
        var before = new List<VisualRowAnchor>(); var after = new List<VisualRowAnchor>();
        for (int i = 0; i < 10000; i++) before.Add(Row("Pasta " + i, i % 10 * 90, i / 10 * 25));
        for (int i = before.Count - 1; i >= 0; i--) after.Add(before[i]);
        Check(VisualSnapshotPolicy.SameRows(before, after), "Large frame reconciles without order dependence");
        after[5000] = Row("Outro item", 0, 0);
        Check(!VisualSnapshotPolicy.SameRows(before, after), "Large frame detects one changed item");
        List<int> order = VisualSnapshotPolicy.WindowVisitOrder(5, 3, 1);
        Check(order.Count == 5 && order[0] == 1 && order[1] == 3 && order[2] == 4 && order[3] == 0 && order[4] == 2,
              "Foreground first with rotated complete background traversal");
        Check(VisualSnapshotPolicy.WindowVisitOrder(0, 99, 99).Count == 0, "Empty Shell collection has no invalid modulo");
        Check(VisualSnapshotPolicy.WindowVisitOrder(1, 0, 0).Count == 1, "Foreground is not visited twice");
        for (int cursor = 0; cursor < 5; cursor++)
        {
            order = VisualSnapshotPolicy.WindowVisitOrder(5, cursor, 1);
            Check(new HashSet<int>(order).Count == 5 && order[0] == 1, "Every background window still gets visited");
        }
    }

    private static void BudgetCases()
    {
        var budget = new MetadataMemoryBudget(1024);
        Check(budget.TryReserve(500), "First metadata index admitted");
        Check(budget.TryReserve(500), "Second index admitted within aggregate cap");
        Check(!budget.TryReserve(25) && budget.Used == 1000, "Active plus building cannot exceed global cap");
        budget.Release(500);
        Check(budget.TryReserve(524) && budget.Used == 1024, "Disposed view releases budget for another view");
        Check(!budget.TryReserve(Int64.MaxValue), "Overflow-sized reservation rejected");
        budget.Release(1024);
        Check(budget.Used == 0, "All owned bytes released");
        bool threw = false;
        try { budget.Release(1); } catch (InvalidOperationException) { threw = true; }
        Check(threw && budget.Used == 0, "Accounting cannot silently underflow");
        Check(MetadataMemoryBudget.EstimateEntry(20, 80) > 80 + 40, "Managed overhead is included beyond raw PIDL bytes");
        var shared = new MetadataMemoryBudget(8);
        int accepted = 0;
        Thread[] threads = new Thread[24];
        ManualResetEventSlim release = new ManualResetEventSlim(false);
        CountdownEvent attempted = new CountdownEvent(threads.Length);
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(delegate() {
                bool reserved = shared.TryReserve(1);
                if (reserved) Interlocked.Increment(ref accepted);
                attempted.Signal(); release.Wait();
                if (reserved) shared.Release(1);
            });
            threads[i].Start();
        }
        Check(attempted.Wait(5000), "Concurrent reservations completed");
        Check(accepted == 8 && shared.Used == 8, "Concurrent callers cannot oversubscribe aggregate budget");
        release.Set();
        foreach (Thread t in threads) t.Join();
        release.Dispose(); attempted.Dispose();
        Check(shared.Used == 0, "Concurrent releases restore full capacity");
    }

    private static DesktopSnapshot Snapshot()
    {
        var result = new DesktopSnapshot { HostHandle = new IntPtr(123), Visible = true,
            Dpi = 144, HostScreenBounds = new Rectangle(-1920, 0, 1920, 1080) };
        result.OccupiedBounds.Add(new Rectangle(-1600, 100, 200, 80));
        result.Folders.Add(new DesktopFolderIcon { Path = @"C:\A\Fotos", Name = "Fotos",
            Bounds = new Rectangle(-1600, 100, 200, 60), ItemBounds = new Rectangle(-1600, 100, 200, 80) });
        return result;
    }

    private static void StampCases()
    {
        DesktopSnapshot a = Snapshot(); a.ComputeGeometryStamp(null);
        Check(a.GeometryStamp != null && a.GeometryStamp.Length == 64, "Visible geometry has a strong stamp");
        DesktopSnapshot b = Snapshot(); b.ComputeGeometryStamp(null);
        Check(a.GeometryStamp == b.GeometryStamp, "Identical captures keep a stable stamp");
        Action<DesktopSnapshot>[] changes = {
            x => x.HostHandle = new IntPtr(124),
            x => x.HostScreenBounds = new Rectangle(-1920, 1, 1920, 1080),
            x => x.Dpi = 192,
            x => x.Folders[0].Path = @"D:\A\Fotos",
            x => x.Folders[0].Name = "Fotos novas",
            x => x.Folders[0].Bounds = new Rectangle(-1600, 120, 200, 60),
            x => x.Folders[0].ItemBounds = new Rectangle(-1600, 100, 200, 85),
            x => x.Folders[0].IsEditing = true,
            x => x.OccupiedBounds.Add(new Rectangle(-1600, 181, 200, 24)),
            x => x.Folders.Clear()
        };
        foreach (Action<DesktopSnapshot> change in changes)
        {
            b = Snapshot(); change(b); b.ComputeGeometryStamp(null);
            Check(a.GeometryStamp != b.GeometryStamp, "Changed host/DPI/identity/name/position/editor/occupancy invalidates frame");
        }
        b = Snapshot(); b.TextColor = 0x00123456; b.Folders[0].IsSelected = true; b.ComputeGeometryStamp(null);
        Check(a.GeometryStamp == b.GeometryStamp, "Color/focus/selection alone does not invalidate geometry");
        var first = new[] { Row("\0name:C:\\A\\Fotos", 10, 20), Row("\0sizecell:C:\\A\\Fotos", 400, 20) };
        a = Snapshot(); a.ComputeGeometryStamp(first);
        b = Snapshot(); b.ComputeGeometryStamp(new[] { first[1], first[0] });
        Check(a.GeometryStamp == b.GeometryStamp, "Column dictionary enumeration order is immaterial");
        b.ComputeGeometryStamp(new[] { first[0], Row("\0sizecell:C:\\A\\Fotos", 500, 20) });
        Check(a.GeometryStamp != b.GeometryStamp, "Reordered or resized size column invalidates previous layout");
        b.ComputeGeometryStamp(new[] { first[0] });
        Check(a.GeometryStamp != b.GeometryStamp, "Missing proven size cell invalidates previous placement");
        string invalid = VisualSnapshotPolicy.CreateStamp(IntPtr.Zero, Rectangle.Empty, 96, new[] { Row("\ud800", 0, 0) });
        string replacement = VisualSnapshotPolicy.CreateStamp(IntPtr.Zero, Rectangle.Empty, 96, new[] { Row("\ufffd", 0, 0) });
        Check(invalid != replacement, "Provider strings are hashed without lossy encoding collisions");
    }

    private static void WorkerCases()
    {
        ManualResetEventSlim entered = new ManualResetEventSlim(false), release = new ManualResetEventSlim(false);
        var workerThreads = new HashSet<int>();
        var worker = new BoundedMtaWorker<string, string>("Teste de provider", delegate(string request) {
            lock (workerThreads) workerThreads.Add(Thread.CurrentThread.ManagedThreadId);
            if (request == "blocked") { entered.Set(); release.Wait(); }
            if (request == "fail") throw new InvalidOperationException("falha simulada");
            return request + "-result";
        });
        string value, error;
        Stopwatch clock = Stopwatch.StartNew();
        Check(!worker.TryExecute("blocked", 100, out value, out error), "Blocked native-like call has bounded caller wait");
        Check(clock.ElapsedMilliseconds < 1500 && value == null && error != null, "No fabricated result on timeout");
        Check(entered.Wait(1000), "Only accepted job starts");
        Check(!worker.TryExecute("queued", 100, out value, out error), "Busy worker refuses an extra queued job");
        Check(value == null, "Old or queued result never reused");
        release.Set();
        bool fresh = false;
        Stopwatch drain = Stopwatch.StartNew();
        do
        {
            fresh = worker.TryExecute("fresh", 1000, out value, out error);
            if (!fresh) Thread.Sleep(5);
        } while (!fresh && drain.ElapsedMilliseconds < 3000);
        Check(fresh && value == "fresh-result", "Late old result discarded; new request uses its own result");
        Check(!worker.TryExecute("fail", 1000, out value, out error) && value == null && error.Contains("falha simulada"), "Provider exception reported without result");
        Check(worker.TryExecute("after-failure", 1000, out value, out error) && value == "after-failure-result", "Worker recovers from provider exception");
        lock (workerThreads) Check(workerThreads.Count == 1, "Timeout and failure never spawn replacement threads");
        entered.Dispose(); release.Dispose();
    }

    private static void AppearanceAndLayoutCases()
    {
        var color = LabelPalette.Create(LabelTheme.Automatic, 0x00332211, false, Color.White, Color.Black);
        Check(color.Ink == Color.FromArgb(0x11, 0x22, 0x33), "Automatic preserves native COLORREF foreground");
        Check(!color.SolidBackground, "Automatic inline label stays visually quiet");
        var dark = LabelPalette.Create(LabelTheme.Dark, 0, false, Color.White, Color.Black);
        var light = LabelPalette.Create(LabelTheme.Light, 0xffffff, false, Color.White, Color.Black);
        Check(dark.Ink.R > dark.Fill.R && dark.SolidBackground, "Forced dark keeps its own readable surface");
        Check(light.Ink.R < light.Fill.R && light.SolidBackground, "Forced light keeps its own readable surface");
        foreach (LabelTheme theme in new[] { LabelTheme.Automatic, LabelTheme.Light, LabelTheme.Dark })
        {
            var contrast = LabelPalette.Create(theme, 0, true, Color.Yellow, Color.Navy);
            Check(contrast.Ink == Color.Yellow && contrast.Fill == Color.Navy && contrast.SolidBackground,
                  "High contrast overrides custom theme " + theme);
            Check(contrast.Shade == Color.Transparent, "High contrast has no decorative shadow " + theme);
        }
        Check(LabelLayout.NormalizeTextScale(Single.NaN) == 1f && LabelLayout.NormalizeTextScale(Single.PositiveInfinity) == 1f,
              "Invalid stored scale resets safely");
        Check(LabelLayout.NormalizeTextScale(-100) == 0.85f && LabelLayout.NormalizeTextScale(100) == 1.3f, "Scale range bounded");
        Rectangle host = new Rectangle(-4000, -2000, 8000, 6000);
        foreach (int dpi in new[] { 96, 120, 144, 192, 288 })
            foreach (float scale in new[] { 0.85f, 1f, 1.15f, 1.3f })
            {
                int k = dpi / 96;
                Rectangle own = new Rectangle(100, 100, 200 * k, 70 * k), placed;
                Check(LabelLayout.TryPlace(own, own, new[] { own }, null, host, dpi, scale, out placed), "Scaled underneath label places");
                Check(host.Contains(placed) && !placed.IntersectsWith(own), "Scaled label avoids its native name");
                Rectangle name = new Rectangle(120, 110, 60 * k, 14 * k);
                Rectangle row = new Rectangle(100, 100, 700 * k, 45 * k);
                Rectangle cell = new Rectangle(row.Right - 190 * k, row.Top, 180 * k, row.Height);
                Check(LabelLayout.TryPlaceInSizeCell(name, row, cell, new[] { row }, null, host, dpi, scale, out placed), "Scaled explicit size cell places");
                Check(cell.Contains(placed) && !placed.IntersectsWith(name), "Scaled size stays inside proven cell");
                Check(!LabelLayout.TryPlaceInSizeCell(name, row, cell, new[] { row, placed }, null, host, dpi, scale, out placed),
                      "Scaled label respects another occupied control");
            }
        Rectangle tooSmall = new Rectangle(0, 0, 65, 20), none;
        Check(!LabelLayout.TryPlaceInSizeCell(Rectangle.Empty, tooSmall, tooSmall, null, null, host, 96, 1.3f, out none),
              "Large text never overflows a narrow native cell");
    }

    private static int Main()
    {
        try
        {
            SnapshotCases(); StampCases(); BudgetCases(); WorkerCases(); AppearanceAndLayoutCases();
            Console.WriteLine("PASS: " + passed + " visual policy, bounded worker, metadata budget and appearance checks. No Windows UI executed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
