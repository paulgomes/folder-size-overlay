using System;
using System.Globalization;

namespace PastaGBIcons
{
    internal enum SizePhase { Queued, Scanning, Ready, Partial, Stale, Refreshing, Unavailable }

    // Value, freshness and screen geometry have independent lifetimes.
    internal sealed class SizeRecord
    {
        internal string Path, Detail;
        internal long Bytes;
        internal SizePhase Phase;
        internal bool LastResultPartial;
        internal DateTime FinishedUtc, NextAttemptUtc;
        internal int FailureCount;
        internal long[] VolumeStamp;
        internal string[] Dependencies = new string[0];

        internal bool HasValue
        {
            get { return Phase == SizePhase.Ready || Phase == SizePhase.Partial ||
                Phase == SizePhase.Stale || Phase == SizePhase.Refreshing; }
        }
        internal bool IsReading { get { return Phase == SizePhase.Scanning || Phase == SizePhase.Refreshing; } }
        internal bool NeedsRead { get { return Phase == SizePhase.Queued || Phase == SizePhase.Stale || Phase == SizePhase.Unavailable; } }
        internal SizeRecord Copy() { return (SizeRecord)MemberwiseClone(); }
        internal SizeRecord Invalidate(string reason)
        {
            SizeRecord copy = Copy();
            if (HasValue) copy.Phase = IsReading ? SizePhase.Refreshing : SizePhase.Stale;
            else if (Phase != SizePhase.Scanning) copy.Phase = SizePhase.Queued;
            copy.Detail = reason; copy.NextAttemptUtc = DateTime.MinValue;
            return copy;
        }
    }

    internal static class SizePresentation
    {
        private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly string[] Suffixes = { " B", " KB", " MB", " GB", " TB", " PB", " EB" };

        internal static string Bytes(long bytes, SizeUnits units)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            if (units == SizeUnits.Gigabytes)
            {
                if (bytes == 0) return "0 GB";
                if (bytes < 10000000L) return "<0,01 GB";
                return ((decimal)bytes / 1000000000m).ToString("0.##", Portuguese) + " GB";
            }
            decimal value = bytes;
            int unit = 0;
            while (unit < Suffixes.Length - 1 && value >= 1000m) { value /= 1000m; unit++; }
            if (unit < Suffixes.Length - 1 && Decimal.Round(value, 2, MidpointRounding.AwayFromZero) >= 1000m)
            { value /= 1000m; unit++; }
            return value.ToString("0.##", Portuguese) + Suffixes[unit];
        }

        internal static string Caption(SizeRecord record, SizeUnits units, bool shortCaption)
        {
            if (record == null || record.Phase == SizePhase.Queued) return "aguardando…";
            if (record.Phase == SizePhase.Scanning)
                return shortCaption ? "calculando…" : "Calculando…";
            if (record.Phase == SizePhase.Unavailable) return "indisponível";
            string value = Bytes(record.Bytes, units);
            bool partial = record.Phase == SizePhase.Partial || record.LastResultPartial;
            bool updating = record.Phase == SizePhase.Stale || record.Phase == SizePhase.Refreshing;
            if (shortCaption) return value + (partial ? "*" : "") + (updating ? " ↻" : "");
            if (updating) return value + (partial ? "*" : "") + " · atualizando";
            return value + (partial ? " · parcial" : "");
        }

        internal static string PhaseName(SizePhase phase)
        {
            switch (phase)
            {
                case SizePhase.Queued: return "aguardando leitura";
                case SizePhase.Scanning: return "calculando";
                case SizePhase.Ready: return "concluído";
                case SizePhase.Partial: return "parcial";
                case SizePhase.Stale: return "aguardando atualização";
                case SizePhase.Refreshing: return "atualizando";
                default: return "indisponível";
            }
        }
    }

    // Kept inside one label, so retained text is bounded by the label budget.
    // Cache only display primitives; media validity is still checked each frame
    // and no directory/result/dependency object is retained by this cache.
    internal struct SizeCaptionCache
    {
        private bool initialized, present, partial, compact;
        private SizePhase phase;
        private SizeUnits units;
        private long bytes;
        private string caption;

        internal string Get(SizeRecord record, SizeUnits nextUnits, bool shortCaption)
        {
            bool nextPresent = record != null;
            SizePhase nextPhase = nextPresent ? record.Phase : SizePhase.Queued;
            long nextBytes = nextPresent ? record.Bytes : 0;
            bool nextPartial = nextPresent && record.LastResultPartial;
            if (initialized && present == nextPresent && phase == nextPhase && bytes == nextBytes &&
                partial == nextPartial && units == nextUnits && compact == shortCaption) return caption;
            string nextCaption = SizePresentation.Caption(record, nextUnits, shortCaption);
            initialized = true; present = nextPresent; phase = nextPhase; bytes = nextBytes;
            partial = nextPartial; units = nextUnits; compact = shortCaption; caption = nextCaption;
            return caption;
        }
    }
}
