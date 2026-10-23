using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using PastaGB;

internal static class V60IdentityProtocolTests
{
    private static int assertions;
    private static void Check(bool result, string message)
    { assertions++; if (!result) throw new Exception(message); }
    private static void Rejected(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; }
        Check(rejected, message);
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "PastaGB-v60-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScanResult result = Complete(); bool omitted;
            byte[] payload = ScanProtocol.Result(result);
            ScanResult copy = ScanProtocol.ReadResult(payload, out omitted);
            Check(copy.PersistentIdentityValidated && copy.PersistentIdentity == result.PersistentIdentity &&
                copy.PersistentVolumeIdentity == result.PersistentVolumeIdentity && copy.PersistentPhysicalPath == result.PersistentPhysicalPath,
                "complete scan identity survives private IPC");
            result.ErrorCount = 1;
            copy = ScanProtocol.ReadResult(ScanProtocol.Result(result), out omitted);
            Check(!copy.PersistentIdentityValidated && copy.PersistentIdentity == null, "partial result never exports durable proof");
            result = Complete(); result.Cancelled = true;
            Check(!ScanProtocol.ReadResult(ScanProtocol.Result(result), out omitted).PersistentIdentityValidated, "cancelled result cannot seed persisted cache");
            result = Complete(); result.DependencyPaths.Add("D:\\linked");
            Check(!ScanProtocol.ReadResult(ScanProtocol.Result(result), out omitted).PersistentIdentityValidated, "extra dependency disallows simple durable snapshot");
            result = Complete(); result.PersistentIdentity = "bad\0identity";
            Check(!ScanProtocol.ReadResult(ScanProtocol.Result(result), out omitted).PersistentIdentityValidated, "invalid identity encoding omitted");
            result = Complete(); result.PersistentIdentity = new string('x', 257);
            Check(!ScanProtocol.ReadResult(ScanProtocol.Result(result), out omitted).PersistentIdentityValidated, "identity field bounded");
            result = Complete(); result.PersistentIdentityValidated = false;
            byte[] missing = ScanProtocol.Result(result); missing[missing.Length - 1] = 1;
            Rejected(delegate { ScanProtocol.ReadResult(missing, out omitted); }, "true flag without identity rejected");
            byte[] invalid = (byte[])payload.Clone();
            int errorOffset = 4 + Encoding.UTF8.GetByteCount(Complete().RootPath) + 3 * 8;
            invalid[errorOffset] = 1;
            Rejected(delegate { ScanProtocol.ReadResult(invalid, out omitted); }, "wire partial result with proof rejected");
            Array.Resize(ref payload, payload.Length - 1);
            Rejected(delegate { ScanProtocol.ReadResult(payload, out omitted); }, "truncated durable identity rejected");
            Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "bytes"), new byte[37]);
            ScanSession scan = Scanner.Start(root);
            DateTime end = DateTime.UtcNow.AddSeconds(5);
            while (!scan.Completed && DateTime.UtcNow < end) Thread.Sleep(5);
            Check(scan.Completed && scan.Result.TotalBytes == 37 && !scan.Result.IsPartial, "real portable scan remains correct");
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Check(!scan.Result.PersistentIdentityValidated && scan.Result.PersistentIdentity == null,
                    "portable path-only resolver never authorizes persisted identity");
            Console.WriteLine("PASS: " + assertions + " durable scan identity and IPC regressions; native identity not executed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static ScanResult Complete()
    {
        const string volume = @"\\?\Volume{12345678-1234-1234-1234-123456789abc}\";
        return new ScanResult { RootPath = @"C:\folder", TotalBytes = 57, StartedUtc = DateTime.UtcNow.AddSeconds(-1), EndedUtc = DateTime.UtcNow,
            Children = new List<FolderSummary>(), Subtotals = new List<FolderSummary>(), TopFiles = new List<FileSummary>(), Issues = new List<ScanIssue>(),
            DependencyPaths = new List<string> { @"C:\folder" }, PersistentIdentityValidated = true,
            PersistentIdentity = volume + "|12345678|1234567812345678|1234567812345678",
            PersistentVolumeIdentity = volume, PersistentPhysicalPath = @"C:\folder" };
    }
}
