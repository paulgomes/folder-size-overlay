using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using PastaGBIcons;

internal static class V60VisualTests
{
    private static int assertions;

    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); assertions++; }

    private static DesktopGeometryItem Item(string name, int x, int y)
    {
        return new DesktopGeometryItem { Name = name,
            HitBounds = new Rectangle(x, y, 80, 100), Bounds = new Rectangle(x, y, 80, 76) };
    }

    private static void NameSnapshotCases()
    {
        var source = new List<string> { "Fotos", "Vídeos", "Documentos", null, String.Empty };
        var index = new ProbeNameIndex(source);
        ProbeNameSnapshot first = index.Capture();
        Check(Object.ReferenceEquals(first, index.Capture()), "Stable captures share the immutable snapshot");
        Check(index.Count == 3 && first.Contains("fOtOs") && first.Contains("VÍDEOS"), "Ordinal case-insensitive lookup");
        Check(!first.Contains(null) && !first.Contains("") && !first.Contains("Outra"), "Missing names never match");
        source.Clear(); source.Add("Outra");
        Check(first.Contains("Fotos") && !first.Contains("Outra"), "Snapshot does not retain mutable caller collection");
        index.Exclude("DOCUMENTOS");
        ProbeNameSnapshot second = index.Capture();
        Check(!Object.ReferenceEquals(first, second), "A classification change publishes a new snapshot");
        Check(first.Contains("Documentos") && !second.Contains("Documentos"), "In-flight old request keeps its original mask");
        Check(second.Contains("Fotos") && second.Contains("Vídeos"), "Exclusion leaves unrelated names intact");
        index.Exclude("documentos"); index.Exclude(null); index.Exclude(""); index.Exclude("Absent");
        Check(Object.ReferenceEquals(second, index.Capture()), "No-op exclusions do not allocate another snapshot");
        Check(Object.ReferenceEquals(new ProbeNameIndex(new string[0]).Capture(), ProbeNameSnapshot.Empty), "Empty snapshot singleton");
        var duplicates = new ProbeNameIndex(new[] { "A", "a", "B", "B", "Único", "FOTOS", "Fotos" });
        Check(duplicates.Count == 1 && duplicates.Capture().Contains("ÚNICO"), "Only unambiguous names survive");
        Check(!duplicates.Capture().Contains("A") && !duplicates.Capture().Contains("B") &&
            !duplicates.Capture().Contains("Fotos"), "Case and exact duplicate names remain excluded");
        string[] names = new string[193];
        for (int i = 0; i < names.Length; i++) names[i] = "Pasta " + i.ToString("D5");
        index = new ProbeNameIndex(names); first = index.Capture();
        var removed = new HashSet<int>(new[] { 0, 1, 62, 63, 64, 65, 126, 127, 128, 191, 192 });
        foreach (int value in removed) index.Exclude(names[value]);
        second = index.Capture();
        for (int i = 0; i < names.Length; i++)
        {
            Check(first.Contains(names[i]), "Old immutable word mask retains every name " + i);
            Check(second.Contains(names[i]) != removed.Contains(i), "Mask boundaries do not corrupt neighbours " + i);
        }
        index = null;
        GC.Collect(); GC.WaitForPendingFinalizers();
        Check(first.Contains(names[64]) && !second.Contains(names[64]), "Snapshots survive producer reclamation");

        var unicode = new ProbeNameIndex(new[] { "𐐀", "名前", "Résumé", "i", "ı", "İ", "A\0B", "\ud800", "\ufffd" });
        ProbeNameSnapshot unicodeSnapshot = unicode.Capture();
        foreach (string name in new[] { "𐐀", "名前", "Résumé", "i", "ı", "İ", "A\0B", "\ud800", "\ufffd" })
            Check(unicodeSnapshot.Contains(name), "Provider string is preserved: " + name);
        Check(!unicodeSnapshot.Contains("A"), "Embedded null does not truncate provider name");

        string[] maximum = new string[ProbeNameIndex.MaximumNames];
        for (int i = 0; i < maximum.Length; i++) maximum[i] = "Folder " + i.ToString("D6");
        index = new ProbeNameIndex(maximum);
        Check(index.Count == maximum.Length && index.Capture().Contains(maximum[maximum.Length - 1]), "100000 names admitted");
        var excessive = new List<string>(maximum); excessive.Add("Too many");
        bool threw = false;
        try { new ProbeNameIndex(excessive); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "Unbounded name source rejected");
        Check(ProbeNameIndex.EstimateStorage(100000) >= 100000L * IntPtr.Size + 25000L,
            "Storage estimate accounts references plus mutable and immutable masks");
        Check(ProbeNameIndex.EstimateStorage(65) > ProbeNameIndex.EstimateStorage(64), "Storage estimate includes next mask word");
        Array.Reverse(names);
        var descending = new ProbeNameIndex(names, names.Length);
        foreach (string name in names)
            Check(descending.Capture().Contains(name), "Descending input takes the sorting path");
        var mixedCase = new ProbeNameIndex(new[] { "z", "a", "Á", "B", "b", "A", "1" }, 7);
        Check(mixedCase.Count == 3 && mixedCase.Capture().Contains("z") && mixedCase.Capture().Contains("Á") &&
            mixedCase.Capture().Contains("1"), "Unsorted duplicate groups are removed after sort");
        Check(!mixedCase.Capture().Contains("A") && !mixedCase.Capture().Contains("B"), "Sort does not accidentally keep ambiguous group");
    }

    private static void InFlightSnapshotCase()
    {
        string[] names = new string[4096];
        for (int i = 0; i < names.Length; i++) names[i] = "Row " + i.ToString("D4");
        var index = new ProbeNameIndex(names);
        ProbeNameSnapshot request = index.Capture();
        Exception failure = null;
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var reader = new Thread(delegate() {
            try
            {
                entered.Set();
                for (int pass = 0; pass < 8; pass++)
                    foreach (string name in names)
                        if (!request.Contains(name)) throw new Exception("Published mask mutated during provider read");
                release.Wait();
                foreach (string name in names)
                    if (!request.Contains(name)) throw new Exception("Old request changed after producer disposal");
            }
            catch (Exception error) { failure = error; }
        });
        reader.Start();
        Check(entered.Wait(5000), "MTA-like reader began");
        for (int i = 0; i < names.Length; i++)
        {
            index.Exclude(names[i]);
            if (i % 127 == 0) index.Capture();
        }
        ProbeNameSnapshot final = index.Capture();
        index = null; release.Set();
        Check(reader.Join(5000), "In-flight reader completes within bound");
        Check(failure == null, "Producer exclusions cannot mutate another thread's snapshot: " + failure);
        foreach (string name in names) Check(!final.Contains(name), "Latest request observes exclusion");
        entered.Dispose(); release.Dispose();
    }

    private static void MatchCases()
    {
        var photos = Item("Fotos", -1900, 20);
        var otherPhotos = Item("FOTOS", -1810, 20);
        var rows = new List<DesktopGeometryItem> { photos, otherPhotos, Item("Arquivo.txt", 20, 20) };
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fotos" };
        var index = new DesktopItemIndex(rows, folders);
        Check(index.Match("Fotos", false, Point.Empty, 1, 96) == null, "Duplicate visible names cannot match by name");
        Check(Object.ReferenceEquals(index.Match("Fotos", true, new Point(-1900, 20), 2, 96), photos),
            "Shell position disambiguates duplicate names and negative monitor coordinates");
        Check(index.Match("Fotos", true, new Point(-2000, 20), 1, 96) == null, "Position mismatch never falls back to name");
        Check(index.Match("Arquivo.txt", false, Point.Empty, 1, 96) == null, "Nonfolder names are absent from candidate index");
        rows.Clear();
        Check(Object.ReferenceEquals(index.Match("Fotos", true, new Point(-1900, 20), 2, 96), photos),
            "Capture index owns candidate references independently of caller list");
        rows.Add(photos);
        index = new DesktopItemIndex(rows, folders);
        Check(Object.ReferenceEquals(index.Match("fotos", false, Point.Empty, 1, 96), photos), "Unique name fallback retains casing semantics");
        Check(index.Match("Fotos", false, Point.Empty, 2, 96) == null, "Duplicate Shell names still forbid fallback");
        Check(Object.ReferenceEquals(index.Match("Fotos", true, new Point(-1907, 20), 1, 192), photos), "DPI-scaled position tolerance retained");
        Check(index.Match("Fotos", true, new Point(-1907, 20), 1, 96) == null, "Tolerance is not borrowed from another DPI");
        rows.Add(Item("Fotos", -1900, 20));
        index = new DesktopItemIndex(rows, folders);
        Check(index.Match("Fotos", true, new Point(-1900, 20), 1, 96) == null, "Overlapping duplicate positions remain ambiguous");
        Check(new DesktopItemIndex(new List<DesktopGeometryItem>(), folders).Match("Fotos", false, Point.Empty, 1, 96) == null,
            "Empty desktop remains empty");
    }

    private static void BaselineEquivalenceCases()
    {
        var random = new Random(600051);
        int[] dpis = { 96, 120, 144, 192, 240, 288 };
        for (int scene = 0; scene < 150; scene++)
        {
            var rows = new List<DesktopGeometryItem>();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 50 + random.Next(350); i++)
            {
                string name = (i % 3 == 0 ? "Pasta " : "pasta ") + random.Next(80).ToString();
                rows.Add(Item(name, random.Next(-2000, 2000), random.Next(-1000, 1500)));
                if (i % 2 == 0) folders.Add(name);
            }
            var index = new DesktopItemIndex(rows, folders);
            for (int query = 0; query < 120; query++)
            {
                DesktopGeometryItem chosen = rows[random.Next(rows.Count)];
                string name = query % 5 == 0 ? "Pasta ausente" : chosen.Name;
                if (query % 5 != 0 && !folders.Contains(name)) continue;
                var entry = new V60VisualBaseline.ShellEntry {
                    Name = name, HasPosition = query % 3 != 0,
                    Position = new Point(chosen.HitBounds.Left + random.Next(-10, 90), chosen.HitBounds.Top + random.Next(-10, 110))
                };
                int count = random.Next(1, 4), dpi = dpis[random.Next(dpis.Length)];
                DesktopGeometryItem before = V60VisualBaseline.FindMatch(entry, rows, count, dpi);
                DesktopGeometryItem after = index.Match(entry.Name, entry.HasPosition, entry.Position, count, dpi);
                Check(Object.ReferenceEquals(before, after), "Baseline5.1 equivalence scene=" + scene + ", query=" + query);
            }
            rows.Reverse();
            var reversed = new DesktopItemIndex(rows, folders);
            foreach (string name in folders)
                Check(Object.ReferenceEquals(index.Match(name, false, Point.Empty, 1, 96),
                    reversed.Match(name, false, Point.Empty, 1, 96)), "Enumeration order does not change unique matching");
        }
    }

    private static void Main()
    {
        NameSnapshotCases();
        InFlightSnapshotCase();
        MatchCases();
        BaselineEquivalenceCases();
        Console.WriteLine("{\"suite\":\"V60VisualTests\",\"assertions\":" + assertions +
            ",\"result\":\"PASS\",\"runtime_scope\":\"pure managed .NET 8; no Windows UIA or GDI execution\"}");
    }
}
