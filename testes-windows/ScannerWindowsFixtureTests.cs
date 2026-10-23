using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PastaGB;

// Run on Windows in a directory the current user owns. It creates real local
// junctions only inside its unique temporary fixture. Exit 77 means NOT RUN.
// Compile against the delivered EXE for net48; do not replace scanner sources.
public static class ScannerWindowsFixtureTests
{
    private static int assertions;
    public static int Main()
    {
        if (Path.DirectorySeparatorChar != '\\')
        {
            Console.WriteLine("NOT RUN: native directory identity/junction checks require Windows."); return 77;
        }
        string fixture = Path.Combine(Path.GetTempPath(), "PastaGB-native-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(fixture, "Target");
        string tree = Path.Combine(fixture, "Tree");
        string first = Path.Combine(tree, "AliasOne");
        string second = Path.Combine(tree, "AliasTwo");
        string cycle = Path.Combine(target, "Back");
        Directory.CreateDirectory(target); Directory.CreateDirectory(tree);
        try
        {
            string deep = Path.Combine(target, "Deep"); Directory.CreateDirectory(deep);
            using (FileStream stream = File.Create(Path.Combine(target, "a.bin"))) stream.SetLength(123);
            using (FileStream stream = File.Create(Path.Combine(deep, "b.bin"))) stream.SetLength(37);
            CreateJunction(first, target);
            ScanResult direct = Finish(Scanner.Start(target, new ScanOptions()));
            ScanResult alias = Finish(Scanner.Start(first, new ScanOptions()));
            Check(direct.FatalError == null && direct.TotalBytes == 160 && !direct.IsPartial, "native plain-directory identity");
            Check(alias.FatalError == null && alias.TotalBytes == 160 && !alias.IsPartial, "native root junction follows local target");
            Check(alias.RootPath == first, "native root junction retains requested logical path");
            Check(alias.Subtotals.Single().Path == Path.Combine(first, "Deep") && alias.Subtotals.Single().Bytes == 37, "native alias descendant path");
            Check(alias.DependencyPaths.Contains(first) && alias.DependencyPaths.Contains(target), "native final destination dependencies");

            CreateJunction(second, target);
            ScanResult duplicate = Finish(Scanner.Start(tree, new ScanOptions()));
            Check(duplicate.TotalBytes == 160 && duplicate.FileCount == 2 && duplicate.SkippedLinks == 1, "two native junctions count destination once");
            Check(duplicate.IsPartial && duplicate.Children.Count(x => x.IsPartial) == 1, "native repeated target marked partial");

            CreateJunction(cycle, tree);
            ScanResult cyclic = Finish(Scanner.Start(tree, new ScanOptions()));
            Check(cyclic.TotalBytes == 160 && cyclic.FileCount == 2 && cyclic.SkippedLinks == 2, "native junction cycle terminates and preserves sum");
            Check(!cyclic.Subtotals.Any(x => x.Path == first || x.Path == second), "cycle-dependent alias subtotal is withheld");

            ScanResult conservative = Finish(Scanner.Start(first));
            Check(conservative.FatalError != null && conservative.IsPartial, "legacy API explicitly refuses native root junction");

            Directory.Delete(cycle); Directory.Delete(second); Directory.Delete(first);
            CreateJunction(first, deep);
            ScanResult retargeted = Finish(Scanner.Start(first, new ScanOptions()));
            Check(retargeted.FatalError == null && retargeted.TotalBytes == 37, "new scan resolves a retargeted alias afresh");
            Check(retargeted.DependencyPaths.Contains(deep), "retargeted alias carries its new dependency");

            Console.WriteLine("PASS: " + assertions + " native Windows directory identity/junction assertions.");
            Console.WriteLine("NOT COVERED BY THIS FIXTURE: cross-volume media removal, ACL denial, Explorer rendering, live link races, symlink privilege modes.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            // Remove known junction entries individually before recursive cleanup.
            foreach (string link in new string[] { cycle, second, first })
            {
                try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            }
            try { Directory.Delete(fixture, true); } catch (Exception ex) { Console.Error.WriteLine("Fixture cleanup: " + ex.Message); }
        }
    }

    private static ScanResult Finish(ScanSession session)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(30);
        while (!session.Completed)
        {
            if (DateTime.UtcNow > until) { session.Cancel(); throw new Exception("Native metadata read did not complete in 30 seconds."); }
            Thread.Sleep(5);
        }
        return session.Result;
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        string substitute = @"\??\" + Path.GetFullPath(target);
        byte[] targetBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] buffer = new byte[20 + targetBytes.Length];
        Array.Copy(BitConverter.GetBytes(0xA0000003U), 0, buffer, 0, 4);
        Put16(buffer, 4, buffer.Length - 8);
        Put16(buffer, 8, 0); Put16(buffer, 10, targetBytes.Length);
        Put16(buffer, 12, targetBytes.Length + 2); Put16(buffer, 14, 0);
        Array.Copy(targetBytes, 0, buffer, 16, targetBytes.Length);
        using (SafeFileHandle handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening temporary junction fixture failed.");
            uint returned;
            if (!DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Creating temporary junction fixture failed.");
        }
    }
    private static void Put16(byte[] data, int offset, int value) { Array.Copy(BitConverter.GetBytes((ushort)value), 0, data, offset, 2); }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
