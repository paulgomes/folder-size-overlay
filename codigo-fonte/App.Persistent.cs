using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PastaGB;

namespace PastaGBIcons
{
    internal sealed partial class IconContext
    {
        private PersistentSizeCache persistentCache;
        private long persistentEpoch, drainedPersistentEpoch = -1;
        private long persistentRequestWindow;
        private int persistentRequestAttempts;
        private long[] persistentVolumeStamp;
        // Coordinator-thread-only state. Work/results are bounded separately in
        // the disk worker; these maps retain only currently requested folders.
        private readonly HashSet<string> persistentRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PersistentSizeRestore> persistentRestored =
            new Dictionary<string, PersistentSizeRestore>(StringComparer.OrdinalIgnoreCase);

        private void InitializePersistentCache()
        {
            persistentVolumeStamp = volumes.Capture();
            try
            {
                persistentCache = new PersistentSizeCache(PersistentSizeCache.DefaultPath);
                persistentCache.ResultsAvailable += ScanDemandChanged;
            }
            catch (Exception ex) { RecordEvent("Cache opcional indisponível: " + ex.Message); }
        }

        private void ResetPersistentEpoch()
        {
            lock (stateGate)
            {
                persistentEpoch++;
                persistentVolumeStamp = volumes.Capture();
                if (persistentCache != null) persistentCache.SetEpoch(persistentEpoch);
            }
        }

        private bool PersistentContext(int currentPolicy, out long epoch, out long[] stamp)
        {
            lock (stateGate)
            {
                epoch = persistentEpoch; stamp = persistentVolumeStamp;
                return !stopped && persistentCache != null && policyGeneration == currentPolicy;
            }
        }

        private void DrainPersistent(long epoch)
        {
            if (drainedPersistentEpoch != epoch)
            {
                persistentRequested.Clear(); persistentRestored.Clear(); drainedPersistentEpoch = epoch;
            }
            foreach (PersistentSizeRestore restored in persistentCache.DrainRestored(epoch))
                if (restored.Epoch == epoch && persistentRequested.Contains(restored.Path) &&
                    persistentRestored.Count < MaximumDemand)
                    persistentRestored[restored.Path] = restored;
        }

        private void RequestCachedSizes(IEnumerable<string> wanted, AppPreferences settings, int currentPolicy)
        {
            long epoch; long[] stamp;
            if (!PersistentContext(currentPolicy, out epoch, out stamp)) return;
            DrainPersistent(epoch);
            var live = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            persistentRequested.RemoveWhere(x => !live.Contains(x));
            foreach (string path in persistentRestored.Keys.Where(x => !live.Contains(x)).ToArray()) persistentRestored.Remove(path);
            foreach (string path in live) RequestCachedPath(path, settings, epoch, stamp);
        }

        private void RequestCachedPath(string path, AppPreferences settings, long epoch, long[] stamp)
        {
            if (persistentRequested.Contains(path) || persistentRequested.Count >= MaximumDemand ||
                !volumes.MatchesDependencies(stamp, path, null)) return;
            long now = Stopwatch.GetTimestamp();
            if (now >= persistentRequestWindow)
            { persistentRequestWindow = now + Stopwatch.Frequency / 4; persistentRequestAttempts = 0; }
            // A full queue must not normalize thousands of rejected paths on
            // each coordinator pass. The ordinary scanner remains independent.
            if (persistentRequestAttempts >= 16) return;
            persistentRequestAttempts++;
            if (persistentCache.RequestRestore(path, settings.FollowDirectoryLinks, settings.CrossVolumeLinks, epoch))
                persistentRequested.Add(path);
        }

        private bool TryRestoreCachedSize(string path, AppPreferences settings, int currentPolicy, out SizeRecord record)
        {
            record = null;
            long epoch; long[] stamp;
            if (!PersistentContext(currentPolicy, out epoch, out stamp)) return false;
            DrainPersistent(epoch);
            // Retry only requests rejected by the bounded queue. Accepted
            // paths are attempted once per epoch; no synchronous disk access.
            RequestCachedPath(path, settings, epoch, stamp);
            PersistentSizeRestore restored;
            if (!persistentRestored.TryGetValue(path, out restored)) return false;
            persistentRestored.Remove(path);
            lock (stateGate)
            {
                if (stopped || epoch != persistentEpoch || currentPolicy != policyGeneration ||
                    !volumes.MatchesDependencies(stamp, path, null)) return false;
                record = restored.ToStaleRecord(stamp);
                return true;
            }
        }

        private void RememberCompletedSize(SizeRecord record, ScanResult scanResult, AppPreferences settings, int currentPolicy)
        {
            if (record == null || scanResult == null) return;
            PersistentIdentityToken token;
            if (!PersistentIdentityToken.TryCreate(record.Path, scanResult.PersistentIdentity,
                scanResult.PersistentVolumeIdentity, scanResult.PersistentPhysicalPath,
                scanResult.PersistentIdentityValidated, out token)) return;
            lock (stateGate)
            {
                if (!stopped && persistentCache != null && policyGeneration == currentPolicy && MediaCurrent(record))
                    persistentCache.Store(record, token, settings.FollowDirectoryLinks, settings.CrossVolumeLinks, persistentEpoch);
            }
        }
    }
}
