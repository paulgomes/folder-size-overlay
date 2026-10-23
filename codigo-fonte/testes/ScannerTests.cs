using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PastaGB;

// Standalone harness; test content is created only within a unique temp folder.
// The shipping scanner itself never creates or opens file contents.
public static class ScannerTests
{
    private static int assertions;

    public static int Main()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "PastaGB-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            TestEmpty(testRoot);
            TestNested(testRoot);
            TestTop100(testRoot);
            TestInvalidRoots(testRoot);
            TestLinksAndIssueBound(testRoot);
            TestCancellation(testRoot);
            Console.WriteLine("PASS: " + assertions + " assertions; empty/nested/counts/direct-files/top100/errors/links/issue-bound/cancellation.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(testRoot, true); }
            catch (Exception ex) { Console.Error.WriteLine("Temporary test cleanup failed: " + ex.Message); }
        }
    }

    private static void TestEmpty(string root)
    {
        string path = NewFolder(root, "empty");
        ScanResult result = Finish(Scanner.Start(path));
        Assert(result.FatalError == null && !result.IsPartial, "empty root succeeds");
        Assert(result.TotalBytes == 0 && result.FileCount == 0 && result.FolderCount == 0, "empty counters");
        Assert(result.Children.Count == 1 && result.Children[0].IsDirectFiles, "empty direct-files row exists");
        Assert(result.TopFiles.Count == 0, "empty top files");
        Assert(result.EndedUtc >= result.StartedUtc, "completion timestamps");
        Assert(Path.GetFullPath(path) == result.RootPath, "canonical path");
    }

    private static void TestNested(string root)
    {
        string path = NewFolder(root, "nested");
        string a = NewFolder(path, "A");
        string b = NewFolder(path, "B");
        string deep = NewFolder(a, "Deep");
        NewFolder(a, "Empty");
        WriteFile(path, "zero", 0);
        WriteFile(path, "direct.bin", 37);
        WriteFile(a, "first.bin", 101);
        WriteFile(deep, "second.bin", 202);
        WriteFile(b, ".hidden-file", 11);
        ScanSession session = Scanner.Start(path);
        ScanResult result = Finish(session);
        Assert(result.FatalError == null && !result.IsPartial, "nested root succeeds");
        Assert(result.TotalBytes == 351 && result.FileCount == 5 && result.FolderCount == 4, "recursive totals");
        FolderSummary aa = result.Children.Single(x => x.Name == "A");
        FolderSummary bb = result.Children.Single(x => x.Name == "B");
        FolderSummary direct = result.Children.Single(x => x.IsDirectFiles);
        Assert(aa.Bytes == 303 && aa.FileCount == 2 && aa.FolderCount == 2, "stable immediate child aggregation");
        Assert(bb.Bytes == 11 && bb.FileCount == 1 && bb.FolderCount == 0, "second subtree and hidden entry");
        Assert(direct.Bytes == 37 && direct.FileCount == 2 && direct.FolderCount == 0, "direct files aggregation");
        Assert(result.Children.Sum(x => x.Bytes) == result.TotalBytes, "child byte totals add up");
        Assert(result.TopFiles[0].Bytes == 202 && result.TopFiles[4].Bytes == 0, "top files sorted descending including zero");
        ScanProgress progress = session.GetProgress();
        Assert(progress.TotalBytes == result.TotalBytes && progress.FileCount == result.FileCount, "published final progress consistent");
        session.Cancel();
        Assert(!session.GetProgress().CancelRequested && !session.Result.Cancelled, "cancel after completion is a no-op");
    }

    private static void TestTop100(string root)
    {
        string path = NewFolder(root, "top100");
        string one = NewFolder(path, "one");
        string two = NewFolder(path, "two");
        for (int i = 0; i < 250; i++)
            WriteFile(i % 2 == 0 ? one : two, i.ToString("D3") + ".bin", i);
        ScanResult result = Finish(Scanner.Start(path));
        Assert(result.TopFiles.Count == 100, "global top 100 bounded");
        Assert(result.TopFiles[0].Bytes == 249 && result.TopFiles[99].Bytes == 150, "heap retains correct largest files");
        Assert(result.TotalBytes == 31125 && result.FileCount == 250, "top100 does not change totals");
        for (int i = 1; i < result.TopFiles.Count; i++)
            Assert(result.TopFiles[i - 1].Bytes >= result.TopFiles[i].Bytes, "top100 descending " + i);

        string ties = NewFolder(root, "ties");
        for (int i = 149; i >= 0; i--) WriteFile(ties, i.ToString("D3") + ".bin", 3);
        ScanResult tied = Finish(Scanner.Start(ties));
        Assert(tied.TopFiles.Count == 100, "tie top100 bounded");
        Assert(tied.TopFiles[0].Name == "000.bin" && tied.TopFiles[99].Name == "099.bin", "equal lengths use deterministic lexical ordering");
    }

    private static void TestInvalidRoots(string root)
    {
        ScanResult absent = Finish(Scanner.Start(Path.Combine(root, "absent")));
        Assert(absent.ErrorCount > 0 && absent.IsPartial && !String.IsNullOrEmpty(absent.FatalError), "missing folder is explicit failure");
        Assert(absent.Issues.Count > 0, "missing root has explanatory issue");
        string file = WriteFile(root, "ordinary-file", 10);
        ScanResult notFolder = Finish(Scanner.Start(file));
        Assert(notFolder.ErrorCount > 0 && notFolder.FatalError != null, "file root rejected");
        ScanResult blank = Finish(Scanner.Start(" "));
        Assert(blank.FatalError != null && blank.ErrorCount > 0, "blank root rejected without caller exception");
        ScanResult nullRoot = Finish(Scanner.Start(null));
        Assert(nullRoot.FatalError != null && nullRoot.ErrorCount > 0, "null root rejected without caller exception");
    }

    private static void TestCancellation(string root)
    {
        string path = NewFolder(root, "cancellation");
        // A broad tree makes immediate cancellation observable without large files.
        for (int i = 0; i < 1000; i++)
        {
            string dir = NewFolder(path, i.ToString("D4"));
            WriteFile(dir, "entry", 1);
        }
        ScanSession session = Scanner.Start(path);
        session.Cancel();
        ScanResult result = Finish(session);
        Assert(result.Cancelled && result.IsPartial, "cooperative cancellation reported");
        Assert(result.FileCount < 1000, "cancellation stops traversal early");
        Assert(result.Issues.Count <= 200 && result.TopFiles.Count <= 100, "cancelled result bounds retained");
        Assert(session.GetProgress().CancelRequested, "cancellation visible in snapshot");
    }

    private static void TestLinksAndIssueBound(string root)
    {
        // Reflective setup lets this harness still compile for Framework 4.x.
        // The portable QA runtime exposes these methods; older runtimes skip
        // link creation rather than requiring administrator/developer settings.
        MethodInfo createFileLink = typeof(File).GetMethod("CreateSymbolicLink", new Type[] { typeof(string), typeof(string) });
        MethodInfo createDirectoryLink = typeof(Directory).GetMethod("CreateSymbolicLink", new Type[] { typeof(string), typeof(string) });
        if (createFileLink == null || createDirectoryLink == null)
        {
            Console.WriteLine("SKIP: symbolic-link fixture creation is not exposed by this runtime.");
            return;
        }
        string path = NewFolder(root, "links");
        string physical = NewFolder(path, "physical");
        string payload = WriteFile(physical, "payload.bin", 123);
        string directoryLink = Path.Combine(path, "directory-link");
        createDirectoryLink.Invoke(null, new object[] { directoryLink, physical });
        createDirectoryLink.Invoke(null, new object[] { Path.Combine(physical, "cycle"), path });
        createFileLink.Invoke(null, new object[] { Path.Combine(path, "file-link"), payload });
        for (int i = 0; i < 210; i++)
            createFileLink.Invoke(null, new object[] { Path.Combine(path, "link-" + i.ToString("D3")), payload });
        ScanResult result = Finish(Scanner.Start(path));
        Assert(result.FatalError == null && result.ErrorCount == 0, "ignored links do not become filesystem errors");
        Assert(result.IsPartial && result.SkippedLinks == 213, "file/directory/cyclic links all skipped");
        Assert(result.TotalBytes == 123 && result.FileCount == 1 && result.FolderCount == 1, "linked targets are not traversed or double-counted");
        Assert(result.Issues.Count == 200 && result.OmittedIssueCount == 13, "issue memory strictly bounded with omission count");
        Assert(result.Issues.All(x => x.IsSkippedLink), "link explanations are identified");
        Assert(result.TopFiles.Count == 1 && result.TopFiles[0].Bytes == 123, "top files exclude reparse entries");
        FolderSummary physicalSummary = result.Children.Single(x => x.Name == "physical");
        Assert(physicalSummary.SkippedLinks == 1 && physicalSummary.IsPartial, "nested skip is attributed to immediate subtree");
        FolderSummary direct = result.Children.Single(x => x.IsDirectFiles);
        Assert(direct.SkippedLinks == 211 && direct.Bytes == 0 && direct.FileCount == 0, "root file-link skips attributed to direct files");
        FolderSummary skippedDirectory = result.Children.Single(x => x.Name == "directory-link");
        Assert(skippedDirectory.SkippedLinks == 1 && skippedDirectory.IsPartial, "skipped immediate directory remains visible");
        ScanResult rootLink = Finish(Scanner.Start(directoryLink));
        Assert(rootLink.FatalError != null && rootLink.ErrorCount > 0 && rootLink.IsPartial, "selected reparse root explicitly refused");
        Assert(rootLink.TotalBytes == 0 && rootLink.FileCount == 0, "reparse root target not traversed");
    }

    private static ScanResult Finish(ScanSession session)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!session.Completed)
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Scan did not complete within 15 seconds.");
            ScanProgress snapshot = session.GetProgress();
            Assert(snapshot.TotalBytes >= 0 && snapshot.FileCount >= 0, "progress snapshot nonnegative");
            Thread.Sleep(2);
        }
        Assert(session.Result != null, "result published before completion flag");
        return session.Result;
    }

    private static string NewFolder(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string folder, string name, int bytes)
    {
        string path = Path.Combine(folder, name);
        using (FileStream stream = File.Create(path)) stream.SetLength(bytes);
        return path;
    }

    private static void Assert(bool condition, string name)
    {
        assertions++;
        if (!condition) throw new Exception(name);
    }
}
