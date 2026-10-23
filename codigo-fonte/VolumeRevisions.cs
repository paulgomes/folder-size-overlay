using System;
using System.Collections.Generic;
using System.Threading;

namespace PastaGBIcons
{
    // Pure state: a drive letter alone is not a stable media identity.
    public sealed class VolumeRevisions
    {
        private readonly long[] revisions = new long[26];
        public long Read(string path)
        {
            int index = DriveIndex(path);
            return index < 0 ? 0 : Interlocked.Read(ref revisions[index]);
        }
        public void Changed(uint letters)
        {
            for (int index = 0; index < 26; index++)
                if ((letters & (1u << index)) != 0) Interlocked.Increment(ref revisions[index]);
        }
        public bool Matches(string path, long revision) { return Read(path) == revision; }
        public long[] Capture()
        {
            var snapshot = new long[26];
            for (int index = 0; index < 26; index++) snapshot[index] = Interlocked.Read(ref revisions[index]);
            return snapshot;
        }
        public bool MatchesAll(long[] snapshot)
        {
            if (snapshot == null || snapshot.Length != 26) return false;
            for (int index = 0; index < 26; index++)
                if (snapshot[index] != Interlocked.Read(ref revisions[index])) return false;
            return true;
        }
        public bool MatchesDependencies(long[] snapshot, string path, IEnumerable<string> dependencies)
        {
            if (!MatchesPath(snapshot, path)) return false;
            if (dependencies != null)
                foreach (string dependency in dependencies)
                    if (!MatchesPath(snapshot, dependency)) return false;
            return true;
        }
        private bool MatchesPath(long[] snapshot, string path)
        {
            int index = DriveIndex(path);
            return index >= 0 && snapshot != null && snapshot.Length == 26 &&
                snapshot[index] == Interlocked.Read(ref revisions[index]);
        }
        public static int DriveIndex(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length < 3 || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/')) return -1;
            char letter = Char.ToUpperInvariant(path[0]);
            return letter >= 'A' && letter <= 'Z' ? letter - 'A' : -1;
        }
    }
}
