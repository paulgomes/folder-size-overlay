using System;
using System.Threading;

namespace PastaGBIcons
{
    // Aggregate accounting of owned metadata payload plus conservative managed
    // overhead. Native provider/proxy allocations are outside this estimate;
    // they are also limited by cache/item counts and enumerator lifetime.
    internal sealed class MetadataMemoryBudget
    {
        private readonly long limit;
        private long used;
        internal MetadataMemoryBudget(long limit)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException("limit");
            this.limit = limit;
        }
        internal long Limit { get { return limit; } }
        internal long Used { get { return Interlocked.Read(ref used); } }
        internal bool TryReserve(long bytes)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            while (true)
            {
                long current = Interlocked.Read(ref used);
                if (bytes > limit - current) return false;
                if (Interlocked.CompareExchange(ref used, current + bytes, current) == current) return true;
            }
        }
        internal void Release(long bytes)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            while (true)
            {
                long current = Interlocked.Read(ref used);
                if (bytes > current) throw new InvalidOperationException("Liberação de metadados excede a reserva.");
                if (Interlocked.CompareExchange(ref used, current - bytes, current) == current) return;
            }
        }
        internal static long EstimateEntry(int nameLength, int pidlBytes)
        {
            if (nameLength < 0 || pidlBytes < 0) throw new ArgumentOutOfRangeException("nameLength");
            // Entry, resolution, strings/arrays, dictionary/list slots and slack.
            return checked(384L + nameLength * 2L + pidlBytes);
        }
    }
}
