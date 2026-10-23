#pragma warning disable 0649
using System;
using System.Collections.Generic;
using System.Drawing;
using PastaGBIcons;

internal static class LayoutPlanCacheTests
{
    private static int checks, calls;
    private static readonly DateTime Now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; }
    private static DesktopFolderIcon Folder(string path, int y)
    { return new DesktopFolderIcon { Path = path, Name = path, Bounds = new Rectangle(0, y, 90, 40), ItemBounds = new Rectangle(0, y, 90, 40) }; }
    private static LabelPlacement Place(DesktopFolderIcon item, IList<Rectangle> used)
    {
        calls++;
        if (item.IsEditing) return new LabelPlacement { SuppressionReason = "editor" };
        Rectangle bounds;
        bool placed = LabelLayout.TryPlace(item.Bounds, item.ItemBounds, null, used,
            new Rectangle(-100, -100, 10000, 10000), 96, 1f, out bounds);
        return new LabelPlacement { Bounds = bounds, SuppressionReason = placed ? null : "conflict" };
    }
    private static LabelPlacement[] Get(LayoutPlanCache cache, string key, string stamp, IList<DesktopFolderIcon> folders)
    { return cache.GetOrCreate(key, stamp, 1, 1f, folders, Now, Place); }

    private static void CacheCases()
    {
        var cache = new LayoutPlanCache();
        var folders = new[] { Folder(@"C:\A", 0), Folder(@"C:\B", 100) };
        calls = 0;
        LabelPlacement[] first = Get(cache, "view", "stamp", folders);
        Check(first.Length == 2 && first[0].Placed && first[1].Placed && calls == 2, "First frame resolves all placements");
        Check(first[0].LabelKey == "view\u001FC:\\A", "Label key binds surface and exact path");
        var recaptured = new[] { Folder(@"C:\A", 0), Folder(@"C:\B", 100) };
        LabelPlacement[] second = Get(cache, "view", "stamp", recaptured);
        Check(Object.ReferenceEquals(first, second) && calls == 2 && cache.Hits == 1,
            "New snapshot objects with identical ordered geometry reuse the plan");
        recaptured[0].IsSelected = true;
        Check(Object.ReferenceEquals(first, Get(cache, "view", "stamp", recaptured)), "Selection alone preserves plan");
        second = Get(cache, "view", "stamp", new[] { recaptured[1], recaptured[0] });
        Check(!Object.ReferenceEquals(first, second) && second[0].LabelKey.EndsWith(@"C:\B"), "Order-independent stamp cannot misassociate indexed placements");
        int before = calls;
        Get(cache, "view", "new-stamp", folders);
        Check(calls == before + 2, "Changed viewport/columns/occupancy stamp rebuilds");
        before = calls;
        cache.GetOrCreate("view", "new-stamp", 4, 1f, folders, Now, Place);
        Check(calls == before + 2, "Changed view mode rebuilds");
        before = calls;
        cache.GetOrCreate("view", "new-stamp", 4, 1.3f, folders, Now, Place);
        Check(calls == before + 2, "Changed text scale rebuilds");
        before = calls;
        Get(cache, "other-view", "new-stamp", folders);
        Check(calls == before + 2 && cache.Count == 2, "Another surface cannot inherit the first plan");
        var changed = new[] { Folder(@"D:\A", 0), Folder(@"C:\B", 100) };
        before = calls; Get(cache, "other-view", "new-stamp", changed);
        Check(calls == before + 2, "Path difference independently rejects cached indexed identity");
        changed[0].IsEditing = true;
        second = Get(cache, "other-view", "new-stamp", changed);
        Check(!second[0].Placed && second[0].SuppressionReason == "editor", "Rename editor cannot reuse visible placement");
        changed[0].Bounds = new Rectangle(10, 10, 90, 40);
        before = calls; Get(cache, "other-view", "new-stamp", changed);
        Check(calls == before + 2, "Changed folder bounds independently reject cache");
        changed[0].ItemBounds = new Rectangle(0, 0, 100, 40);
        before = calls; Get(cache, "other-view", "new-stamp", changed);
        Check(calls == before + 2, "Changed own cell rejects cache");
        before = calls; Get(cache, "other-view", "new-stamp", new[] { changed[0] });
        Check(calls == before + 1, "Deleted folder cannot reuse a larger plan");
        cache.Clear();
        Check(cache.Count == 0 && cache.RetainedItems == 0 && cache.EstimatedBytes == 0, "Pause/shutdown release retained plan memory");
        first = Get(cache, "same", null, folders); second = Get(cache, "same", null, folders);
        Check(cache.Count == 0 && !Object.ReferenceEquals(first, second), "Missing verified geometry never enters cache");
        Get(cache, null, "stamp", folders);
        Check(cache.Count == 0, "Missing identity never enters cache");
        Get(cache, "same", "stamp", folders);
        cache.Trim(Now.AddSeconds(30)); Check(cache.Count == 1, "Short navigation gap retains bounded plan");
        cache.Trim(Now.AddSeconds(31)); Check(cache.Count == 0, "Unvisited plan expires after 30 seconds");
        var overlapping = new[] { Folder(@"C:\first", 0), Folder(@"C:\second", 0) };
        second = Get(cache, "same", "overlap", overlapping);
        Check(second[0].Placed && !second[1].Placed, "Plan reserves first placement even if native drawing later fails");
        Check(Object.ReferenceEquals(second, Get(cache, "same", "overlap", overlapping)), "Overlapping decision remains stable across repeated frames");
        cache.Clear();
        for (int i = 0; i < 30; i++) Get(cache, "window" + i, "stamp", folders);
        Check(cache.Count <= 16 && cache.RetainedItems <= 2048 && cache.EstimatedBytes <= 2 * 1024 * 1024,
            "Window churn stays within all cache budgets");
        cache.Clear();
        var many = new List<DesktopFolderIcon>();
        for (int i = 0; i < 1200; i++) many.Add(Folder(@"C:\many\" + i, i * 5));
        Get(cache, "big1", "stamp", many); Get(cache, "big2", "stamp", many);
        Check(cache.Count == 1 && cache.RetainedItems == 1200, "Aggregate item cap evicts another large view");
        cache.Clear();
        var hugeNames = new List<DesktopFolderIcon>();
        for (int i = 0; i < 40; i++) hugeNames.Add(Folder(new String('x', 20000) + i, i * 100));
        Get(cache, "huge", "stamp", hugeNames);
        Check(cache.Count == 0 && cache.EstimatedBytes == 0, "Path retention estimate rejects an oversized plan");
        Get(cache, "empty", "stamp", new DesktopFolderIcon[0]);
        Check(cache.Count == 1 && cache.RetainedItems == 0, "Empty view is a distinct bounded plan");
    }

    private static void CaptionCases()
    {
        SizeCaptionCache cache = new SizeCaptionCache();
        foreach (SizeUnits units in new[] { SizeUnits.Gigabytes, SizeUnits.Automatic })
            foreach (bool compact in new[] { true, false })
                foreach (SizePhase phase in (SizePhase[])Enum.GetValues(typeof(SizePhase)))
                    foreach (bool partial in new[] { true, false })
                    {
                        var value = new SizeRecord { Bytes = 12345678901L, Phase = phase, LastResultPartial = partial };
                        string expected = SizePresentation.Caption(value, units, compact);
                        string actual = cache.Get(value, units, compact);
                        Check(actual == expected, "Caption cache preserves units/state/partial/length presentation");
                        Check(Object.ReferenceEquals(actual, cache.Get(value.Copy(), units, compact)), "Equivalent replacement result reuses string");
                        value.Bytes++;
                        Check(cache.Get(value, units, compact) == SizePresentation.Caption(value, units, compact), "Changed bytes use formatter");
                    }
        Check(cache.Get(null, SizeUnits.Gigabytes, true) == "aguardando…", "Invalid media/null result clears prior numeric caption");
        var old = new SizeRecord { Bytes = 1000000000, Phase = SizePhase.Ready };
        Check(cache.Get(old, SizeUnits.Gigabytes, true) == "1 GB", "Valid result returns after unavailable media");
        old.Phase = SizePhase.Stale;
        Check(cache.Get(old, SizeUnits.Gigabytes, true).Contains("↻"), "Invalidation updates refresh marker immediately");
        old.Phase = SizePhase.Unavailable;
        Check(cache.Get(old, SizeUnits.Gigabytes, true) == "indisponível", "Unavailable result replaces stale caption");
    }

    private static int Main()
    {
        try { CacheCases(); CaptionCases(); Console.WriteLine("PASS: " + checks + " checks of bounded layout and caption reuse; no Windows UI execution."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
