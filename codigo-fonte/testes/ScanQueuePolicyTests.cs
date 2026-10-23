using System;
using System.Collections.Generic;
using System.Linq;
using PastaGBIcons;

// No Windows, timers or I/O are needed: the clock and scan completions are
// controlled so a slow-folder starvation regression can be reproduced quickly.
internal static class ScanQueuePolicyTests
{
    private static int checks;
    private static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Request
    {
        internal string Path;
        internal bool Foreground, HasResult;
        internal DateTime FirstWantedUtc, FinishedUtc;
    }

    private sealed class Work
    {
        internal Request Request;
        internal int EndsAt;
        internal bool ProtectedFirstRead, CancelRequested;
    }

    private static void Assert(bool condition, string scenario)
    {
        if (!condition) throw new Exception(scenario);
        checks++;
    }

    private static Request Item(string path, bool foreground, bool hasResult, int arrived)
    {
        return new Request { Path = path, Foreground = foreground, HasResult = hasResult,
            FirstWantedUtc = Epoch.AddSeconds(arrived), FinishedUtc = DateTime.MinValue };
    }

    private static List<Request> Order(IEnumerable<Request> requests, DateTime now)
    {
        return Order(requests, now, false);
    }

    private static List<Request> Order(IEnumerable<Request> requests, DateTime now, bool reserveForeground)
    {
        // This is the production integration contract: oldest promoted first
        // reads precede recent requests and repeated reads in every window.
        return requests.Select(r => new { Request = r, Priority = ScanQueuePolicy.GetPriority(
                r.Foreground, r.HasResult, r.FirstWantedUtc, now, reserveForeground) })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Priority == 0 ? x.Request.FirstWantedUtc : DateTime.MinValue)
            .ThenBy(x => x.Request.FinishedUtc)
            .ThenBy(x => x.Request.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Request).ToList();
    }

    private static void TestInitialPriorities()
    {
        var fgNew = Item("fg-new", true, false, 0);
        var fgOld = Item("fg-refresh", true, true, 0);
        var bgNew = Item("bg-new", false, false, 0);
        var bgOld = Item("bg-refresh", false, true, 0);
        List<Request> queue = Order(new[] { bgOld, bgNew, fgOld, fgNew }, Epoch);
        Assert(queue[0] == fgNew, "Initial foreground first read wins");
        Assert(queue[1] == fgOld, "A foreground refresh precedes a recent background request");
        Assert(queue[2] == bgNew && queue[3] == bgOld, "Background first read precedes background refresh");
        Assert(Order(new[] { bgNew, fgOld }, Epoch.AddSeconds(29.999))[0] == fgOld,
            "A new background request is not promoted just before the wait boundary");
        Assert(Order(new[] { bgNew, fgOld }, Epoch.AddSeconds(30))[0] == bgNew,
            "At the wait boundary an unread background folder precedes a foreground refresh");
        Assert(ScanQueuePolicy.GetPriority(false, true, Epoch, Epoch.AddDays(1)) != 0,
            "An old cached result never takes a promoted first-read slot");
        Assert(ScanQueuePolicy.GetPriority(true, true, Epoch, Epoch.AddDays(1)) != 0,
            "The promotion also excludes cached foreground results");
        Assert(ScanQueuePolicy.GetPriority(false, false, Epoch.AddSeconds(1), Epoch) ==
            ScanQueuePolicy.GetPriority(false, false, Epoch, Epoch),
            "A future first-seen timestamp does not immediately promote after a clock correction");
    }

    private static void TestOldestFirst()
    {
        var older = Item("Z-old-background", false, false, 0);
        var newer = Item("A-new-foreground", true, false, 5);
        var fresh = Item("0-fresh-foreground", true, false, 39);
        List<Request> queue = Order(new[] { fresh, newer, older }, Epoch.AddSeconds(40));
        Assert(queue[0] == older, "An older promoted background read beats a newer foreground read and lexical order");
        Assert(queue[1] == newer && queue[2] == fresh,
            "An aged foreground first read precedes a fresh foreground first read");
        Assert(Order(new[] { newer, older }, Epoch.AddSeconds(100))[0] == older,
            "Repeated scheduling does not reset first-read wait age");

        older.HasResult = true;
        Assert(Order(new[] { older, fresh }, Epoch.AddSeconds(40))[0] == fresh,
            "Completing a first read immediately relinquishes its aging priority");

        var reentered = Item("Z-reentered", false, false, 39);
        var refresh = Item("fg-refresh", true, true, 0);
        Assert(Order(new[] { reentered, refresh }, Epoch.AddSeconds(40))[0] == refresh,
            "A request removed from demand and later reentered begins a new wait interval");

        var sameAgeB = Item("B", false, false, 0);
        var sameAgeA = Item("A", true, false, 0);
        Assert(Order(new[] { sameAgeB, sameAgeA }, Epoch.AddSeconds(50))[0] == sameAgeA,
            "Equal wait times have a deterministic final path ordering");
    }

    private static void TestSlowForegroundDoesNotStarveDesktop()
    {
        var requests = new List<Request>();
        for (int index = 1; index <= 5; index++)
            requests.Add(Item("F" + index, true, false, 0));
        Request desktop = Item("ZDesktop", false, false, 0);
        requests.Add(desktop);
        var active = new List<Work>();
        var firstStart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int maxActive = 0, starts = 0;

        for (int second = 0; second <= 2000; second++)
        {
            DateTime now = Epoch.AddSeconds(second);
            foreach (Work work in active.ToList())
            {
                if (work.EndsAt > second) continue;
                active.Remove(work);
                work.Request.HasResult = true;
                work.Request.FinishedUtc = now;
            }
            foreach (Request request in Order(requests, now))
            {
                if (active.Count >= 2) break;
                if (active.Any(w => w.Request == request)) continue;
                if (request.HasResult && (now - request.FinishedUtc).TotalSeconds < 60) continue;
                active.Add(new Work { Request = request, EndsAt = second + 200 });
                if (!firstStart.ContainsKey(request.Path)) firstStart.Add(request.Path, second);
                starts++;
            }
            maxActive = Math.Max(maxActive, active.Count);
        }

        Assert(firstStart["F1"] == 0 && firstStart["F2"] == 0,
            "Slow-folder scenario still gives the first slots to the foreground");
        Assert(firstStart.ContainsKey(desktop.Path),
            "The desktop gets its first read despite a permanently busy foreground refresh queue");
        Assert(firstStart[desktop.Path] == 400,
            "The desktop uses a freed slot after older first reads, without cancelling active I/O");
        Assert(firstStart.Count == 6, "Every continuously wanted folder starts at least once");
        Assert(maxActive == 2, "Aging never adds a third concurrent scan");
        Assert(starts > requests.Count, "The simulation includes repeat refreshes, not only one initial pass");
        Console.WriteLine("Slow-folder regression: desktop first starts at " + firstStart[desktop.Path] +
            " s; all " + firstStart.Count + " folders start; at most " + maxActive + " scans run.");
    }

    private static void TestReservationPreservesAgingState()
    {
        DateTime now = Epoch.AddSeconds(40);
        int plain = ScanQueuePolicy.GetPriority(true, false, Epoch, now);
        int reserved = ScanQueuePolicy.GetPriority(true, false, Epoch, now, true);
        Assert(plain == 0 && reserved == -1,
            "A reserved promoted foreground read keeps aging protection through the base overload");
        Assert(ScanQueuePolicy.GetPriority(false, false, Epoch, now, true) == 0,
            "The foreground reservation does not modify an aged background priority");
        Assert(ScanQueuePolicy.GetPriority(true, true, Epoch, now, false) ==
            ScanQueuePolicy.GetPriority(true, true, Epoch, now),
            "The five-argument overload remains compatible when no slot is reserved");

        var agedBackground = Item("A-aged-background", false, false, 0);
        var recentForeground = Item("Z-recent-foreground", true, false, 39);
        Assert(Order(new[] { recentForeground, agedBackground }, now)[0] == agedBackground,
            "An aged first read normally precedes a newly arrived foreground request");
        Assert(Order(new[] { recentForeground, agedBackground }, now, true)[0] == recentForeground,
            "A foreground request receives a slot explicitly released for it");
        Assert(Order(new[] { recentForeground, agedBackground }, now, false)[0] == agedBackground,
            "After consuming a reservation the normal aging policy resumes");
    }

    private static void TestPreemptionAndReservation()
    {
        var requests = new List<Request> { Item("A-desktop", false, false, 0),
            Item("B-desktop", false, false, 0), Item("C-desktop", false, false, 0) };
        Request foreground = Item("Z-foreground", true, false, 40);
        var active = new List<Work>();
        var starts = new List<KeyValuePair<int, Work>>();
        bool reserveForeground = false;
        int preemptions = 0, protectedDeferrals = 0, maxActive = 0;
        for (int second = 0; second <= 450; second++)
        {
            DateTime now = Epoch.AddSeconds(second);
            if (second == 40) requests.Add(foreground);
            foreach (Work work in active.ToList())
            {
                if (work.EndsAt > second) continue;
                active.Remove(work);
                if (work.CancelRequested) continue;
                work.Request.HasResult = true;
                work.Request.FinishedUtc = now;
            }

            bool foregroundWaiting = requests.Any(r => r.Foreground &&
                (!r.HasResult || (now - r.FinishedUtc).TotalSeconds >= 60) &&
                !active.Any(w => w.Request == r));
            if (!foregroundWaiting) reserveForeground = false;
            if (foregroundWaiting && active.Count >= 2 &&
                !active.Any(w => w.Request.Foreground || w.CancelRequested))
            {
                Work yielding = active.LastOrDefault(w => !w.ProtectedFirstRead);
                if (yielding != null)
                {
                    // Cancellation completes after three ticks. Until then the
                    // existing work still counts against the two-slot limit.
                    yielding.CancelRequested = true;
                    yielding.EndsAt = second + 3;
                    reserveForeground = true;
                    preemptions++;
                }
                else protectedDeferrals++;
            }

            foreach (Request request in Order(requests, now, reserveForeground))
            {
                if (active.Count >= 2) break;
                if (active.Any(w => w.Request == request)) continue;
                if (request.HasResult && (now - request.FinishedUtc).TotalSeconds < 60) continue;
                var work = new Work { Request = request, EndsAt = second + 200,
                    ProtectedFirstRead = ScanQueuePolicy.GetPriority(request.Foreground,
                        request.HasResult, request.FirstWantedUtc, now) == 0 };
                active.Add(work);
                starts.Add(new KeyValuePair<int, Work>(second, work));
                if (request.Foreground) reserveForeground = false;
            }
            maxActive = Math.Max(maxActive, active.Count);
        }

        Assert(preemptions == 1, "A newly focused folder preempts one originally unprotected desktop read");
        Assert(starts.Where(x => x.Value.Request == foreground).First().Key == 43,
            "The foreground gets the released slot after asynchronous cancellation completes");
        Assert(starts.Any(x => x.Key == 200 && x.Value.Request.Path == "B-desktop" &&
            x.Value.ProtectedFirstRead), "The interrupted desktop first read later starts with aging protection");
        Assert(protectedDeferrals > 0, "A stale foreground refresh waits while both current first reads are protected");
        Assert(starts.Where(x => x.Value.ProtectedFirstRead).All(x => !x.Value.CancelRequested),
            "The preemption rule never cancels a first read that earned aging protection");
        Assert(requests.All(r => r.HasResult), "Protected desktop reads complete and produce their first results");
        Assert(starts.Count(x => x.Value.Request == foreground) >= 2,
            "Foreground refreshes resume after the protected first reads finish");
        Assert(maxActive == 2, "A reserved foreground slot does not start a third worker before cancellation completes");
        Console.WriteLine("Preemption regression: foreground starts at 43 s after one cancellation; promoted desktop reads complete protected.");
    }

    public static int Main()
    {
        try
        {
            TestInitialPriorities();
            TestOldestFirst();
            TestSlowForegroundDoesNotStarveDesktop();
            TestReservationPreservesAgingState();
            TestPreemptionAndReservation();
            Console.WriteLine("PASS: " + checks + " checks; initial priority, aging, fairness, reentry, reserved foreground and protected preemption simulations.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }
}
