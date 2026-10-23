using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PastaGB;

// Real portable enumeration, including a logical alias supplied by the same
// resolver seam used in the existing suite. No Windows identity API is mocked
// as a successful native Windows execution.
internal static class ScannerPerformanceTests
{
    private static int checks;
    private static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "PastaGB-perf-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            FreshScanAfterMutations(temp);
            LogicalAliasTopFiles(temp);
            OversizedTopFileDiagnostic(temp);
            Console.WriteLine("PASS: " + checks + " checks; fresh rescans, exact totals and logical alias paths after metadata/path optimization.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private static void OversizedTopFileDiagnostic(string temp)
    {
        // Synthetic logical paths are intentional: a deeply chained alias can
        // grow beyond the physical path length. Exercise the detail budget
        // without attempting to create a 128 Ki-character path on disk.
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        Type stateType = typeof(ScanSession).GetNestedType("WorkerState", BindingFlags.NonPublic);
        Type frameType = typeof(ScanSession).GetNestedType("DirectoryFrame", BindingFlags.NonPublic);
        Type folderType = typeof(ScanSession).GetNestedType("MutableFolder", BindingFlags.NonPublic);
        object state = Activator.CreateInstance(stateType, members, null,
            new object[] { null, temp, DateTime.UtcNow, new ScanOptions(), new AliasResolver(temp, temp) }, null);
        object frame = Activator.CreateInstance(frameType, true);
        object summary = Activator.CreateInstance(folderType, true);
        FieldInfo path = folderType.GetField("Path", members);
        path.SetValue(summary, temp);
        frameType.GetField("Summary", members).SetValue(frame, summary);
        MethodInfo consider = stateType.GetMethod("ConsiderTopFile", members);
        for (int i = 0; i < 100; i++)
            consider.Invoke(state, new object[] { frame, new FileInfo(Path.Combine(temp, "item" + i)), 100L });
        path.SetValue(summary, new String('p', 131072));
        FileInfo candidate = new FileInfo(Path.Combine(temp, "oversized"));
        consider.Invoke(state, new object[] { frame, candidate, 1L });
        var issues = (System.Collections.Generic.List<ScanIssue>)stateType.GetField("issues", members).GetValue(state);
        var top = (System.Collections.Generic.List<FileSummary>)stateType.GetField("topHeap", members).GetValue(state);
        Check(issues.Count == 1 && issues[0].Message.Contains("arquivos maiores"), "oversized rejected top-file candidate retains detail-budget diagnostic");
        Check(top.Count == 100 && top.All(x => x.Bytes == 100), "oversized rejected candidate cannot displace valid top files");
        consider.Invoke(state, new object[] { frame, candidate, 1L });
        Check(issues.Count == 1, "oversized detail diagnostic remains bounded to one issue");
    }

    private static void FreshScanAfterMutations(string temp)
    {
        string root = Path.Combine(temp, "fresh"); string child = Path.Combine(root, "Subpasta ç");
        Directory.CreateDirectory(child);
        string first = Path.Combine(root, "arquivo á.bin"); string second = Path.Combine(child, "arquivo β.bin");
        Write(first, 11); Write(second, 23);
        ScanResult one = Finish(Scanner.Start(root));
        Check(one.TotalBytes == 34 && one.FileCount == 2 && !one.IsPartial, "first enumeration exact");
        Check(one.TopFiles.Any(x => x.Path == first) && one.TopFiles.Any(x => x.Path == second), "normal paths and Unicode preserved");
        Write(first, 101); Write(second, 7);
        ScanResult two = Finish(Scanner.Start(root));
        Check(two.TotalBytes == 108 && two.FileCount == 2 && !two.IsPartial, "fresh enumeration observes growth and shrink");
        Check(two.Subtotals.Single().Bytes == 7, "fresh subtotal sees modified length");
        File.Delete(first);
        string renamed = Path.Combine(child, "renomeado β.bin"); File.Move(second, renamed);
        Write(Path.Combine(root, "novo.bin"), 41);
        ScanResult three = Finish(Scanner.Start(root));
        Check(three.TotalBytes == 48 && three.FileCount == 2 && !three.IsPartial, "deleted/renamed/created entries reconciled by next scan");
        Check(three.TopFiles.Any(x => x.Path == renamed) && !three.TopFiles.Any(x => x.Path == first || x.Path == second), "removed paths never leak between scans");
        File.Delete(renamed); File.Delete(Path.Combine(root, "novo.bin"));
        ScanResult empty = Finish(Scanner.Start(root));
        Check(empty.TotalBytes == 0 && empty.FileCount == 0 && !empty.IsPartial, "fresh zero after deleting files is verified");
        Check(empty.TopFiles.Count == 0 && empty.Subtotals.Single().Bytes == 0, "fresh details remain empty");
    }

    private static void LogicalAliasTopFiles(string temp)
    {
        string physical = Path.Combine(temp, "destino-fisico");
        string logical = Path.Combine(temp, "alias-logico-ç");
        Directory.CreateDirectory(physical); Directory.CreateDirectory(logical);
        string deep = Path.Combine(physical, "Pasta á"); Directory.CreateDirectory(deep);
        long expected = 0;
        for (int i = 0; i < 350; i++)
        {
            int bytes = i < 150 ? 10000 : 1;
            string name = i.ToString("D3") + "-" + new String('x', 80) + ".bin";
            Write(Path.Combine(deep, name), bytes); expected += bytes;
        }
        ScanResult result = Finish(Scanner.StartForTest(logical, new ScanOptions(), new AliasResolver(logical, physical)));
        Check(result.TotalBytes == expected && result.FileCount == 350 && !result.IsPartial, "alias aggregate includes all small rejected heap candidates");
        Check(result.RootPath == logical, "alias root remains requested path");
        Check(result.TopFiles.Count == 100 && result.TopFiles.All(x => x.Bytes == 10000), "bounded heap keeps largest aliases");
        Check(result.TopFiles[0].Name.StartsWith("000-") && result.TopFiles[99].Name.StartsWith("099-"), "tie ordering unaffected by deferred path construction");
        Check(result.TopFiles.All(x => x.Path.StartsWith(logical + Path.DirectorySeparatorChar, StringComparison.Ordinal)), "all top paths remain logical");
        Check(result.TopFiles.All(x => !x.Path.StartsWith(physical, StringComparison.Ordinal)), "no physical path substituted for alias");
        Check(result.Subtotals.Single().Path == Path.Combine(logical, "Pasta á"), "descendant subtotal path remains logical");
        Check(result.Children.Single(x => !x.IsDirectFiles).Path == Path.Combine(logical, "Pasta á"), "child detail path remains logical");
        Check(result.DependencyPaths.Contains(logical) && result.DependencyPaths.Contains(physical), "watcher dependencies retained");
    }

    private sealed class AliasResolver : IDirectoryResolver
    {
        private readonly string logical, physical;
        internal AliasResolver(string requested, string target) { logical = requested; physical = target; }
        public ResolvedDirectory Resolve(string path, ScanOptions options)
        {
            bool linked = path == logical;
            string target = linked ? physical : DirectoryResolvers.Normalize(path);
            ResolvedDirectory result = new ResolvedDirectory
            {
                SourcePath = path, PhysicalPath = target, Identity = target, VolumeIdentity = "fixture-volume", HasLink = linked
            };
            if (linked) result.Dependencies.Add(physical);
            return result;
        }
        public bool IsSame(ResolvedDirectory directory, ScanOptions options)
        { return Directory.Exists(directory.PhysicalPath) && Resolve(directory.SourcePath, options).Identity == directory.Identity; }
    }

    private static ScanResult Finish(ScanSession session)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!session.Completed) { if (DateTime.UtcNow > deadline) throw new Exception("Scanner timed out."); Thread.Sleep(1); }
        return session.Result;
    }
    private static void Write(string path, long bytes) { using (FileStream file = File.Create(path)) file.SetLength(bytes); }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
}
