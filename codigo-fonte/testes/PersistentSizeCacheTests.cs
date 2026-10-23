using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PastaGBIcons;

internal static class PersistentSizeCacheTests
{
    private static int assertions;
    private static readonly DateTime Now = DateTime.UtcNow;
    private const string Volume = @"\\?\VOLUME{130A6B64-ECA3-40BC-B0F6-1C9C02C80DC2}\";
    private const string OtherVolume = @"\\?\VOLUME{412A6B64-ECA3-40BC-B0F6-1C9C02C80DC2}\";

    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PastaGB-persistent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            IdentityAndPathValidation();
            CodecValidation();
            AtomicReplacement(directory);
            RestoreAndValidation(directory);
            WrongIdentityAndPolicy(directory);
            GenerationAndDeadline(directory);
            StoreEligibilityAndCoalescing(directory);
            CorruptionRecoveryAndBounds(directory);
            Console.WriteLine("PASS: " + assertions + " persistent-cache assertions; Windows identities mocked, real atomic file I/O on " + Environment.OSVersion.Platform + ".");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private static PersistentIdentityToken Token(string path, string id = "00000000000000000000000000000001", string volume = Volume, string physical = null)
    {
        PersistentIdentityToken token;
        if (!PersistentIdentityToken.TryCreate(path, volume + "|0000000000000012|" + id + "|01D9000000000000",
            volume, physical ?? path, true, out token)) throw new Exception("Bad test token " + path);
        return token;
    }
    private static PersistentCacheEntry Entry(string path, long size = 123, DateTime? time = null, byte policy = 3)
    { return new PersistentCacheEntry(Token(path), size, time ?? Now, policy); }
    private static SizeRecord Record(string path, long size = 123)
    { return new SizeRecord { Path = path, Bytes = size, Phase = SizePhase.Ready, FinishedUtc = Now, Dependencies = new[] { path } }; }
    private static void Check(bool value, string reason)
    { assertions++; if (!value) throw new Exception("Assertion failed: " + reason); }
    private static void Reject(Action work, string reason)
    {
        bool rejected = false;
        try { work(); } catch (Exception error)
        { rejected = error is IOException || error is InvalidDataException || error is ArgumentException || error is OverflowException; }
        Check(rejected, reason);
    }
    private static void Until(Func<bool> predicate, string reason)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!predicate())
        { if (timer.ElapsedMilliseconds > 6000) throw new Exception("Timeout: " + reason); Thread.Sleep(5); }
    }
    private static void Finish(PersistentSizeCache cache)
    { Until(() => cache.Statistics.Loaded && cache.Statistics.Pending == 0 && !cache.Statistics.Busy, "worker idle"); }
    private static string FileAt(string directory, string name) { return Path.Combine(directory, name + ".bin"); }
    private static void Write(string file, params PersistentCacheEntry[] entries) { PersistentCacheFile.WriteAtomic(file, entries); }

    private static void IdentityAndPathValidation()
    {
        PersistentIdentityToken token;
        Check(Token(@"C:\Dados").SameObject(Token(@"c:\dados")), "same stable identity, case-insensitive DOS path");
        Check(!Token(@"C:\Dados").SameObject(Token(@"C:\Dados", "00000000000000000000000000000002")), "recreated directory rejected");
        Check(!Token(@"C:\Dados").SameObject(Token(@"C:\Dados", volume: OtherVolume)), "drive-letter reuse rejected");
        Check(!Token(@"C:\Dados").SameObject(Token(@"C:\Dados", physical: @"C:\Different")), "different physical alias rejected");
        Check(!PersistentIdentityToken.TryCreate(@"C:\Dados", "anything", Volume, @"C:\Dados", false, out token), "unverified scan rejected");
        Check(!PersistentIdentityToken.TryCreate(@"C:\Dados", "anything", Volume, @"C:\Dados", true, out token), "identity format checked");
        string zero = Volume + "|0000000000000012|00000000000000000000000000000000|01D9000000000000";
        Check(!PersistentIdentityToken.TryCreate(@"C:\Dados", zero, Volume, @"C:\Dados", true, out token), "zero file ID rejected");
        Check(PersistentIdentityToken.TryCreate(@"C:\", Volume + "|VOLUME_ROOT|01D9000000000000", Volume, @"C:\", true, out token), "volume root with verified GUID accepted");
        Check(!PersistentIdentityToken.TryCreate(@"C:\Folder", Volume + "|VOLUME_ROOT|01D9000000000000", Volume, @"C:\Folder", true, out token), "zero non-root identity rejected");
        string norm;
        Check(PersistentCachePaths.TryNormalize("c:/Projetos/Olá-😀/", out norm) && norm == "C:\\Projetos\\Olá-😀", "Unicode normalization");
        foreach (string invalid in new[] { "", "relative", @"C:relative", @"\\server\share", @"\\?\C:\Dados", @"C:\..\Dados", @"C:\.\Dados", @"C:\Dados:stream", @"C:\a\\b", @"C:\NUL", @"C:\con.txt", @"C:\aux\x", @"C:\a.\b", @"C:\a \b", "C:\\a\0b", "C:\\a\ud800", "C:\\a\udc00", "C:\\" + new string('a', 2048) })
            Check(!PersistentCachePaths.TryNormalize(invalid, out norm), "reject unsafe path " + invalid);
        Check(new PersistentWindowsIdentityProvider().Resolve(@"C:\Dados", true, true) == null, "Linux path resolver never acts as Windows stable identity");
    }

    private static void CodecValidation()
    {
        var original = new[] { Entry(@"C:\Dados", 9876543210), Entry("E:\\Olá-😀", 0) };
        byte[] bytes = PersistentCacheFile.Encode(original);
        List<PersistentCacheEntry> decoded = PersistentCacheFile.Decode(bytes, Now);
        Check(decoded.Count == 2 && decoded[0].Bytes == 9876543210 && decoded[1].Bytes == 0, "exact bytes including empty directory survive round trip");
        Check(decoded[0].Token.SameObject(original[0].Token) && decoded[0].FinishedUtc == Now, "identity and UTC timestamp survive");
        Check(decoded[1].Token.Path == "E:\\Olá-😀", "Unicode paths survive UTF-8");
        byte[] bad = (byte[])bytes.Clone(); bad[30] ^= 1;
        Reject(() => PersistentCacheFile.Decode(bad, Now), "payload corruption rejected");
        bad = (byte[])bytes.Clone(); bad[bad.Length - 1] ^= 1;
        Reject(() => PersistentCacheFile.Decode(bad, Now), "checksum corruption rejected");
        foreach (int length in new[] { 0, 8, 42, bytes.Length - 1 })
        {
            byte[] truncated = bytes.Take(length).ToArray();
            Reject(() => PersistentCacheFile.Decode(truncated, Now), "truncated file " + length);
        }
        Reject(() => PersistentCacheFile.Decode(new byte[PersistentSizeCache.MaximumFileBytes + 1], Now), "file byte budget");
        bad = (byte[])bytes.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(999), 0, bad, 8, 4); Rehash(bad);
        Reject(() => PersistentCacheFile.Decode(bad, Now), "unknown schema rejected even with valid checksum");
        bad = (byte[])bytes.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(1025), 0, bad, 12, 4); Rehash(bad);
        Reject(() => PersistentCacheFile.Decode(bad, Now), "untrusted entry count bounded");
        bad = (byte[])bytes.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, bad, 16, 4); Rehash(bad);
        Reject(() => PersistentCacheFile.Decode(bad, Now), "untrusted string allocation bounded");
        Reject(() => PersistentCacheFile.Encode(new[] { Entry(@"C:\Dados", -1) }), "negative size rejected");
        Reject(() => PersistentCacheFile.Encode(new[] { Entry(@"C:\Dados", policy: 4) }), "unknown policy rejected");
        Reject(() => PersistentCacheFile.Encode(new[] { Entry(@"C:\Dados"), Entry(@"c:\dados") }), "duplicate paths rejected");
        var dates = new[] { Entry(@"C:\Old", time: Now.AddDays(-31)), Entry(@"C:\Future", time: Now.AddMinutes(6)), Entry(@"C:\Valid") };
        Check(PersistentCacheFile.Decode(PersistentCacheFile.Encode(dates), Now).Count == 1, "old and future values excluded");
        var limit = Enumerable.Range(0, 1024).Select(i => Entry(@"C:\F" + i)).ToArray();
        Check(PersistentCacheFile.Decode(PersistentCacheFile.Encode(limit), Now).Count == 1024, "entry budget boundary supported");
        Reject(() => PersistentCacheFile.Encode(limit.Concat(new[] { Entry(@"C:\Extra") })), "entry budget enforced");
        var texts = Enumerable.Range(0, 120).Select(i => Entry(@"C:\F" + i + new string('x', 1400))).ToArray();
        Reject(() => PersistentCacheFile.Encode(texts), "aggregate text budget enforced");
        byte[] extra = new byte[bytes.Length + 1]; Buffer.BlockCopy(bytes, 0, extra, 0, bytes.Length - 32); Rehash(extra);
        Reject(() => PersistentCacheFile.Decode(extra, Now), "checksummed trailing payload rejected");
    }

    private static void Rehash(byte[] bytes)
    {
        byte[] hash; using (SHA256 algorithm = SHA256.Create()) hash = algorithm.ComputeHash(bytes, 0, bytes.Length - 32);
        Buffer.BlockCopy(hash, 0, bytes, bytes.Length - 32, 32);
    }

    private static void AtomicReplacement(string directory)
    {
        string file = FileAt(directory, "atomic");
        Write(file, Entry(@"C:\Dados", 111));
        byte[] old = File.ReadAllBytes(file);
        PersistentCacheFile.BeforeCommitForTests = temp => { Check(File.Exists(temp), "prepared temporary file exists"); throw new IOException("simulated interruption"); };
        try { Reject(() => Write(file, Entry(@"C:\Dados", 222)), "interruption before commit"); }
        finally { PersistentCacheFile.BeforeCommitForTests = null; }
        Check(old.SequenceEqual(File.ReadAllBytes(file)), "failed commit preserves previous bytes");
        Check(Directory.GetFiles(directory, ".tamanhos-*.tmp").Length == 0, "failed commit cleans owned temporary file");
        Write(file, Entry(@"C:\Dados", 333));
        Check(PersistentCacheFile.Load(file, Now).Single().Bytes == 333, "replacement committed and decodes");
        Check(Directory.GetFiles(directory, ".tamanhos-*.tmp").Length == 0, "successful replacement leaves no temporary file");
    }

    private static void RestoreAndValidation(string directory)
    {
        string file = FileAt(directory, "restore"); Write(file, Entry(@"C:\Dados", 321));
        var provider = new FakeIdentity(); provider.Put(Token(@"C:\Dados"));
        using (var cache = new PersistentSizeCache(file, provider, 20, 500))
        {
            Until(() => cache.Statistics.Loaded, "cache load");
            Check(provider.Calls == 0 && cache.Statistics.Entries == 1, "loading file does not inspect saved directories");
            int events = 0; cache.ResultsAvailable += () => Interlocked.Increment(ref events);
            Check(cache.RequestRestore(@"C:\Dados", true, true, 0), "restore request accepted");
            Until(() => Volatile.Read(ref events) == 1, "restore notification");
            PersistentSizeRestore[] restored = cache.DrainRestored(0);
            Check(restored.Length == 1 && restored[0].Bytes == 321 && restored[0].Epoch == 0, "validated restore delivered");
            long[] stamp = new long[26]; stamp[2] = 8;
            SizeRecord record = restored[0].ToStaleRecord(stamp); stamp[2] = 9;
            Check(record.Phase == SizePhase.Stale && record.NeedsRead && !record.IsReading, "cached total is always stale and eligible for fresh reading");
            Check(record.VolumeStamp[2] == 8 && record.Dependencies.SequenceEqual(new[] { @"C:\Dados" }), "runtime media stamp cloned and root dependency explicit");
            Check(SizePresentation.Caption(record, SizeUnits.Gigabytes, true).Contains("↻"), "cached value visibly marked updating");
            Check(!cache.RequestRestore(@"c:\dados", true, true, 0) && provider.Calls == 1, "unchanged request does not repeat native validation");
            Check(cache.DrainRestored(0).Length == 0, "restore is consumed once");
            Check(cache.RequestRestore(@"C:\Missing", true, true, 0), "miss may be queued"); Finish(cache);
            Check(provider.Calls == 1, "cache miss does not open directory metadata");
        }
    }

    private static void WrongIdentityAndPolicy(string directory)
    {
        string file = FileAt(directory, "mismatch"); Write(file, Entry(@"C:\Dados"));
        foreach (PersistentIdentityToken changed in new[] { Token(@"C:\Dados", "00000000000000000000000000000002"), Token(@"C:\Dados", volume: OtherVolume), Token(@"C:\Dados", physical: @"C:\Elsewhere"), null })
        {
            var provider = new FakeIdentity(); if (changed != null) provider.Put(changed);
            using (var cache = new PersistentSizeCache(file, provider, 20, 500))
            {
                cache.RequestRestore(@"C:\Dados", true, true, 0);
                Until(() => cache.Statistics.Rejected == 1, "mismatched identity rejected");
                Check(cache.DrainRestored(0).Length == 0, "different root/volume/physical target or unavailable identity never supplies bytes");
            }
        }
        var good = new FakeIdentity(); good.Put(Token(@"C:\Dados"));
        using (var cache = new PersistentSizeCache(file, good, 20, 500))
        {
            cache.RequestRestore(@"C:\Dados", false, true, 0); Finish(cache);
            Check(cache.DrainRestored(0).Length == 0 && good.Calls == 0, "link policy mismatch does not validate or restore");
        }
        var denied = new FakeIdentity { Failure = new UnauthorizedAccessException("access denied") };
        using (var cache = new PersistentSizeCache(file, denied, 20, 500))
        {
            cache.RequestRestore(@"C:\Dados", true, true, 0);
            Until(() => cache.Statistics.Rejected == 1, "unavailable identity");
            Check(cache.DrainRestored(0).Length == 0 && cache.Diagnostic.Contains("Identidade não confirmada"), "denied identity fails closed with diagnostic");
        }
    }

    private static void GenerationAndDeadline(string directory)
    {
        string file = FileAt(directory, "epoch"); Write(file, Entry(@"C:\Dados"));
        var provider = new FakeIdentity { Block = true }; provider.Put(Token(@"C:\Dados"));
        using (var cache = new PersistentSizeCache(file, provider, 20, 500))
        {
            cache.RequestRestore(@"C:\Dados", true, true, 0); Check(provider.Entered.WaitOne(2000), "identity validation started");
            cache.SetEpoch(1); provider.Block = false; provider.Release.Set();
            Until(() => cache.Statistics.Rejected == 1, "old generation completion");
            Check(cache.DrainRestored(0).Length == 0 && cache.DrainRestored(1).Length == 0, "late restore from old media/policy generation discarded");
            Check(!cache.RequestRestore(@"C:\Dados", true, true, 0) && !cache.Store(Record(@"C:\Dados"), Token(@"C:\Dados"), true, true, 0), "old generation enqueue rejected");
            Check(cache.RequestRestore(@"C:\Dados", true, true, 1), "new generation permits new validation");
            Until(() => cache.Statistics.Restored == 1, "new generation result");
            Check(cache.DrainRestored(1).Single().Epoch == 1, "new generation carries exact epoch");
            cache.SetEpoch(0); Check(cache.Statistics.Epoch == 1, "generation never moves backwards");
        }
        var slow = new FakeIdentity { Delay = 80 }; slow.Put(Token(@"C:\Dados"));
        using (var cache = new PersistentSizeCache(file, slow, 20, 20))
        {
            cache.RequestRestore(@"C:\Dados", true, true, 0);
            Until(() => cache.Statistics.Rejected == 1, "late native result");
            Check(cache.DrainRestored(0).Length == 0, "validation deadline rejects late answer");
        }
        var stuck = new FakeIdentity { Block = true }; stuck.Put(Token(@"C:\Dados"));
        var stopping = new PersistentSizeCache(file, stuck, 20, 20);
        stopping.RequestRestore(@"C:\Dados", true, true, 0); Check(stuck.Entered.WaitOne(2000), "stuck identity in dedicated worker");
        int accepted = 0;
        for (int i = 0; i < 100; i++) if (stopping.RequestRestore(@"C:\Q" + i, true, true, 0)) accepted++;
        Check(accepted == PersistentSizeCache.MaximumPendingJobs && stopping.Statistics.Pending == PersistentSizeCache.MaximumPendingJobs, "bounded queue while one native call is stuck");
        Stopwatch duration = Stopwatch.StartNew(); stopping.Dispose(); duration.Stop();
        Check(duration.ElapsedMilliseconds < 200 && stopping.Statistics.Disposed, "shutdown never waits for blocked metadata I/O");
        Check(!stopping.RequestRestore(@"C:\After", true, true, 0), "disposed cache rejects work");
        stuck.Release.Set();
    }

    private static void StoreEligibilityAndCoalescing(string directory)
    {
        string file = FileAt(directory, "store");
        var provider = new FakeIdentity(); provider.Put(Token(@"C:\Dados"));
        using (var cache = new PersistentSizeCache(file, provider, 20, 500))
        {
            foreach (SizePhase phase in Enum.GetValues(typeof(SizePhase)))
            {
                if (phase == SizePhase.Ready) continue;
                SizeRecord record = Record(@"C:\Dados"); record.Phase = phase;
                Check(!cache.Store(record, Token(record.Path), true, true, 0), "non-complete phase not persisted: " + phase);
            }
            SizeRecord partial = Record(@"C:\Dados"); partial.LastResultPartial = true;
            Check(!cache.Store(partial, Token(partial.Path), true, true, 0), "partial history not persisted");
            SizeRecord linked = Record(@"C:\Dados"); linked.Dependencies = new[] { linked.Path, @"E:\Target" };
            Check(!cache.Store(linked, Token(linked.Path), true, true, 0), "cross-target dependency not persisted");
            SizeRecord empty = Record(@"C:\Dados"); empty.Dependencies = new string[0];
            Check(!cache.Store(empty, Token(empty.Path), true, true, 0), "missing dependency evidence rejected");
            Check(!cache.Store(Record(@"C:\Other"), Token(@"C:\Dados"), true, true, 0), "scan token from another path rejected");
            SizeRecord source = Record(@"C:\Dados", 444);
            Check(cache.Store(source, Token(source.Path), true, true, 0), "complete scan evidence accepted"); source.Bytes = 999;
            Until(() => cache.Statistics.Writes == 1, "batched write");
            Check(PersistentCacheFile.Load(file, Now).Single().Bytes == 444, "asynchronous store copies scalar data");
        }
        string coalesced = FileAt(directory, "coalesce"); Write(coalesced, Entry(@"C:\Blocked"));
        var blocking = new FakeIdentity { Block = true }; blocking.Put(Token(@"C:\Blocked")); blocking.Put(Token(@"C:\Dados"));
        using (var cache = new PersistentSizeCache(coalesced, blocking, 30, 1000))
        {
            cache.RequestRestore(@"C:\Blocked", true, true, 0); Check(blocking.Entered.WaitOne(2000), "coalescing barrier entered");
            cache.Store(Record(@"C:\Dados", 100), Token(@"C:\Dados"), true, true, 0);
            cache.Store(Record(@"C:\Dados", 200), Token(@"C:\Dados"), true, true, 0);
            Check(cache.Statistics.Pending == 1, "latest write coalesces by path");
            blocking.Block = false; blocking.Release.Set();
            Until(() => cache.Statistics.Writes == 1, "coalesced write");
            Check(PersistentCacheFile.Load(coalesced, Now).Single(e => e.Token.Path == @"C:\Dados").Bytes == 200, "latest queued value persisted");
            Check(cache.Statistics.Stored == 1, "coalesced store validates once");
        }
    }

    private static void CorruptionRecoveryAndBounds(string directory)
    {
        string file = FileAt(directory, "corrupt"); File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        var provider = new FakeIdentity(); provider.Put(Token(@"C:\Dados"));
        using (var cache = new PersistentSizeCache(file, provider, 20, 500))
        {
            Until(() => cache.Statistics.Loaded, "corrupt cache ignored");
            Check(cache.Statistics.Entries == 0 && cache.Diagnostic.Contains("Cache anterior ignorado"), "corruption disables old values only");
            Check(cache.Store(Record(@"C:\Dados"), Token(@"C:\Dados"), true, true, 0), "fresh scan can replace corrupt cache");
            Until(() => cache.Statistics.Writes == 1, "corruption replacement");
            Check(PersistentCacheFile.Load(file, Now).Count == 1, "recovered cache decodes");
        }
        string bounded = FileAt(directory, "bounded");
        var identities = new FakeIdentity();
        using (var cache = new PersistentSizeCache(bounded, identities, 100, 500))
        {
            int accepted = 0;
            for (int i = 0; i < 70; i++)
            {
                string path = @"C:\F" + i.ToString("D3") + new string('x', 1990);
                PersistentIdentityToken token = Token(path); identities.Put(token);
                SizeRecord record = Record(path, i); record.FinishedUtc = Now.AddTicks(i);
                Until(() => cache.Store(record, token, true, true, 0), "bounded store accepted"); accepted++;
            }
            Until(() => cache.Statistics.Stored == accepted && cache.Statistics.Writes > 0 && cache.Statistics.Pending == 0 && !cache.Statistics.Busy, "bounded entries persisted");
            PersistentCacheStatistics state = cache.Statistics;
            Check(state.Entries < 70 && state.RetainedTextCharacters <= PersistentSizeCache.MaximumTextCharacters, "retained text evicts oldest entries");
            Check(state.Entries <= PersistentSizeCache.MaximumEntries, "entry limit holds");
            Until(() => PersistentCacheFile.Load(bounded, Now).Any(e => e.Bytes == 69), "newest entry persisted");
            Check(new FileInfo(bounded).Length <= PersistentSizeCache.MaximumFileBytes, "file size remains bounded");
        }
    }

    private sealed class FakeIdentity : IPersistentDirectoryIdentityProvider
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, PersistentIdentityToken> values = new Dictionary<string, PersistentIdentityToken>(StringComparer.OrdinalIgnoreCase);
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
        internal volatile bool Block;
        internal int Calls, Delay;
        internal Exception Failure;
        internal void Put(PersistentIdentityToken token) { lock (gate) values[token.Path] = token; }
        public PersistentIdentityToken Resolve(string path, bool followDirectoryLinks, bool crossVolumeLinks)
        {
            Interlocked.Increment(ref Calls); Entered.Set();
            if (Block) Release.WaitOne();
            if (Delay > 0) Thread.Sleep(Delay);
            if (Failure != null) throw Failure;
            lock (gate) { PersistentIdentityToken value; return values.TryGetValue(path, out value) ? value : null; }
        }
    }
}
