using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PastaGB;

// SCAN_PROCESS_TESTS adds an internal launcher seam only to this executable.
// The shipping application never accepts an alternate executable/command line.
// These are real child processes on the host OS, NOT a Windows UI validation.
public static class ScanProcessTests
{
    private static int assertions;
    private static int cases;
    private static readonly string Host = Environment.GetEnvironmentVariable("PASTAGB_TEST_DOTNET");
    private static readonly string AssemblyPath = typeof(ScanProcessTests).Assembly.Location;

    public static int Main(string[] args)
    {
        if (ScanWorker.TryRun(args)) return Environment.ExitCode;
        if (args.Length == 2 && args[0] == "--test-child") return FakeChild(args[1]);
        string root = Path.Combine(Path.GetTempPath(), "PastaGB-process-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestProtocol(root);
            TestCpuLimitConfiguration();
            TestMemoryLimitConfiguration();
            TestInvalidRequest(root);
            TestLaunchFailure(root);
            TestSuccessfulProcess(root);
            TestCrash(root);
            TestInvalidChild(root, "malformed", "Versão");
            TestInvalidChild(root, "wrong-id", "outro pedido");
            TestInvalidChild(root, "wrong-root", "pasta solicitada");
            TestCooperativeCancel(root);
            TestTimeout(root, "no-progress", 600, 3000, 3000, "sem avanço");
            TestTimeout(root, "heartbeat-stops", 5000, 3000, 600, "deixou de responder");
            TestBlockedRequest(root);
            TestQuarantinedBudget(root);
            Assert(ProcessScanSession.OwnedProcessCount == 0, "all process slots released after confirmed exits");
            Console.WriteLine("PASS: " + assertions + " assertions in " + cases + " cases; protocol/real IPC/crash/cancel/deadlines/owned-worker budget.");
            Console.WriteLine(ProcessScanSession.DiagnosticSummary);
            Console.WriteLine("WINDOWS_UI_EXECUTED=false");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            Console.Error.WriteLine(ProcessScanSession.DiagnosticSummary);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestProtocol(string root)
    {
        cases++;
        Guid id = Guid.NewGuid();
        ScanOptions options = ScanProtocol.CopyOptions(new ScanOptions
        {
            FollowDirectoryLinks = true, CrossVolumeLinks = false, MaxDepth = 0,
            MaxDirectories = Int32.MaxValue, MaxEntries = 17, MaxSubtotals = 9, MaxEntriesPerSecond = 3000
        });
        Assert(options.MaxDepth == 1 && options.MaxDirectories == 100000, "options normalized before transmission");
        byte[] payload = ScanProtocol.Request(root, options);
        ScanFrame frame;
        using (MemoryStream stream = new MemoryStream())
        {
            ScanProtocol.WriteFrame(stream, ScanFrameKind.Request, id, payload);
            stream.Position = 0;
            frame = ScanProtocol.ReadFrame(stream);
            Assert(ScanProtocol.ReadFrame(stream) == null, "clean EOF between frames");
        }
        Assert(frame.RequestId == id && frame.Kind == ScanFrameKind.Request, "request ID/type round trip");
        ScanRequest request = ScanProtocol.ReadRequest(frame.Payload);
        Assert(request.Path == root && request.Options.MaxEntries == 17 && request.Options.FollowDirectoryLinks &&
            !request.Options.CrossVolumeLinks && request.Options.MaxEntriesPerSecond == 3000, "path/options/rate budget round trip");
        Assert(ScanProtocol.CopyOptions(new ScanOptions { MaxEntriesPerSecond = -1 }).MaxEntriesPerSecond == 100 &&
            ScanProtocol.CopyOptions(new ScanOptions { MaxEntriesPerSecond = Int32.MaxValue }).MaxEntriesPerSecond == 50000,
            "metadata entry rate normalized within agreed limits");

        ScanResult result = SampleResult(root);
        result.TotalBytes = 1234567890123L;
        result.ErrorCount = 2;
        result.SkippedLinks = 3;
        result.OmittedIssueCount = 7;
        result.Cancelled = true;
        result.FatalError = "Falha parcial de teste";
        result.Children.Add(new FolderSummary
        {
            Name = "Subpasta ç", Path = Path.Combine(root, "Subpasta ç"), Bytes = 123,
            FileCount = 4, FolderCount = 5, ErrorCount = 1, SkippedLinks = 2
        });
        result.Subtotals.Add(result.Children[0]);
        result.TopFiles.Add(new FileSummary { Name = "arquivo.bin", Path = Path.Combine(root, "arquivo.bin"), Bytes = 123 });
        result.Issues.Add(new ScanIssue { Path = root, Message = "Permissão", IsSkippedLink = true });
        result.DependencyPaths.Add(Path.Combine(root, "target"));
        bool omitted;
        ScanResult copy = ScanProtocol.ReadResult(ScanProtocol.Result(result), out omitted);
        Assert(copy.TotalBytes == result.TotalBytes && copy.FileCount == result.FileCount && copy.FolderCount == result.FolderCount,
            "64-bit totals survive IPC");
        Assert(copy.ErrorCount == 2 && copy.SkippedLinks == 3 && copy.OmittedIssueCount == 7 && copy.Cancelled && copy.IsPartial,
            "partial/cancellation semantics survive IPC");
        Assert(copy.StartedUtc == result.StartedUtc && copy.EndedUtc == result.EndedUtc && copy.FatalError == result.FatalError,
            "timestamps/failure survive IPC");
        Assert(copy.Children.Count == 1 && copy.Subtotals.Count == 1 && copy.Children[0].FolderCount == 5 &&
            copy.Subtotals[0].Path == result.Children[0].Path, "children/subtotals survive IPC");
        Assert(copy.TopFiles[0].Bytes == 123 && copy.Issues[0].IsSkippedLink && copy.Issues[0].Message == "Permissão",
            "bounded detail survives IPC");
        Assert(copy.DependencyPaths.Count == 2 && copy.DependencyPaths[1] == result.DependencyPaths[1] && !omitted,
            "all dependency paths survive IPC");
        long sequence;
        ScanProgress progress = ScanProtocol.ReadProgress(ScanProtocol.Progress(8, ProcessScanSession.ProgressFromResult(result)), out sequence);
        Assert(sequence == 8 && progress.TotalBytes == result.TotalBytes && progress.CancelRequested, "progress/sequence round trip");

        BadFrame(0, "bad magic rejected");
        BadFrame(4, "bad protocol version rejected");
        byte[] huge = FrameBytes(id, ScanFrameKind.Cancel, new byte[0]);
        WriteInt(huge, 28, ScanProtocol.MaxFrameBytes + 1);
        MustThrow(delegate { ScanProtocol.ReadFrame(new MemoryStream(huge)); }, "oversized frame rejected before body allocation");
        byte[] negative = FrameBytes(id, ScanFrameKind.Cancel, new byte[0]);
        WriteInt(negative, 28, -1);
        MustThrow(delegate { ScanProtocol.ReadFrame(new MemoryStream(negative)); }, "negative frame length rejected");
        byte[] badKind = FrameBytes(id, ScanFrameKind.Cancel, new byte[0]);
        WriteInt(badKind, 8, 99);
        MustThrow(delegate { ScanProtocol.ReadFrame(new MemoryStream(badKind)); }, "unknown frame kind rejected");
        MustThrow(delegate { ScanProtocol.ReadFrame(new MemoryStream(new byte[] { 1, 2, 3 })); }, "truncated header rejected");
        byte[] truncated = FrameBytes(id, ScanFrameKind.Cancel, new byte[] { 1, 2, 3 });
        Array.Resize(ref truncated, truncated.Length - 1);
        MustThrow(delegate { ScanProtocol.ReadFrame(new MemoryStream(truncated)); }, "truncated payload rejected");
        byte[] trailing = new byte[payload.Length + 1];
        Array.Copy(payload, trailing, payload.Length);
        MustThrow(delegate { ScanProtocol.ReadRequest(trailing); }, "trailing payload rejected");
        byte[] badString = (byte[])payload.Clone();
        WriteInt(badString, 0, ScanProtocol.MaxStringBytes + 1);
        MustThrow(delegate { ScanProtocol.ReadRequest(badString); }, "oversized string length rejected");
        byte[] badBoolean = (byte[])payload.Clone();
        badBoolean[4 + Encoding.UTF8.GetByteCount(root)] = 2;
        MustThrow(delegate { ScanProtocol.ReadRequest(badBoolean); }, "noncanonical boolean rejected");
        byte[] badRate = (byte[])payload.Clone();
        WriteInt(badRate, badRate.Length - 4, 0);
        MustThrow(delegate { ScanProtocol.ReadRequest(badRate); }, "zero metadata entry budget rejected by worker protocol");
        byte[] badCount = ScanProtocol.Result(SampleResult(root));
        int dependenciesOffset = 4 + Encoding.UTF8.GetByteCount(root) + 8 * 8 + 1 + 4;
        WriteInt(badCount, dependenciesOffset, 65);
        MustThrow(delegate { bool wasOmitted; ScanProtocol.ReadResult(badCount, out wasOmitted); }, "dependency count bounded before reading");
        ScanProgress negativeProgress = ProcessScanSession.ProgressFromResult(result);
        negativeProgress.TotalBytes = -1;
        byte[] badCounter = ScanProtocol.Progress(1, negativeProgress);
        MustThrow(delegate { long ignored; ScanProtocol.ReadProgress(badCounter, out ignored); }, "negative counters rejected");
        MustThrow(delegate { long ignored; ScanProtocol.ReadProgress(ScanProtocol.Progress(0, progress), out ignored); }, "zero sequence rejected");

        ScanResult longDetails = SampleResult(root);
        string longPath = root + "/" + new string('ç', 5000);
        for (int i = 0; i < 300; i++)
        {
            longDetails.Children.Add(new FolderSummary { Name = "long", Path = longPath, Bytes = 1 });
            longDetails.Subtotals.Add(new FolderSummary { Name = "long", Path = longPath, Bytes = 1 });
        }
        longDetails.TotalBytes = 300;
        byte[] bounded = ScanProtocol.Result(longDetails);
        ScanResult boundedCopy = ScanProtocol.ReadResult(bounded, out omitted);
        Assert(omitted && boundedCopy.Children.Count < 300 && boundedCopy.TotalBytes == 300,
            "detail byte budget preserves total");
        Assert(boundedCopy.DependencyPaths.Count == longDetails.DependencyPaths.Count && bounded.Length < ScanProtocol.MaxFrameBytes,
            "detail budget retains every dependency and bounds aggregate frame");
    }

    private static void TestCpuLimitConfiguration()
    {
        cases++;
        Assert(Marshal.SizeOf(typeof(WorkerJob.CpuRateLimit)) == 8, "CPU job ABI is two DWORDs on both architectures");
        Assert(Marshal.OffsetOf(typeof(WorkerJob.CpuRateLimit), "ControlFlags").ToInt32() == 0 &&
            Marshal.OffsetOf(typeof(WorkerJob.CpuRateLimit), "CpuRate").ToInt32() == 4, "CPU union starts at byte4");
        WorkerJob.CpuRateLimit normal = WorkerJob.CreateCpuLimit(15);
        WorkerJob.CpuRateLimit economical = WorkerJob.CreateCpuLimit(10);
        Assert(normal.ControlFlags == 5 && economical.ControlFlags == 5, "CPU policy enables a hard cap, never weight/minmax flags");
        Assert(normal.CpuRate == 1500 && economical.CpuRate == 1000, "CPU percentages converted to hundredths correctly");
        Assert(WorkerJob.CreateCpuLimit(0).CpuRate == 100 && WorkerJob.CreateCpuLimit(Int32.MaxValue).CpuRate == 10000,
            "CPU rate never zero or beyond documented100percent");
    }

    private static void TestMemoryLimitConfiguration()
    {
        cases++;
        int basicBytes = IntPtr.Size == 8 ? 64 : 48;
        int extendedBytes = IntPtr.Size == 8 ? 144 : 112;
        int memoryOffset = IntPtr.Size == 8 ? 112 : 96;
        Assert(Marshal.SizeOf(typeof(WorkerJob.BasicLimit)) == basicBytes,
            "basic job ABI matches the native pointer width of this test process");
        Assert(Marshal.SizeOf(typeof(WorkerJob.IoCounters)) == 48 &&
            Marshal.SizeOf(typeof(WorkerJob.ExtendedLimit)) == extendedBytes,
            "extended job ABI contains six 64-bit IO counters and four SIZE_T fields");
        Assert(Marshal.OffsetOf(typeof(WorkerJob.BasicLimit), "LimitFlags").ToInt32() == 16 &&
            Marshal.OffsetOf(typeof(WorkerJob.ExtendedLimit), "ProcessMemoryLimit").ToInt32() == memoryOffset,
            "native memory policy fields have documented ABI offsets");
        WorkerJob.ExtendedLimit memory = WorkerJob.CreateExtendedLimit();
        Assert(memory.Basic.LimitFlags == 0x00002100,
            "native memory policy combines PROCESS_MEMORY with KILL_ON_JOB_CLOSE");
        Assert(memory.ProcessMemoryLimit.ToUInt64() == 402653184UL,
            "native per-reader committed-memory cap is exactly 384 MiB");
        Assert(memory.JobMemoryLimit == UIntPtr.Zero && memory.Basic.MinimumWorkingSetSize == UIntPtr.Zero &&
            memory.Basic.MaximumWorkingSetSize == UIntPtr.Zero,
            "native committed-memory policy does not accidentally set a job-total or working-set cap");
        Assert(WorkerJob.MemoryLimitMatches(memory, (uint)extendedBytes),
            "complete native readback confirms configured per-reader memory cap");
        Assert(!WorkerJob.MemoryLimitMatches(memory, (uint)(extendedBytes - 1)),
            "short native memory readback cannot claim confirmation");
        memory.Basic.LimitFlags = 0x00002000;
        Assert(!WorkerJob.MemoryLimitMatches(memory, (uint)extendedBytes),
            "kill-on-close alone cannot claim a native memory cap");
        memory.Basic.LimitFlags |= 0x00000100;
        memory.ProcessMemoryLimit = new UIntPtr(1);
        Assert(!WorkerJob.MemoryLimitMatches(memory, (uint)extendedBytes),
            "mismatched native memory readback cannot claim the requested cap");
    }

    private static void TestSuccessfulProcess(string root)
    {
        cases++;
        string folder = Path.Combine(root, "Pasta ç \" literal $(nao-executar)");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "Filha"));
        File.WriteAllBytes(Path.Combine(folder, "arquivo.bin"), new byte[321]);
        File.WriteAllBytes(Path.Combine(folder, "Filha", "outro.bin"), new byte[123]);
        using (ProcessScanSession session = Launch(folder + Path.DirectorySeparatorChar, "--scan-worker", 5000, 5000, 3000, 400, false))
        {
            Finish(session, 7000);
            ScanResult result = session.Result;
            Assert(result != null && !result.IsPartial && result.FatalError == null, "real scanner subprocess succeeds");
            Assert(result.TotalBytes == 444 && result.FileCount == 2 && result.FolderCount == 1, "real child computed exact fixture totals");
            Assert(result.RootPath == folder && result.DependencyPaths.Count >= 1, "normalized identity and dependencies return from child");
            Assert(result.Subtotals.Count >= 1, "child subtotals transferred for shared cache");
            Assert(session.GetProgress().TotalBytes == 444, "final progress matches final result");
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert(!session.CpuLimitApplied && session.Diagnostic.Contains("Controle nativo de CPU indisponível"),
                    "unsupported CPU job control is explicit and does not prevent scanning");
                Assert(!session.MemoryLimitApplied && session.Diagnostic.Contains("Limite nativo de memória não confirmado") &&
                    session.Diagnostic.Contains("guarda reativa"),
                    "unsupported native memory control reports the reactive fallback without claiming confirmation");
            }
            session.Cancel();
            Assert(!session.Result.Cancelled, "cancel after completion leaves result intact");
        }
        WaitForSlots(2, 5000);
    }

    private static void TestInvalidRequest(string root)
    {
        cases++;
        using (ProcessScanSession invalid = ProcessScanSession.Start(null, new ScanOptions()))
        {
            Assert(invalid.Completed && invalid.Result != null && invalid.Result.FatalError != null,
                "invalid request yields immediate explicit failure");
            Assert(ProcessScanSession.OwnedProcessCount == 0, "invalid request never reserves/spawns a worker");
        }
        using (ProcessScanSession invalidText = ProcessScanSession.Start(root + "/" + new string('x', ScanProtocol.MaxStringBytes + 1), new ScanOptions()))
            Assert(invalidText.Completed && invalidText.Result.FatalError != null && ProcessScanSession.OwnedProcessCount == 0,
                "oversized request fails before reserving a worker");
    }

    private static void TestLaunchFailure(string root)
    {
        cases++;
        using (ProcessScanSession session = ProcessScanSession.StartForTest(root, new ScanOptions(),
            Path.Combine(root, "no-such-worker"), "", 5000, 3000, 3000, 250, false))
        {
            Finish(session, 5000);
            Assert(session.Result.FatalError != null, "process launch failure is terminal");
        }
        WaitForSlots(2, 5000);
    }

    private static void TestCrash(string root)
    {
        cases++;
        using (ProcessScanSession session = Fake(root, "crash"))
        {
            Finish(session, 5000);
            Assert(!String.IsNullOrEmpty(session.Result.FatalError) && session.Result.IsPartial, "crash becomes explicit failed result");
            Assert(!session.Result.Cancelled, "unexpected crash distinguished from caller cancellation");
        }
        WaitForSlots(2, 5000);
        using (ProcessScanSession healthy = Launch(root, "--scan-worker", 5000, 5000, 3000, 400, false))
        {
            Finish(healthy, 7000);
            Assert(healthy.Result.FatalError == null, "later work can run after failed child exits");
        }
        WaitForSlots(2, 5000);
    }

    private static void TestInvalidChild(string root, string mode, string expected)
    {
        cases++;
        using (ProcessScanSession session = Fake(root, mode))
        {
            Finish(session, 5000);
            Assert(session.Result.FatalError != null && session.Result.FatalError.Contains(expected), mode + " rejected as explicit failure");
        }
        WaitForSlots(2, 5000);
    }

    private static void TestCooperativeCancel(string root)
    {
        cases++;
        using (ProcessScanSession session = Fake(root, "cooperative"))
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (session.GetProgress().FileCount == 0 && wait.ElapsedMilliseconds < 4000) Thread.Sleep(20);
            Assert(session.GetProgress().FileCount == 1 && !session.Completed, "child alive before cancellation");
            Stopwatch elapsed = Stopwatch.StartNew();
            session.Cancel();
            Assert(elapsed.ElapsedMilliseconds < 100, "Cancel is nonblocking on calling thread");
            Finish(session, 5000);
            Assert(session.Result.Cancelled && session.Result.TotalBytes == 19, "cooperative cancel returns partial progress");
        }
        WaitForSlots(2, 5000);
    }

    private static void TestTimeout(string root, string mode, int noProgress, int startup, int heartbeat, string expected)
    {
        cases++;
        using (ProcessScanSession session = Launch(root, "--test-child " + mode, noProgress, startup, heartbeat, 200, false))
        {
            Finish(session, 5000);
            Assert(session.Result.FatalError != null && session.Result.FatalError.Contains(expected), mode + " has specific timeout reason");
            Assert(!session.Result.Cancelled && session.Result.IsPartial, "timeout failure distinguished from requested cancellation");
        }
        WaitForSlots(2, 5000);
    }

    private static void TestBlockedRequest(string root)
    {
        cases++;
        string largePath = root + "/" + new string('x', 100000);
        using (ProcessScanSession session = Launch(largePath, "--test-child no-read", 5000, 600, 5000, 200, false))
        {
            Finish(session, 5000);
            Assert(session.Result.FatalError != null && session.Result.FatalError.Contains("não iniciou"), "blocked stdin request has startup deadline");
        }
        WaitForSlots(2, 5000);
        Assert(ProcessScanSession.OwnedProcessCount == 0, "blocked writer/reader end after owned child is killed");
    }

    private static void TestQuarantinedBudget(string root)
    {
        cases++;
        using (ProcessScanSession a = Launch(root, "--test-child quarantine", 550, 3000, 3000, 150, true))
        using (ProcessScanSession b = Launch(root, "--test-child quarantine", 550, 3000, 3000, 150, true))
        {
            Finish(a, 4000); Finish(b, 4000);
            Assert(a.Completed && b.Completed && a.TerminationPending && b.TerminationPending,
                "terminal failures do not pretend child termination completed");
            Assert(!a.HasExitedForTest && !b.HasExitedForTest && ProcessScanSession.AvailableSlots == 0,
                "both live/unconfirmed children still occupy global budget");
            Stopwatch quarantine = Stopwatch.StartNew();
            while (ProcessScanSession.QuarantinedProcessCount != 2 && quarantine.ElapsedMilliseconds < 2200) Thread.Sleep(20);
            Assert(ProcessScanSession.QuarantinedProcessCount == 2 && ProcessScanSession.AvailableSlots == 0,
                "prolonged unconfirmed termination reported separately from a normal short drain");
            using (ProcessScanSession rejected = Fake(root, "crash"))
                Assert(rejected.Completed && rejected.Result.FatalError != null && ProcessScanSession.OwnedProcessCount == 2,
                    "third worker never launches while prior children remain quarantined");
        }
        WaitForSlots(2, 6000);
        Assert(ProcessScanSession.OwnedProcessCount == 0, "quarantined slots released only after real delayed exits");
    }

    private static ProcessScanSession Fake(string path, string mode)
    {
        return Launch(path, "--test-child " + mode, 5000, 5000, 3000, 250, false);
    }

    private static ProcessScanSession Launch(string path, string mode, int noProgress, int startup, int heartbeat, int grace, bool ignoreKill)
    {
        string arguments = QuoteArgument(AssemblyPath) + " " + mode;
        return ProcessScanSession.StartForTest(path, new ScanOptions(), Host, arguments, noProgress, startup, heartbeat, grace, ignoreKill);
    }

    private static int FakeChild(string mode)
    {
        // Failure fixtures run only in this test binary. They never inspect or
        // modify any process except their own, and they do not read file data.
        if (mode == "no-read") { Thread.Sleep(10000); return 0; }
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        ScanFrame requestFrame = ScanProtocol.ReadFrame(input);
        ScanRequest request = ScanProtocol.ReadRequest(requestFrame.Payload);
        if (Environment.CommandLine.Contains(request.Path)) throw new Exception("Folder leaked into command line");
        if (mode == "crash") return 23;
        if (mode == "malformed")
        {
            byte[] bad = new byte[ScanProtocol.HeaderBytes];
            output.Write(bad, 0, bad.Length); output.Flush(); Thread.Sleep(10000); return 0;
        }
        ScanResult result = SampleResult(request.Path);
        ScanProgress progress = ProcessScanSession.ProgressFromResult(result);
        if (mode == "wrong-id")
        {
            ScanProtocol.WriteFrame(output, ScanFrameKind.Progress, Guid.NewGuid(), ScanProtocol.Progress(1, progress));
            Thread.Sleep(10000); return 0;
        }
        if (mode == "wrong-root")
        {
            result.RootPath = Path.Combine(request.Path, "other");
            ScanProtocol.WriteFrame(output, ScanFrameKind.Result, requestFrame.RequestId, ScanProtocol.Result(result));
            Thread.Sleep(10000); return 0;
        }
        if (mode == "cooperative")
        {
            progress.FileCount = 1; progress.TotalBytes = 19;
            ScanProtocol.WriteFrame(output, ScanFrameKind.Progress, requestFrame.RequestId, ScanProtocol.Progress(1, progress));
            ScanFrame cancel = ScanProtocol.ReadFrame(input);
            if (cancel == null || cancel.Kind != ScanFrameKind.Cancel || cancel.RequestId != requestFrame.RequestId || cancel.Payload.Length != 0) return 24;
            result.TotalBytes = 19; result.FileCount = 1; result.Cancelled = true;
            ScanProtocol.WriteFrame(output, ScanFrameKind.Result, requestFrame.RequestId, ScanProtocol.Result(result));
            return 0;
        }
        if (mode == "heartbeat-stops")
        {
            ScanProtocol.WriteFrame(output, ScanFrameKind.Progress, requestFrame.RequestId, ScanProtocol.Progress(1, progress));
            Thread.Sleep(10000); return 0;
        }
        Stopwatch elapsed = Stopwatch.StartNew();
        long sequence = 0;
        while (mode != "quarantine" || elapsed.ElapsedMilliseconds < 3600)
        {
            ScanProtocol.WriteFrame(output, ScanFrameKind.Progress, requestFrame.RequestId, ScanProtocol.Progress(++sequence, progress));
            Thread.Sleep(100);
        }
        return 0;
    }

    private static string QuoteArgument(string value)
    {
        // Correct quoting for ProcessStartInfo.Arguments: preserve ordinary
        // Windows separators and escape runs only before a quote/end quote.
        StringBuilder result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '\"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
            else { result.Append('\\', slashes); result.Append(c); }
            slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"');
        return result.ToString();
    }

    private static ScanResult SampleResult(string root)
    {
        return new ScanResult
        {
            RootPath = root, StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow,
            Children = new List<FolderSummary>(), Subtotals = new List<FolderSummary>(),
            TopFiles = new List<FileSummary>(), Issues = new List<ScanIssue>(),
            DependencyPaths = new List<string> { root }
        };
    }

    private static void Finish(ProcessScanSession session, int milliseconds)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!session.Completed && wait.ElapsedMilliseconds < milliseconds) Thread.Sleep(20);
        Assert(session.Completed, "session reaches terminal state within test deadline: " + session.Diagnostic);
        Assert(session.Result != null, "Completed publishes a result atomically");
    }

    private static void WaitForSlots(int expected, int milliseconds)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (ProcessScanSession.AvailableSlots != expected && wait.ElapsedMilliseconds < milliseconds) Thread.Sleep(20);
        Assert(ProcessScanSession.AvailableSlots == expected, "process/pipe resource budget restored: " + ProcessScanSession.DiagnosticSummary);
    }

    private static void BadFrame(int offset, string message)
    {
        byte[] bytes = FrameBytes(Guid.NewGuid(), ScanFrameKind.Cancel, new byte[0]);
        bytes[offset] ^= 0x7f;
        MustThrow(delegate { ScanProtocol.ReadFrame(new MemoryStream(bytes)); }, message);
    }

    private static byte[] FrameBytes(Guid id, ScanFrameKind kind, byte[] payload)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            ScanProtocol.WriteFrame(stream, kind, id, payload);
            return stream.ToArray();
        }
    }

    private static void WriteInt(byte[] bytes, int offset, int value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static void MustThrow(Action action, string message)
    {
        bool threw = false;
        try { action(); }
        catch (InvalidDataException) { threw = true; }
        catch (EndOfStreamException) { threw = true; }
        catch (DecoderFallbackException) { threw = true; }
        Assert(threw, message);
    }
    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
}
