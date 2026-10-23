using System;
using System.Collections.Generic;

namespace PastaGBIcons
{
    // A quiet period coalesces ordinary bursts. The first-change deadline is
    // retained until a read starts, so a stream of writes cannot postpone it
    // indefinitely. This is an eligibility deadline, not an I/O completion SLA.
    internal sealed class ChangeDebounce
    {
        internal const int QuietMilliseconds = 800, MaximumMilliseconds = 5000;
        internal const int MaximumTrackedPaths = 4096;
        private readonly Dictionary<string, DateTime> first =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        internal int Count { get { return first.Count; } }

        internal DateTime Mark(string path, DateTime now)
        {
            DateTime started;
            if (!first.TryGetValue(path, out started) || now < started)
            {
                if (first.Count >= MaximumTrackedPaths && !first.ContainsKey(path)) return now;
                first[path] = started = now;
            }
            DateTime quiet = AddMilliseconds(now, QuietMilliseconds);
            DateTime maximum = AddMilliseconds(started, MaximumMilliseconds);
            return quiet < maximum ? quiet : maximum;
        }

        internal void Forget(string path) { first.Remove(path); }
        internal void Clear() { first.Clear(); }

        private static DateTime AddMilliseconds(DateTime value, int milliseconds)
        {
            long ticks = (long)milliseconds * TimeSpan.TicksPerMillisecond;
            return value.Ticks > DateTime.MaxValue.Ticks - ticks ? DateTime.MaxValue : value.AddTicks(ticks);
        }
    }

    internal static class CoordinatorWaitPolicy
    {
        internal const int IdleMilliseconds = 1000, MinimumNotificationMilliseconds = 50;

        internal static int Begin(int activeReaders, int scanInterval, bool paused)
        {
            // Zero is an explicit clock adapter used by the performance harness.
            if (scanInterval <= 0) return 0;
            return activeReaders > 0 ? Math.Max(1, scanInterval) : IdleMilliseconds;
        }

        internal static int Include(int wait, SizeRecord record, DateTime now, int reconcileSeconds)
        {
            if (wait <= 0 || record == null || record.IsReading) return wait;
            if (record.NextAttemptUtc > now) return Until(wait, now, record.NextAttemptUtc);
            if (record.NeedsRead) return Math.Min(wait, 175);
            // Below one minute, monitor health cannot affect readiness. Above
            // it, the normal coordinator pass still verifies current coverage.
            DateTime due = AddSeconds(record.FinishedUtc, 60);
            if (due <= now) due = AddSeconds(record.FinishedUtc, Math.Max(60, reconcileSeconds));
            return due > now ? Until(wait, now, due) : Math.Min(wait, 175);
        }

        internal static int AfterSignal(int scheduledWait, double elapsedPassMilliseconds, double elapsedWaitMilliseconds)
        {
            // Repeated signals must not turn the event-driven loop into a busy
            // poll. An already-idle loop still wakes immediately, and an earlier
            // retry/debounce deadline always wins over this brief coalescing.
            double untilMinimum = MinimumNotificationMilliseconds - elapsedPassMilliseconds;
            double untilDeadline = scheduledWait - elapsedWaitMilliseconds;
            return (int)Math.Max(0, Math.Ceiling(Math.Min(untilMinimum, untilDeadline)));
        }

        private static int Until(int wait, DateTime now, DateTime deadline)
        { return Math.Min(wait, Math.Max(1, (int)Math.Min(Int32.MaxValue, Math.Ceiling((deadline - now).TotalMilliseconds)))); }

        private static DateTime AddSeconds(DateTime value, int seconds)
        {
            long ticks = (long)seconds * TimeSpan.TicksPerSecond;
            return value.Ticks > DateTime.MaxValue.Ticks - ticks ? DateTime.MaxValue : value.AddTicks(ticks);
        }
    }
}
