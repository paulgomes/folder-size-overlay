using System;
using System.Threading;

namespace PastaGBIcons
{
    // Pure scheduling: events reset the backoff, stable verified captures reduce
    // polling, and a finite reconciliation interval catches missing WinEvents.
    internal sealed class AdaptiveRefreshPolicy
    {
        private int stableCaptures;

        internal void Reset() { stableCaptures = 0; }

        internal int NextDelay(bool changed, bool incomplete, bool enabled,
            int activeMilliseconds, int idleMilliseconds)
        {
            int active = Math.Max(50, Math.Min(2000, activeMilliseconds));
            int idle = Math.Max(active, Math.Min(4000, idleMilliseconds));
            if (!enabled) { stableCaptures = 0; return idle; }
            if (changed || incomplete) { stableCaptures = 0; return active; }
            stableCaptures = Math.Min(12, stableCaptures + 1);
            int multiplier = 1 << Math.Min(4, stableCaptures / 2);
            return (int)Math.Min(idle, (long)active * multiplier);
        }

        internal static double DrawingLeaseSeconds(int idleMilliseconds, long captureMilliseconds, int capturePasses = 1)
        {
            // The last accepted capture stays eligible through the next bounded
            // reconciliation. Geometry/identity events still revoke it at once.
            return Math.Max(2.5, Math.Min(10,
                Math.Max(0, idleMilliseconds) / 1000d + Math.Max(0, captureMilliseconds) / 1000d *
                Math.Max(1, Math.Min(2, capturePasses)) + 1));
        }
    }

    // A signal delivered during a capture must not be consumed as if it happened
    // before that capture. Version + AutoResetEvent avoids that lost-wakeup race.
    internal sealed class RefreshSignal
    {
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private long version;
        internal long Version { get { return Interlocked.Read(ref version); } }
        internal void Signal() { Interlocked.Increment(ref version); wake.Set(); }
        internal bool Wait(int milliseconds, long observedVersion)
        {
            bool signaled = Version != observedVersion || wake.WaitOne(Math.Max(0, milliseconds));
            if (signaled)
            {
                // One short settling interval merges event storms into a
                // single capture. It never blocks the UI thread.
                Thread.Sleep(50);
                wake.WaitOne(0);
            }
            return signaled;
        }
    }
}
