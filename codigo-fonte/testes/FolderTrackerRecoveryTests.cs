using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using PastaGBIcons;

internal static class FolderTrackerRecoveryTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static bool Wait(Func<bool> condition)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 4000) { if (condition()) return true; Thread.Sleep(10); }
        return condition();
    }
    public static int Main()
    {
        string path = Path.Combine(Path.GetTempPath(), "pasta-gb-watch-recover-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            int attempts = 0, firstThread = 0, differentThread = 0;
            using (var tracker = new FolderChangeTracker(delegate {
                int thread = Thread.CurrentThread.ManagedThreadId;
                int first = Interlocked.CompareExchange(ref firstThread, thread, 0);
                if (first != 0 && first != thread) Interlocked.Exchange(ref differentThread, 1);
                if (Interlocked.Increment(ref attempts) == 1) throw new IOException("falha inesperada injetada");
            }, 10))
            {
                tracker.Track(new[] { path });
                Check(Wait(delegate { return tracker.IsCovered(path); }), "Watcher and parent guard recover after an unexpected component failure");
                Check(tracker.Recoveries == 1 && differentThread == 0, "Recovery reuses one thread instead of accumulating replacements");
                Check(tracker.Drain().ReconcileAll, "The unobserved interval forces reconciliation");
                string file = Path.Combine(path, "alterado.txt"); File.WriteAllText(file, "after recovery");
                Check(Wait(delegate { var batch = tracker.Drain(); foreach (string item in batch.Paths) if (item == file) return true; return false; }),
                    "Real filesystem events resume after recovery");
                Check(tracker.RecursiveWatcherCount == 1 && tracker.GuardWatcherCount == 1, "Recovery does not duplicate native watchers");
            }
            Console.WriteLine("PASS: " + checks + " checks for real filesystem watcher recovery on Linux; no Windows UI executed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL after " + checks + " checks: " + ex); return 1; }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
