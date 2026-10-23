using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PastaGBIcons;

// Real FileSystemWatcher callbacks on the executing OS. The Linux run records
// event/lock/coalescing semantics only; it does not prove native Windows behavior.
internal static class V60CoordinatorWatchTests
{
    private static int assertions;
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "PastaGB-V60-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int cycles = 0;
            using (FolderChangeTracker tracker = new FolderChangeTracker(delegate { Interlocked.Increment(ref cycles); }, 10))
            {
                tracker.Track(new[] { root });
                Until(() => tracker.IsCovered(root), "watch becomes covered");
                tracker.Drain();
                FolderChangeBatch empty = tracker.Drain();
                Check(Object.ReferenceEquals(empty, tracker.Drain()), "stable empty drains reuse a snapshot");

                int notifications = 0, outsideLock = 0, finished = 0;
                Action handler = delegate
                {
                    Interlocked.Increment(ref notifications);
                    Thread reader = new Thread(delegate()
                    {
                        // A different thread must acquire the monitor's gate.
                        // A same-thread IsCovered call would be reentrant and
                        // could not establish that the callback is outside it.
                        if (tracker.IsCovered(root)) Interlocked.Exchange(ref outsideLock, 1);
                    }) { IsBackground = true };
                    reader.Start(); reader.Join(1000);
                    Interlocked.Increment(ref finished);
                };
                tracker.WorkAvailable += handler;
                string file = Path.Combine(root, "payload.bin");
                File.WriteAllText(file, "one");
                Until(() => Volatile.Read(ref finished) > 0, "real content event wakes subscriber");
                Check(Volatile.Read(ref outsideLock) == 1, "subscriber runs outside tracker locks");
                for (int i = 0; i < 6; i++) File.AppendAllText(file, "more");
                Thread.Sleep(180);
                Check(Volatile.Read(ref notifications) == 1, "pending content bursts coalesce into one wake");
                FolderChangeBatch changed = tracker.Drain();
                Check(changed.Paths.Contains(file) && changed.Revision > empty.Revision, "woken consumer receives path and a new revision");
                Check(empty.Paths.Length == 0 && !empty.ReconcileAll, "reused empty snapshot is never mutated by later events");
                FolderChangeBatch nextEmpty = tracker.Drain();
                Check(!Object.ReferenceEquals(empty, nextEmpty) && Object.ReferenceEquals(nextEmpty, tracker.Drain()),
                    "empty snapshot is replaced once after revision changes");
                File.AppendAllText(file, "again");
                Until(() => Volatile.Read(ref finished) >= 2, "draining re-arms the next wake");
                Check(tracker.Drain().Paths.Contains(file), "re-armed event reaches consumer");
                tracker.WorkAvailable -= handler;

                int throwingNotifications = 0;
                tracker.WorkAvailable += delegate { Interlocked.Increment(ref throwingNotifications); throw new InvalidOperationException("fixture subscriber"); };
                Thread.Sleep(120); tracker.Drain();
                File.AppendAllText(file, "throws-one");
                Until(() => Volatile.Read(ref throwingNotifications) >= 1, "throwing subscriber is called");
                Thread.Sleep(120); tracker.Drain();
                File.AppendAllText(file, "throws-two");
                Until(() => Volatile.Read(ref throwingNotifications) >= 2, "subscriber exception does not stop future notifications");
                Check(tracker.IsCovered(root), "subscriber failure does not break watcher coverage");
                Thread.Sleep(120); tracker.Drain();

                int before = Volatile.Read(ref cycles);
                Stopwatch idle = Stopwatch.StartNew();
                bool coveredThroughout = true;
                while (idle.ElapsedMilliseconds < 5600)
                { if (!tracker.IsCovered(root)) coveredThroughout = false; Thread.Sleep(100); }
                int maintenanceCycles = Volatile.Read(ref cycles) - before;
                Check(coveredThroughout, "five-second maintenance preserves the eight-second health lease");
                Check(maintenanceCycles >= 1 && maintenanceCycles <= 2, "idle watcher maintenance no longer polls every second");
            }
            Console.WriteLine("PASS: " + assertions + " V6 watcher wake/coalescing/idle assertions; real filesystem events on " + Environment.OSVersion.Platform + ".");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    private static void Until(Func<bool> predicate, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(6);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new Exception(message); Thread.Sleep(10); }
    }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
}
