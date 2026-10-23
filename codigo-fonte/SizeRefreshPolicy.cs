using System;

namespace PastaGBIcons
{
    internal static class SizeRefreshPolicy
    {
        // Watch coverage can only affect the decision between the common
        // 60-second fallback and the monitored reconciliation deadline.
        // Querying native watcher state before/after that interval is wasted
        // work; notification invalidation and media checks remain independent.
        internal static bool NeedsCoverage(SizeRecord record, DateTime now, int reconcileSeconds)
        {
            if (record == null || record.IsReading || now < record.NextAttemptUtc || record.NeedsRead) return false;
            double elapsed = Math.Max(0, (now - record.FinishedUtc).TotalSeconds);
            return elapsed >= 60 && elapsed < Math.Max(60, reconcileSeconds);
        }

        internal static bool Due(SizeRecord record, DateTime now, bool monitored, int reconcileSeconds)
        {
            if (record == null) return true;
            if (record.IsReading || now < record.NextAttemptUtc) return false;
            if (record.NeedsRead) return true;
            double elapsed = Math.Max(0, (now - record.FinishedUtc).TotalSeconds);
            return elapsed >= (monitored ? Math.Max(60, reconcileSeconds) : 60);
        }

        internal static int RetrySeconds(int failures)
        {
            int bounded = Math.Max(0, Math.Min(5, failures - 1));
            return Math.Min(120, 5 * (1 << bounded));
        }

        internal static bool CanShare(SizeRecord record)
        { return record != null && record.Phase == SizePhase.Ready && !record.LastResultPartial; }
    }
}
