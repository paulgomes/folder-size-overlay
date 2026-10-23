using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PastaGB;

// Portable tests exercise traversal with deterministic directory identities.
// They do not execute any Windows P/Invoke or claim a native junction test.
public static class ScannerV5Tests
{
    private static int assertions;
    private static string root;

    public static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "PastaGB-v5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestNestedSubtotals();
            TestRootAliasAndDependencies();
            TestDuplicateTargetsAndCycles();
            TestCrossVolumePolicy();
            TestUnknownProvider();
            TestRootAndNestedReplacement();
            TestLimits();
            TestDependencyBudget();
            TestCancellation();
            TestOptionsSnapshot();
            TestRateBudgetAndCancellation();
            TestNativeTagParser();
            Console.WriteLine("PASS: " + assertions + " scanner-v5 deterministic assertions; Windows APIs not executed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestNestedSubtotals()
    {
        string path = Folder("subtotals");
        string a = Child(path, "A"); string deep = Child(a, "Deep"); string empty = Child(a, "Empty");
        string b = Child(path, "B");
        FileBytes(path, "root.bin", 7); FileBytes(a, "a.bin", 11); FileBytes(deep, "deep.bin", 23); FileBytes(b, "b.bin", 31);
        ScanResult r = Finish(Scanner.StartForTest(path, new ScanOptions(), new FakeResolver()));
        Check(r.TotalBytes == 72 && !r.IsPartial, "nested total stays exact");
        Check(r.Subtotals.Count == 4, "all finished descendants are exported");
        Check(r.Subtotals.Single(x => x.Path == a).Bytes == 34, "parent subtotal aggregates descendant bytes");
        Check(r.Subtotals.Single(x => x.Path == deep).Bytes == 23, "deep subtotal has own path and value");
        Check(r.Subtotals.Single(x => x.Path == empty).Bytes == 0 && !r.Subtotals.Single(x => x.Path == empty).IsPartial, "finished empty directory is a verified zero");
        Check(r.Subtotals.Single(x => x.Path == a).FolderCount == 2, "subtree counts exclude the directory itself");
        Check(r.DependencyPaths.Count == 1 && r.DependencyPaths[0] == path, "ordinary scan only depends on its root");
        Check(r.Children.Sum(x => x.Bytes) == r.TotalBytes, "compatible child aggregation");
    }

    private static void TestRootAliasAndDependencies()
    {
        string alias = Folder("root-alias"); string physical = Folder("root-target");
        string deep = Child(physical, "Deep"); FileBytes(deep, "bytes", 91);
        FakeResolver resolver = new FakeResolver(); resolver.Link(alias, physical, "volume-A");
        ScanResult r = Finish(Scanner.StartForTest(alias, new ScanOptions(), resolver));
        Check(r.RootPath == alias && r.TotalBytes == 91 && !r.IsPartial, "root alias resolves while retaining logical identity");
        Check(r.Subtotals.Single().Path == Path.Combine(alias, "Deep"), "alias descendants retain logical paths");
        Check(r.TopFiles.Single().Path == Path.Combine(alias, "Deep", "bytes"), "top file path remains logical");
        Check(r.DependencyPaths.Contains(alias) && r.DependencyPaths.Contains(physical), "root and external target dependencies included");
        ScanResult disabled = Finish(Scanner.StartForTest(alias, new ScanOptions { FollowDirectoryLinks = false }, resolver));
        Check(disabled.FatalError != null && disabled.IsPartial, "disabled root alias is explicit unavailable rather than zero");
    }

    private static void TestDuplicateTargetsAndCycles()
    {
        string path = Folder("duplicates"); string target = Folder("shared-target");
        string one = Child(path, "One"); string two = Child(path, "Two");
        FileBytes(target, "payload", 123);
        FakeResolver resolver = new FakeResolver(); resolver.Link(one, target, "volume-A"); resolver.Link(two, target, "volume-A");
        ScanResult r = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        Check(r.TotalBytes == 123 && r.FileCount == 1 && r.FolderCount == 1, "same target counted once");
        Check(r.SkippedLinks == 1 && r.IsPartial && r.FatalError == null, "duplicate target explained as partial coverage");
        Check(r.Children.Count(x => !x.IsDirectFiles && x.IsPartial) == 1, "duplicate alias has explicit partial flag");
        Check(r.Subtotals.Count == 1 && r.Subtotals[0].Bytes == 123, "deduplicated zero is never exported as a complete subtotal");

        string cycleRoot = Folder("cycle"); string branch = Child(cycleRoot, "Branch"); string cycle = Child(branch, "Back");
        FileBytes(branch, "data", 19); resolver = new FakeResolver(); resolver.Link(cycle, cycleRoot, "volume-A");
        r = Finish(Scanner.StartForTest(cycleRoot, new ScanOptions(), resolver));
        Check(r.TotalBytes == 19 && r.FileCount == 1 && r.SkippedLinks == 1, "ancestor cycle terminates without duplicate bytes");
        Check(!r.Subtotals.Any(x => x.Path == branch), "ancestor affected by traversal-context cycle is not reusable");
        Check(r.Issues.Any(x => x.Message.Contains("ciclo")), "cycle diagnostic is actionable");
    }

    private static void TestCrossVolumePolicy()
    {
        string path = Folder("cross-volume"); string alias = Child(path, "OtherDisk"); string target = Folder("volume-B-target");
        FileBytes(target, "content", 79);
        FakeResolver resolver = new FakeResolver(); resolver.Link(alias, target, "volume-B"); resolver.Volume(target, "volume-B");
        ScanResult allowed = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        Check(allowed.TotalBytes == 79 && !allowed.IsPartial, "cross-volume local directory included when enabled");
        Check(allowed.DependencyPaths.Contains(target), "external volume target tracked");
        ScanResult denied = Finish(Scanner.StartForTest(path, new ScanOptions { CrossVolumeLinks = false }, resolver));
        Check(denied.TotalBytes == 0 && denied.IsPartial && denied.SkippedLinks == 1, "cross-volume-disabled zero is explicitly partial");
        Check(denied.Subtotals.Count == 0, "skipped cross-volume alias not reused");
    }

    private static void TestUnknownProvider()
    {
        string path = Folder("providers"); string skipped = Child(path, "Unknown"); string safe = Child(path, "Safe");
        FileBytes(skipped, "must-not-read", 99); FileBytes(safe, "allowed", 17);
        FakeResolver resolver = new FakeResolver(); resolver.Reject(skipped);
        ScanResult r = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        Check(r.TotalBytes == 17 && r.SkippedLinks == 1 && r.IsPartial, "unknown provider subtree is not traversed");
        Check(!r.TopFiles.Any(x => x.Name == "must-not-read"), "no files returned from unrecognized destination");
        Check(r.Subtotals.Single().Path == safe, "unrelated finished subtree remains reusable");

        path = Folder("partial-subtotal"); string branch = Child(path, "Branch");
        skipped = Child(branch, "Unknown"); FileBytes(branch, "known", 29);
        resolver = new FakeResolver(); resolver.Reject(skipped);
        r = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        FolderSummary partial = r.Subtotals.Single(x => x.Path == branch);
        Check(partial.Bytes == 29 && partial.IsPartial && partial.SkippedLinks == 1,
            "context-independent skipped provider keeps explicit partial flag on a completed subtotal");
    }

    private static void TestRootAndNestedReplacement()
    {
        string path = Folder("root-replaced"); string child = Child(path, "Child"); FileBytes(child, "payload", 43);
        FakeResolver resolver = new FakeResolver(); resolver.ChangeAfterFirstVerification(path);
        ScanResult r = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        Check(r.FatalError != null && r.IsPartial, "root identity change yields explicit failure");
        Check(r.Subtotals.Count == 0, "root replacement invalidates all logical descendant results");

        path = Folder("nested-replaced"); child = Child(path, "Alias"); string target = Folder("nested-target");
        string deep = Child(target, "Deep"); FileBytes(deep, "payload", 47);
        resolver = new FakeResolver(); resolver.Link(child, target, "volume-A"); resolver.ChangeAfterFirstVerification(child);
        r = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        Check(r.FatalError != null && r.ErrorCount > 0 && r.IsPartial, "changed accepted alias cannot become a completed parent result");
        Check(!r.Subtotals.Any(x => x.Path.StartsWith(child, StringComparison.Ordinal)), "nested replacement removes previously completed logical descendants");

        path = Folder("link-changed-after-frame"); child = Child(path, "Alias"); target = Folder("link-final-target");
        deep = Child(target, "Deep"); FileBytes(deep, "payload", 59);
        resolver = new FakeResolver(); resolver.Link(child, target, "volume-A");
        resolver.ChangeAfterTwoVerifications(child);
        r = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        Check(r.TotalBytes == 59 && r.FatalError != null && r.IsPartial,
            "alias changed after its completed frame is rejected before root publication");
        Check(r.Subtotals.Count == 0 && r.Issues.Any(x => x.Message.Contains("após a leitura da subpasta")),
            "final alias revalidation removes earlier exported descendant values");
    }

    private static void TestLimits()
    {
        string path = Folder("depth"); string a = Child(path, "A"); string b = Child(a, "B");
        FileBytes(a, "one", 5); FileBytes(b, "two", 13);
        ScanResult r = Finish(Scanner.StartForTest(path, new ScanOptions { MaxDepth = 1 }, new FakeResolver()));
        Check(r.TotalBytes == 5 && r.IsPartial && r.ErrorCount == 1, "depth cap gives an honest partial value");
        Check(!r.Subtotals.Any(x => x.Path == a), "depth-relative partial parent is not independently reusable");

        path = Folder("directory-budget"); a = Child(path, "A"); b = Child(path, "B");
        FileBytes(a, "one", 7); FileBytes(b, "two", 7);
        r = Finish(Scanner.StartForTest(path, new ScanOptions { MaxDirectories = 2 }, new FakeResolver()));
        Check(r.TotalBytes == 7 && r.FolderCount == 1 && r.IsPartial, "directory budget includes root and stops bounded work");
        Check(r.Subtotals.Count == 1, "only completed subtree exported after directory budget");

        path = Folder("entry-budget"); for (int i = 0; i < 6; i++) FileBytes(path, "f" + i, 9);
        r = Finish(Scanner.StartForTest(path, new ScanOptions { MaxEntries = 3 }, new FakeResolver()));
        Check(r.TotalBytes == 27 && r.FileCount == 3 && r.IsPartial, "entry budget stops exactly at allowed entries");
        Check(r.Issues.Any(x => x.Message.Contains("limite de itens")), "entry limit reason included");

        path = Folder("detail-budget");
        for (int i = 0; i < 8; i++) FileBytes(Child(path, "D" + i), "one", 1);
        r = Finish(Scanner.StartForTest(path, new ScanOptions { MaxSubtotals = 2 }, new FakeResolver()));
        Check(r.TotalBytes == 8 && !r.IsPartial, "detail-output cap does not falsely mark a complete byte sum incomplete");
        Check(r.Subtotals.Count == 2 && r.Children.Count == 3, "both summary output lists bounded");
        Check(r.Issues.Any(x => x.Message.Contains("subtotais")) && r.Issues.Any(x => x.Message.Contains("detalhes")), "detail omissions explicitly documented");
    }

    private static void TestDependencyBudget()
    {
        string path = Folder("dependency-budget"); FakeResolver resolver = new FakeResolver();
        for (int i = 0; i < 70; i++)
        {
            string alias = Child(path, "L" + i.ToString("D2")); string target = Folder("external-" + i);
            FileBytes(target, "payload", 1); resolver.Link(alias, target, "volume-A");
        }
        ScanResult r = Finish(Scanner.StartForTest(path, new ScanOptions(), resolver));
        Check(r.DependencyPaths.Count == 64, "dependency count never exceeds 64 including root");
        Check(r.TotalBytes == 63 && r.SkippedLinks == 7 && r.IsPartial, "untrackable aliases skipped before reading their targets");
        Check(r.Subtotals.Count == 63 && r.Subtotals.Sum(x => x.Bytes) == 63, "no result from an untracked target is reusable");
    }

    private static void TestCancellation()
    {
        string path = Folder("cancel");
        for (int i = 0; i < 64; i++) FileBytes(Child(path, "D" + i), "payload", 1);
        FakeResolver resolver = new FakeResolver(); resolver.DelayMilliseconds = 3;
        ScanSession session = Scanner.StartForTest(path, new ScanOptions(), resolver);
        Thread.Sleep(15); session.Cancel(); ScanResult r = Finish(session);
        Check(r.Cancelled && r.IsPartial && r.FileCount < 64, "cancel stops active traversal");
        Check(r.Subtotals.All(x => x.Bytes == 1 && !x.IsPartial), "only fully read descendants survive cancellation");
    }

    private static void TestOptionsSnapshot()
    {
        ScanOptions options = new ScanOptions { MaxDepth = -7, MaxDirectories = Int32.MaxValue, MaxEntries = 0,
            MaxSubtotals = Int32.MaxValue, MaxEntriesPerSecond = 0 };
        ScanOptions copy = options.Snapshot();
        Check(copy.MaxDepth == 1 && copy.MaxDirectories == 100000 && copy.MaxEntries == 1 && copy.MaxSubtotals == 4096, "option clamps are explicit and finite");
        options.MaxDepth = 12; Check(copy.MaxDepth == 1, "copied options cannot change during scan");
        Check(new ScanOptions().FollowDirectoryLinks && new ScanOptions().CrossVolumeLinks, "explicit new API defaults include local links");
        Check(copy.MaxEntriesPerSecond == 100 && new ScanOptions().MaxEntriesPerSecond == 12000, "rate limit defaults and lower clamp");
        options.MaxEntriesPerSecond = Int32.MaxValue;
        Check(options.Snapshot().MaxEntriesPerSecond == 50000, "rate limit upper clamp");
    }

    private static void TestRateBudgetAndCancellation()
    {
        ScanRateBudget budget = new ScanRateBudget(100, 0); int wait;
        for (int i = 0; i < 10; i++) Check(budget.TryTake(0, out wait), "bounded initial burst entry " + i);
        Check(!budget.TryTake(0, out wait) && wait == 10, "depleted low-rate budget asks for exactly next token time");
        Check(!budget.TryTake(9, out wait) && wait == 1, "fractional token is not spent early");
        Check(budget.TryTake(10, out wait) && wait == 0, "token becomes available at configured rate");
        for (int i = 0; i < 10; i++) Check(budget.TryTake(3600000, out wait), "idle burst remains finite " + i);
        Check(!budget.TryTake(3600000, out wait), "long idle does not accumulate unlimited disk work");
        Check(!budget.TryTake(3599999, out wait), "backward time does not manufacture tokens");

        budget = new ScanRateBudget(12000, 0); int accepted = 0;
        for (long time = 0; time <= 1000; time++)
            while (budget.TryTake(time, out wait)) accepted++;
        Check(accepted == 13200, "one second is bounded by sustained rate plus the explicit 100 ms burst");

        string path = Folder("rate-cancel"); for (int i = 0; i < 100; i++) FileBytes(path, "f" + i, 1);
        ScanSession session = Scanner.StartForTest(path, new ScanOptions { MaxEntriesPerSecond = 100 }, new FakeResolver());
        Thread.Sleep(80); DateTime cancelledAt = DateTime.UtcNow; session.Cancel(); ScanResult r = Finish(session);
        Check(r.Cancelled && r.IsPartial && r.FileCount < 50, "cancelling paced work stops enumeration before full scan");
        Check((DateTime.UtcNow - cancelledAt).TotalSeconds < 1, "pacing wait responds to cancellation without waiting a full rate window");
    }

    private static void TestNativeTagParser()
    {
        string target; bool relative;
        byte[] valid = Reparse(WindowsDirectoryResolver.SymbolicLinkTag, @"\??\C:\Data", false);
        Check(WindowsDirectoryResolver.TryReadLinkTarget(valid, valid.Length, out target, out relative) && target == @"\??\C:\Data" && !relative, "symlink substitute-name parsed");
        valid = Reparse(WindowsDirectoryResolver.SymbolicLinkTag, @"..\Data", true);
        Check(WindowsDirectoryResolver.TryReadLinkTarget(valid, valid.Length, out target, out relative) && relative, "relative symlink flag parsed");
        valid = Reparse(WindowsDirectoryResolver.MountPointTag, @"\??\Volume{00000000-0000-0000-0000-000000000001}\", false);
        Check(WindowsDirectoryResolver.TryReadLinkTarget(valid, valid.Length, out target, out relative) && !relative, "mount-point substitute-name parsed");
        byte[] cloud = Reparse(0x9000001A, "unused", false);
        Check(!WindowsDirectoryResolver.TryReadLinkTarget(cloud, cloud.Length, out target, out relative), "cloud tag fails closed");
        byte[] unknown = Reparse(0x80000099, "unused", false);
        Check(!WindowsDirectoryResolver.TryReadLinkTarget(unknown, unknown.Length, out target, out relative), "unknown tag fails closed");
        valid = Reparse(WindowsDirectoryResolver.SymbolicLinkTag, "X", false);
        Put16(valid, 10, 3000);
        Check(!WindowsDirectoryResolver.TryReadLinkTarget(valid, valid.Length, out target, out relative), "out-of-buffer target rejected");
        valid = Reparse(WindowsDirectoryResolver.SymbolicLinkTag, "X", false); Put16(valid, 8, 1);
        Check(!WindowsDirectoryResolver.TryReadLinkTarget(valid, valid.Length, out target, out relative), "unaligned UTF16 target rejected");
        valid = Reparse(WindowsDirectoryResolver.SymbolicLinkTag, "X", false); valid[16] = 2;
        Check(!WindowsDirectoryResolver.TryReadLinkTarget(valid, valid.Length, out target, out relative), "unknown flags rejected");
        valid = Reparse(WindowsDirectoryResolver.SymbolicLinkTag, "A\0B", false);
        Check(!WindowsDirectoryResolver.TryReadLinkTarget(valid, valid.Length, out target, out relative), "embedded null rejected");
        for (int length = 0; length < 20; length++)
            Check(!WindowsDirectoryResolver.TryReadLinkTarget(new byte[20], length, out target, out relative), "truncated reparse header " + length);
    }

    private static byte[] Reparse(uint tag, string target, bool relative)
    {
        byte[] payload = Encoding.Unicode.GetBytes(target); int start = tag == WindowsDirectoryResolver.MountPointTag ? 16 : 20;
        byte[] data = new byte[start + payload.Length]; Array.Copy(BitConverter.GetBytes(tag), data, 4);
        Put16(data, 4, data.Length - 8); Put16(data, 8, 0); Put16(data, 10, payload.Length);
        if (start == 20) data[16] = relative ? (byte)1 : (byte)0;
        Array.Copy(payload, 0, data, start, payload.Length); return data;
    }
    private static void Put16(byte[] data, int position, int value) { Array.Copy(BitConverter.GetBytes((ushort)value), 0, data, position, 2); }

    private sealed class FakeResolver : IDirectoryResolver
    {
        private sealed class Alias { internal string Target, Volume; }
        private readonly Dictionary<string, Alias> aliases = new Dictionary<string, Alias>(DirectoryResolvers.PathComparer);
        private readonly Dictionary<string, string> volumes = new Dictionary<string, string>(DirectoryResolvers.PathComparer);
        private readonly HashSet<string> rejected = new HashSet<string>(DirectoryResolvers.PathComparer);
        private readonly Dictionary<string, int> changed = new Dictionary<string, int>(DirectoryResolvers.PathComparer);
        internal int DelayMilliseconds;
        internal void Link(string alias, string target, string volume) { aliases[alias] = new Alias { Target = target, Volume = volume }; }
        internal void Volume(string target, string volume) { volumes[target] = volume; }
        internal void Reject(string path) { rejected.Add(path); }
        internal void ChangeAfterFirstVerification(string path) { changed[path] = 0; }
        internal void ChangeAfterTwoVerifications(string path) { changed[path] = -1; }

        public ResolvedDirectory Resolve(string path, ScanOptions options)
        {
            if (DelayMilliseconds > 0) Thread.Sleep(DelayMilliseconds);
            path = DirectoryResolvers.Normalize(path);
            if (rejected.Contains(path)) throw new DirectoryResolutionException("Provedor desconhecido ignorado.", true);
            Alias alias; bool linked = aliases.TryGetValue(path, out alias);
            if (linked && !options.FollowDirectoryLinks) throw new DirectoryResolutionException("Links desativados.", true);
            string physical = linked ? alias.Target : path; string volume = linked ? alias.Volume : "volume-A";
            foreach (KeyValuePair<string, string> item in volumes) if (DirectoryResolvers.IsWithin(physical, item.Key)) volume = item.Value;
            if (!Directory.Exists(physical)) throw new IOException("Missing fixture directory.");
            ResolvedDirectory result = new ResolvedDirectory
            {
                SourcePath = path, PhysicalPath = physical, Identity = volume + "|" + physical,
                VolumeIdentity = volume, HasLink = linked
            };
            if (linked) result.Dependencies.Add(physical);
            return result;
        }
        public bool IsSame(ResolvedDirectory directory, ScanOptions options)
        {
            int calls;
            if (changed.TryGetValue(directory.SourcePath, out calls))
            { changed[directory.SourcePath] = calls + 1; if (calls >= 1) return false; }
            try { return Resolve(directory.SourcePath, options).Identity == directory.Identity; } catch { return false; }
        }
    }

    private static ScanResult Finish(ScanSession session)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(20);
        while (!session.Completed) { if (DateTime.UtcNow > end) throw new Exception("Scanner timed out."); Thread.Sleep(1); }
        return session.Result;
    }
    private static string Folder(string name) { return Child(root, name); }
    private static string Child(string parent, string name) { string path = Path.Combine(parent, name); Directory.CreateDirectory(path); return path; }
    private static void FileBytes(string path, string name, int bytes) { using (FileStream stream = File.Create(Path.Combine(path, name))) stream.SetLength(bytes); }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
}
