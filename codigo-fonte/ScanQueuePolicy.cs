using System;

namespace PastaGBIcons
{
    // Scheduling only: promoted work gets the next available slot. It does not
    // interrupt a synchronous filesystem call already running on another item.
    public static class ScanQueuePolicy
    {
        public const int InitialReadPromotionSeconds = 30;

        // Lower numbers run first. The caller must order priority 0 by the
        // unchanged firstWantedUtc, then use its normal deterministic tie-break.
        // A first-read request must keep that timestamp while it remains wanted.
        public static int GetPriority(bool foreground, bool hasResult,
            DateTime firstWantedUtc, DateTime now)
        {
            if (!hasResult && (now - firstWantedUtc).TotalSeconds >= InitialReadPromotionSeconds)
                return 0;
            if (foreground) return hasResult ? 2 : 1;
            return hasResult ? 4 : 3;
        }

        // After an explicit preemption, reserve one released slot for the
        // foreground. The caller clears the reservation once that work starts
        // or no foreground work remains pending. Use the four-argument overload
        // to decide whether a first read was promoted and deserves protection;
        // this temporary ordering override must not erase its aging state.
        public static int GetPriority(bool foreground, bool hasResult,
            DateTime firstWantedUtc, DateTime now, bool reserveForeground)
        {
            return reserveForeground && foreground ? -1 :
                GetPriority(foreground, hasResult, firstWantedUtc, now);
        }
    }
}
