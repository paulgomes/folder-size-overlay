using System;
using PastaGBIcons;

internal static class V60CoordinatorPolicyTests
{
    private static int assertions;
    private static int Main()
    {
        try
        {
            DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ChangeDebounce debounce = new ChangeDebounce();
            Check(debounce.Mark(@"C:\A", start) == start.AddMilliseconds(800), "first change has an 800 ms quiet period");
            Check(debounce.Mark(@"c:\a", start.AddMilliseconds(600)) == start.AddMilliseconds(1400), "bursts share a case-insensitive path clock");
            Check(debounce.Mark(@"C:\A", start.AddMilliseconds(4500)) == start.AddMilliseconds(5000), "burst cannot extend beyond five seconds");
            Check(debounce.Mark(@"C:\A", start.AddMilliseconds(8000)) == start.AddMilliseconds(5000), "continuous writes keep the original eligibility deadline");
            debounce.Forget(@"c:\a");
            Check(debounce.Mark(@"C:\A", start.AddSeconds(9)) == start.AddMilliseconds(9800), "new read resets first-change deadline");
            Check(debounce.Mark(@"C:\A", start.AddSeconds(-1)) == start.AddMilliseconds(-200), "backward clock starts a fresh bounded period");
            debounce.Clear();
            for (int i = 0; i < ChangeDebounce.MaximumTrackedPaths; i++) debounce.Mark(@"C:\P" + i, start);
            Check(debounce.Count == ChangeDebounce.MaximumTrackedPaths, "debounce map has a bounded capacity");
            Check(debounce.Mark(@"C:\Overflow", start) == start && debounce.Count == ChangeDebounce.MaximumTrackedPaths,
                "overflow paths become eligible instead of expanding memory or postponing forever");
            debounce.Clear();
            Check(debounce.Count == 0, "policy/reset clears clocks");
            Check(debounce.Mark(@"C:\End", DateTime.MaxValue.AddMilliseconds(-100)) == DateTime.MaxValue,
                "deadline addition saturates safely");
            Check(CoordinatorWaitPolicy.Begin(0, 175, false) == 1000, "idle fallback is one second");
            Check(CoordinatorWaitPolicy.Begin(0, 300, true) == 1000, "paused fallback is one second");
            Check(CoordinatorWaitPolicy.Begin(2, 175, false) == 175, "balanced active reader polling stays timely");
            Check(CoordinatorWaitPolicy.Begin(1, 300, false) == 300, "eco active reader polling stays timely");
            Check(CoordinatorWaitPolicy.Begin(0, 0, false) == 0, "benchmark adapter disables intentional waits");
            Check(CoordinatorWaitPolicy.AfterSignal(1000, 5, 0) == 45, "early signals are coalesced to a bounded pass interval");
            Check(CoordinatorWaitPolicy.AfterSignal(20, 5, 0) == 20, "an earlier retry deadline wins over signal coalescing");
            Check(CoordinatorWaitPolicy.AfterSignal(20, 23, 15) == 5, "elapsed wait is subtracted from the deadline budget");
            Check(CoordinatorWaitPolicy.AfterSignal(1000, 200, 195) == 0, "a signal received after idle wakes immediately");
            Check(CoordinatorWaitPolicy.AfterSignal(10, 20, 15) == 0, "past deadlines never add a coalescing delay");
            SizeRecord record = new SizeRecord { Phase = SizePhase.Stale, NextAttemptUtc = start.AddMilliseconds(800) };
            Check(CoordinatorWaitPolicy.Include(1000, record, start, 300) == 800, "debounce deadline shortens an idle wait");
            Check(CoordinatorWaitPolicy.Include(1000, record, start.AddMilliseconds(799), 300) == 1, "subsequent wait does not overshoot the deadline");
            Check(CoordinatorWaitPolicy.Include(1000, record, start.AddMilliseconds(801), 300) == 175, "eligible blocked work retains a short fairness fallback");
            record.Phase = SizePhase.Unavailable; record.NextAttemptUtc = start.AddMilliseconds(300);
            Check(CoordinatorWaitPolicy.Include(1000, record, start, 300) == 300, "failure retry deadline is honored");
            record.Phase = SizePhase.Refreshing;
            Check(CoordinatorWaitPolicy.Include(1000, record, start, 300) == 1000, "in-flight values do not add a second readiness deadline");
            record.Phase = SizePhase.Ready; record.NextAttemptUtc = DateTime.MinValue; record.FinishedUtc = start;
            Check(CoordinatorWaitPolicy.Include(1000, record, start.AddMilliseconds(59750), 300) == 250, "first monitor-health threshold wakes on time");
            Check(CoordinatorWaitPolicy.Include(1000, record, start.AddSeconds(90), 300) == 1000, "healthy monitored age keeps bounded idle fallback");
            Check(CoordinatorWaitPolicy.Include(1000, record, start.AddMilliseconds(299900), 300) == 100, "periodic reconciliation deadline is not delayed");
            Check(CoordinatorWaitPolicy.Include(1000, record, start.AddSeconds(301), 300) == 175, "past periodic deadline retains timely retry");
            Console.WriteLine("PASS: " + assertions + " V6 debounce and scheduling deadline assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
}
