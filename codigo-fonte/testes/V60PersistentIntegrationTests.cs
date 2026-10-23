using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PastaGB;
using PastaGBIcons;

// Compiles production App.Persistent.cs with a minimal, non-Windows host and
// the real cache worker/codec. Only stable native directory identity is faked.
internal static class V60PersistentIntegrationTests
{
    private const string Volume = @"\\?\VOLUME{130A6B64-ECA3-40BC-B0F6-1C9C02C80DC2}\";
    private static int assertions;

    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PastaGB-V60-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AsynchronousCallbackAndStaleValue(directory);
            EpochRejectsInFlightValidation(directory);
            MediaStampPredatesIdentityValidation(directory);
            PolicyRejectsOldDelivery(directory);
            BoundedEnqueueBudget(directory);
            CompletedStoreChecksPolicyAndMedia(directory);
            Console.WriteLine("PASS: " + assertions + " App.Persistent integration assertions; real worker/files, injected native identity; no Windows UI.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private static void AsynchronousCallbackAndStaleValue(string directory)
    {
        string path = @"C:\AsyncValue";
        var provider = new IntegrationIdentity { Block = true }; provider.Put(Token(path));
        using (IconContext host = Host(directory, "async", provider, path))
        {
            host.Request(path);
            Check(provider.Entered.WaitOne(2000), "identity validation runs on cache worker");
            Stopwatch timer = Stopwatch.StartNew(); SizeRecord record;
            Check(!host.Restore(path, out record), "a pending restore never blocks the coordinator");
            Check(timer.ElapsedMilliseconds < 200, "request/delivery hook returns while identity is blocked");
            provider.Release.Set();
            Until(() => host.Callbacks > 0, "validated cache result wakes the host");
            Check(host.Restore(path, out record) && record.Bytes == 1000 && record.Phase == SizePhase.Stale,
                "real hook returns last-known bytes explicitly marked stale");
            Check(host.IsMediaCurrent(record) && record.Dependencies.SequenceEqual(new[] { path }),
                "restore includes current runtime media stamp and exact folder dependency");
            Check(!host.Restore(path, out record) && provider.Calls == 1, "accepted restore is consumed once without repeated native work");
        }
    }

    private static void EpochRejectsInFlightValidation(string directory)
    {
        string path = @"C:\EpochValue";
        var provider = new IntegrationIdentity { Block = true }; provider.Put(Token(path));
        using (IconContext host = Host(directory, "epoch", provider, path))
        {
            host.Request(path);
            Check(provider.Entered.WaitOne(2000), "old epoch validation starts");
            host.ResetEpoch(); provider.Block = false; provider.Release.Set();
            Until(() => host.Statistics.Rejected > 0, "old in-flight epoch is rejected");
            Check(host.Callbacks == 0 && host.Statistics.Restored == 0, "old epoch cannot deliver or wake with a stale completion");
            SizeRecord value;
            Check(!host.Restore(path, out value), "new epoch never consumes an old completion");
            Until(() => host.Callbacks > 0, "new epoch is allowed to validate independently");
            Check(host.Restore(path, out value) && value.Phase == SizePhase.Stale, "new epoch produces its own validated stale value");
        }
    }

    private static void MediaStampPredatesIdentityValidation(string directory)
    {
        string path = @"E:\MediaValue";
        var provider = new IntegrationIdentity { Block = true }; provider.Put(Token(path));
        using (IconContext host = Host(directory, "media", provider, path))
        {
            host.Request(path);
            Check(provider.Entered.WaitOne(2000), "media fixture starts identity validation");
            // Simulate the interval after the volume monitor bumps its revision
            // but before the UI callback resets the cache epoch. A stamp taken
            // only at delivery would incorrectly accept this old operation.
            host.ChangeMediaOnly('E'); provider.Block = false; provider.Release.Set();
            Until(() => host.Callbacks > 0, "old operation reaches callback before epoch-reset hook");
            SizeRecord value;
            Check(!host.Restore(path, out value), "media changed after request cannot be stamped as newly validated");
            host.ResetEpoch(); host.Request(path);
            Until(() => host.Callbacks >= 2, "fresh media epoch can validate again");
            Check(host.Restore(path, out value) && value.VolumeStamp[4] == 1 && host.IsMediaCurrent(value),
                "new operation carries the independently captured new E revision");
        }
    }

    private static void PolicyRejectsOldDelivery(string directory)
    {
        string path = @"C:\PolicyValue";
        var provider = new IntegrationIdentity(); provider.Put(Token(path));
        using (IconContext host = Host(directory, "policy", provider, path))
        {
            host.Request(path); Until(() => host.Callbacks > 0, "policy fixture has a queued restore");
            host.ChangePolicy(false, false);
            SizeRecord value;
            Check(!host.RestoreWithPolicy(path, 0, out value), "old policy generation cannot access the restore hook");
            Check(!host.Restore(path, out value), "delivery from old link policy is dropped on the new epoch");
            Until(() => host.Statistics.Pending == 0 && !host.Statistics.Busy, "new-policy request drains");
            Check(!host.Restore(path, out value) && provider.Calls == 1,
                "cache entry written with different link policy is not revalidated or exposed");
        }
    }

    private static void BoundedEnqueueBudget(string directory)
    {
        string[] paths = Enumerable.Range(0, 2048).Select(i => @"C:\Budget\F" + i.ToString("D4")).ToArray();
        var provider = new IntegrationIdentity { Block = true }; provider.Put(Token(paths[0]));
        using (IconContext host = Host(directory, "budget", provider, paths[0]))
        {
            host.Request(paths);
            Check(provider.Entered.WaitOne(2000), "queue fixture holds its first identity read");
            Check(host.RequestedCount == 16 && host.RequestAttempts == 16, "one window attempts only sixteen of 2048 visible folders");
            host.Request(paths);
            Check(host.RequestedCount == 16 && host.RequestAttempts == 16, "repeated demand cannot bypass the 250 ms budget");
            for (int i = 0; i < 4; i++) { Thread.Sleep(270); host.Request(paths); }
            Check(host.Statistics.Pending == PersistentSizeCache.MaximumPendingJobs && host.RequestedCount <= 65,
                "repeated windows respect the cache queue bound while one validation is active");
            long rejected = host.Statistics.Rejected;
            SizeRecord value;
            for (int i = 0; i < 200; i++) host.Restore(paths[1000 + i], out value);
            Check(host.Statistics.Rejected == rejected, "per-path polling cannot normalize/enqueue more rejected work within the same window");
            provider.Block = false; provider.Release.Set();
        }
    }

    private static void CompletedStoreChecksPolicyAndMedia(string directory)
    {
        string path = @"C:\StoredValue";
        var provider = new IntegrationIdentity(); PersistentIdentityToken token = Token(path); provider.Put(token);
        using (IconContext host = Host(directory, "store", provider))
        {
            SizeRecord ready = host.NewReady(path, 4321);
            ScanResult result = new ScanResult { PersistentIdentity = token.Identity, PersistentVolumeIdentity = token.VolumeIdentity,
                PersistentPhysicalPath = token.PhysicalPath, PersistentIdentityValidated = true };
            host.Remember(ready, result, 1);
            Check(host.Statistics.Stored == 0 && host.Statistics.Pending == 0, "wrong policy generation never enters store queue");
            host.Remember(ready, result, 0);
            Until(() => host.Statistics.Stored == 1, "valid complete record is remembered through real hook");
            host.ChangeMediaOnly('C'); host.Remember(ready, result, 0);
            Check(host.Statistics.Stored == 1 && host.Statistics.Pending == 0, "old media record is rejected before storing");
        }
    }

    private static IconContext Host(string directory, string name, IntegrationIdentity provider, params string[] paths)
    {
        string file = Path.Combine(directory, name + ".bin");
        PersistentCacheFile.WriteAtomic(file, paths.Select(x => new PersistentCacheEntry(Token(x), 1000, DateTime.UtcNow, 3)));
        var host = new IconContext(new PersistentSizeCache(file, provider, 10, 5000));
        Until(() => host.Statistics.Loaded, "cache file load");
        return host;
    }
    private static PersistentIdentityToken Token(string path)
    {
        PersistentIdentityToken token;
        if (!PersistentIdentityToken.TryCreate(path, Volume + "|0000000000000012|00000000000000000000000000000001|01D9000000000000",
            Volume, path, true, out token)) throw new Exception("Invalid fixture identity");
        return token;
    }
    private static void Until(Func<bool> predicate, string reason)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.ElapsedMilliseconds > 6000) throw new Exception("Timeout: " + reason); Thread.Sleep(5); }
    }
    private static void Check(bool condition, string reason)
    { assertions++; if (!condition) throw new Exception(reason); }

    private sealed class IntegrationIdentity : IPersistentDirectoryIdentityProvider
    {
        private readonly Dictionary<string, PersistentIdentityToken> tokens = new Dictionary<string, PersistentIdentityToken>(StringComparer.OrdinalIgnoreCase);
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
        internal volatile bool Block;
        internal int Calls;
        internal void Put(PersistentIdentityToken token) { tokens[token.Path] = token; }
        public PersistentIdentityToken Resolve(string path, bool followDirectoryLinks, bool crossVolumeLinks)
        {
            Interlocked.Increment(ref Calls); Entered.Set();
            if (Block && !Release.WaitOne(5000)) throw new TimeoutException("fixture release");
            PersistentIdentityToken value; return tokens.TryGetValue(path, out value) ? value : null;
        }
    }
}

namespace PastaGBIcons
{
    internal sealed partial class IconContext : IDisposable
    {
        private const int MaximumDemand = 2048;
        private readonly object stateGate = new object();
        private readonly VolumeRevisions volumes = new VolumeRevisions();
        private AppPreferences preferences = new AppPreferences();
        private int policyGeneration;
        private volatile bool stopped;
        private int callbacks;

        internal IconContext(PersistentSizeCache cache)
        {
            persistentCache = cache; persistentVolumeStamp = volumes.Capture();
            persistentCache.ResultsAvailable += ScanDemandChanged;
        }
        private void ScanDemandChanged() { Interlocked.Increment(ref callbacks); }
        private void RecordEvent(string message) { }
        private bool MediaCurrent(SizeRecord record)
        { return record != null && volumes.MatchesDependencies(record.VolumeStamp, record.Path, record.Dependencies); }
        internal bool IsMediaCurrent(SizeRecord record) { return MediaCurrent(record); }
        internal int Callbacks { get { return Volatile.Read(ref callbacks); } }
        internal int RequestedCount { get { return persistentRequested.Count; } }
        internal int RequestAttempts { get { return persistentRequestAttempts; } }
        internal PersistentCacheStatistics Statistics { get { return persistentCache.Statistics; } }
        internal void Request(params string[] wanted) { RequestCachedSizes(wanted, preferences, policyGeneration); }
        internal bool Restore(string path, out SizeRecord record)
        { return TryRestoreCachedSize(path, preferences, policyGeneration, out record); }
        internal bool RestoreWithPolicy(string path, int policy, out SizeRecord record)
        { return TryRestoreCachedSize(path, preferences, policy, out record); }
        internal void ResetEpoch() { ResetPersistentEpoch(); }
        internal void ChangeMediaOnly(char letter) { volumes.Changed(1u << (letter - 'A')); }
        internal void ChangePolicy(bool follow, bool cross)
        {
            lock (stateGate)
            {
                AppPreferences copy = preferences.Copy(); copy.FollowDirectoryLinks = follow; copy.CrossVolumeLinks = cross;
                preferences = copy; policyGeneration++;
            }
            ResetPersistentEpoch();
        }
        internal SizeRecord NewReady(string path, long bytes)
        { return new SizeRecord { Path = path, Bytes = bytes, Phase = SizePhase.Ready, FinishedUtc = DateTime.UtcNow,
            Dependencies = new[] { path }, VolumeStamp = volumes.Capture() }; }
        internal void Remember(SizeRecord record, ScanResult result, int policy)
        { RememberCompletedSize(record, result, preferences, policy); }
        public void Dispose() { stopped = true; persistentCache.Dispose(); }
    }
}
