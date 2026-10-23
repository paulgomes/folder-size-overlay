using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PastaGBIcons
{
    internal sealed class PersistentSizeRestore
    {
        internal readonly string Path;
        internal readonly long Bytes, Epoch;
        internal readonly DateTime FinishedUtc;

        internal PersistentSizeRestore(PersistentCacheEntry entry, long epoch)
        { Path = entry.Token.Path; Bytes = entry.Bytes; FinishedUtc = entry.FinishedUtc; Epoch = epoch; }

        internal SizeRecord ToStaleRecord(long[] volumeStamp)
        {
            return new SizeRecord { Path = Path, Bytes = Bytes, Phase = SizePhase.Stale,
                LastResultPartial = false, FinishedUtc = FinishedUtc, NextAttemptUtc = DateTime.MinValue,
                VolumeStamp = volumeStamp == null ? null : (long[])volumeStamp.Clone(), Dependencies = new[] { Path },
                Detail = "Último tamanho conhecido; identidade confirmada, conteúdo aguardando atualização" };
        }
    }

    internal sealed class PersistentCacheEntry
    {
        internal readonly PersistentIdentityToken Token;
        internal readonly long Bytes;
        internal readonly DateTime FinishedUtc;
        internal readonly byte Policy;

        internal PersistentCacheEntry(PersistentIdentityToken token, long bytes, DateTime finishedUtc, byte policy)
        { Token = token; Bytes = bytes; FinishedUtc = finishedUtc; Policy = policy; }

        internal int TextCharacters
        { get { return Token.Path.Length + Token.Identity.Length + Token.VolumeIdentity.Length + Token.PhysicalPath.Length; } }
    }

    internal sealed class PersistentCacheStatistics
    {
        internal bool Loaded, Busy, Disposed;
        internal int Entries, Pending, RetainedTextCharacters;
        internal long Restored, Stored, Rejected, Validations, Writes, Epoch;
        internal double BusySeconds;
        internal string LastError;
    }

    internal sealed class PersistentSizeCache : IDisposable
    {
        internal const int MaximumEntries = 1024, MaximumPendingJobs = 64, MaximumRestored = 64;
        internal const int MaximumTextCharacters = 262144, MaximumAttemptCharacters = 131072;
        internal const int MaximumFileBytes = 4 * 1024 * 1024;
        internal const int ValidationTimeoutMilliseconds = 2000, JobLifetimeMilliseconds = 5000;
        internal const int WriteDelayMilliseconds = 2000;
        private static readonly PersistentSizeRestore[] NoResults = new PersistentSizeRestore[0];
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly LinkedList<Job> jobs = new LinkedList<Job>();
        private readonly Dictionary<string, LinkedListNode<Job>> queued =
            new Dictionary<string, LinkedListNode<Job>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> attempts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> attemptOrder = new Queue<string>();
        private readonly Queue<PersistentSizeRestore> restored = new Queue<PersistentSizeRestore>();
        // Entries are accessed only by the one worker. Public statistics are copied under gate.
        private readonly Dictionary<string, PersistentCacheEntry> entries =
            new Dictionary<string, PersistentCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly string filePath;
        private readonly IPersistentDirectoryIdentityProvider identity;
        private readonly int writeDelayMilliseconds, validationTimeoutMilliseconds;
        private readonly Thread worker;
        private bool stopped, loaded, busy, dirty;
        private long epoch, restoredCount, storedCount, rejectedCount, validations, writes, busySince;
        private int retainedCharacters, entryCount, entryCharacters, attemptCharacters;
        private long nextWrite;
        private string lastError;

        private sealed class Job
        {
            internal string Key, Path;
            internal byte Policy;
            internal long Epoch, QueuedAt;
            internal PersistentCacheEntry Store;
        }

        internal static string DefaultPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PastaGBIcones", "tamanhos-v1.bin"); }
        }

        internal event Action ResultsAvailable;

        internal PersistentSizeCache(string filePath)
            : this(filePath, new PersistentWindowsIdentityProvider(), WriteDelayMilliseconds, ValidationTimeoutMilliseconds) { }

        // Injectable identity and timing keep portable tests independent of Win32.
        internal PersistentSizeCache(string filePath, IPersistentDirectoryIdentityProvider identity,
            int writeDelayMilliseconds, int validationTimeoutMilliseconds)
        {
            if (String.IsNullOrEmpty(filePath)) throw new ArgumentException("Caminho de cache ausente.", "filePath");
            if (identity == null) throw new ArgumentNullException("identity");
            this.filePath = System.IO.Path.GetFullPath(filePath);
            this.identity = identity;
            this.writeDelayMilliseconds = Math.Max(1, writeDelayMilliseconds);
            this.validationTimeoutMilliseconds = Math.Max(1, validationTimeoutMilliseconds);
            worker = new Thread(Run) { IsBackground = true, Name = "PastaGB.PersistentSizeCache" };
            worker.Start();
        }

        internal void SetEpoch(long nextEpoch)
        {
            lock (gate)
            {
                if (stopped || nextEpoch <= epoch) return;
                epoch = nextEpoch;
                jobs.Clear(); queued.Clear(); restored.Clear(); attempts.Clear(); attemptOrder.Clear(); attemptCharacters = 0;
                wake.Set();
            }
        }

        internal bool RequestRestore(string path, bool followDirectoryLinks, bool crossVolumeLinks, long expectedEpoch)
        {
            string normalized;
            if (!PersistentCachePaths.TryNormalize(path, out normalized)) return false;
            byte policy = Policy(followDirectoryLinks, crossVolumeLinks);
            string key = "R" + policy.ToString(CultureInfo.InvariantCulture) + "|" + normalized;
            lock (gate)
            {
                if (stopped || epoch != expectedEpoch || attempts.Contains(key)) return false;
                if (jobs.Count >= MaximumPendingJobs) { rejectedCount++; return false; }
                while (attempts.Count >= MaximumEntries || attemptCharacters + key.Length > MaximumAttemptCharacters)
                {
                    string removed = attemptOrder.Dequeue();
                    if (attempts.Remove(removed)) attemptCharacters -= removed.Length;
                }
                attempts.Add(key); attemptOrder.Enqueue(key); attemptCharacters += key.Length;
                Enqueue(new Job { Key = key, Path = normalized, Policy = policy, Epoch = expectedEpoch, QueuedAt = Clock() });
                return true;
            }
        }

        internal bool Store(SizeRecord record, PersistentIdentityToken token,
            bool followDirectoryLinks, bool crossVolumeLinks, long expectedEpoch)
        {
            string normalized;
            if (record == null || token == null || record.Phase != SizePhase.Ready || record.LastResultPartial ||
                record.Bytes < 0 || !PersistentCachePaths.TryNormalize(record.Path, out normalized) ||
                !String.Equals(normalized, token.Path, StringComparison.OrdinalIgnoreCase) ||
                record.Dependencies == null || record.Dependencies.Length != 1 ||
                !String.Equals(normalized, record.Dependencies[0], StringComparison.OrdinalIgnoreCase) ||
                !PersistentCacheFile.AcceptTime(record.FinishedUtc, DateTime.UtcNow)) return false;
            byte policy = Policy(followDirectoryLinks, crossVolumeLinks);
            var entry = new PersistentCacheEntry(token, record.Bytes, record.FinishedUtc, policy);
            string key = "S|" + normalized;
            lock (gate)
            {
                if (stopped || epoch != expectedEpoch) return false;
                LinkedListNode<Job> previous;
                if (queued.TryGetValue(key, out previous))
                {
                    previous.Value = new Job { Key = key, Path = normalized, Policy = policy, Epoch = expectedEpoch,
                        QueuedAt = Clock(), Store = entry };
                    wake.Set(); return true;
                }
                if (jobs.Count >= MaximumPendingJobs) { rejectedCount++; return false; }
                Enqueue(new Job { Key = key, Path = normalized, Policy = policy, Epoch = expectedEpoch,
                    QueuedAt = Clock(), Store = entry });
                return true;
            }
        }

        internal PersistentSizeRestore[] DrainRestored(long expectedEpoch)
        {
            lock (gate)
            {
                if (stopped || epoch != expectedEpoch || restored.Count == 0) return NoResults;
                PersistentSizeRestore[] result = restored.ToArray(); restored.Clear(); return result;
            }
        }

        internal PersistentCacheStatistics Statistics
        {
            get
            {
                lock (gate) return new PersistentCacheStatistics { Loaded = loaded, Busy = busy, Disposed = stopped,
                    Entries = entryCount, Pending = jobs.Count, RetainedTextCharacters = entryCharacters,
                    Restored = restoredCount, Stored = storedCount, Rejected = rejectedCount,
                    Validations = validations, Writes = writes, Epoch = epoch, LastError = lastError,
                    BusySeconds = busy ? Elapsed(busySince) / 1000d : 0 };
            }
        }

        internal string Diagnostic
        {
            get
            {
                PersistentCacheStatistics value = Statistics;
                return "Cache persistente: " + (value.Disposed ? "encerrado" : value.Loaded ? "carregado" : "carregando") +
                    "; entradas=" + value.Entries + "/" + MaximumEntries + "; fila=" + value.Pending + "/" + MaximumPendingJobs +
                    "; texto=" + value.RetainedTextCharacters + "/" + MaximumTextCharacters +
                    "; recuperados=" + value.Restored + "; gravados=" + value.Stored + "; rejeitados=" + value.Rejected +
                    "; validações=" + value.Validations + "; gravações em disco=" + value.Writes +
                    (value.Busy ? "; operação em curso há " + value.BusySeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" : String.Empty) +
                    (String.IsNullOrEmpty(value.LastError) ? String.Empty : "; último aviso=" + value.LastError);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true; jobs.Clear(); queued.Clear(); restored.Clear(); attempts.Clear(); attemptOrder.Clear();
                attemptCharacters = 0; wake.Set();
            }
            // Never wait for disk/native metadata I/O on the UI shutdown path.
            // Last uncommitted values may be lost; atomic replacement protects the previous file.
        }

        private void Enqueue(Job job)
        { queued.Add(job.Key, jobs.AddLast(job)); wake.Set(); }

        private void Run()
        {
            try
            {
                try
                {
                    BeginOperation();
                    foreach (PersistentCacheEntry entry in PersistentCacheFile.Load(filePath, DateTime.UtcNow))
                        AddEntry(entry, false);
                }
                catch (Exception ex) { Error("Cache anterior ignorado", ex); }
                finally { lock (gate) { loaded = true; busy = false; } }
                while (true)
                {
                    Job job = null;
                    int wait = Timeout.Infinite;
                    bool write;
                    lock (gate)
                    {
                        if (stopped) break;
                        if (jobs.Count != 0)
                        {
                            job = jobs.First.Value; jobs.RemoveFirst(); queued.Remove(job.Key);
                        }
                        write = dirty && Clock() >= nextWrite;
                        if (dirty && !write) wait = Math.Max(1, (int)Math.Min(Int32.MaxValue, Remaining(nextWrite)));
                    }
                    if (write) Flush();
                    if (job != null) Process(job);
                    else if (!write) wake.WaitOne(wait);
                }
            }
            catch (Exception ex) { Error("Cache opcional desativado", ex); }
            finally
            {
                lock (gate)
                { stopped = true; busy = false; jobs.Clear(); queued.Clear(); restored.Clear(); }
                wake.Dispose();
            }
        }

        private void Process(Job job)
        {
            lock (gate)
                if (stopped || epoch != job.Epoch || Elapsed(job.QueuedAt) > JobLifetimeMilliseconds)
                { rejectedCount++; return; }
            PersistentCacheEntry entry = job.Store;
            if (entry == null && (!entries.TryGetValue(job.Path, out entry) || entry.Policy != job.Policy ||
                !PersistentCacheFile.AcceptTime(entry.FinishedUtc, DateTime.UtcNow))) return;
            long started = Clock();
            try
            {
                BeginOperation();
                lock (gate) validations++;
                PersistentIdentityToken current = identity.Resolve(job.Path, (job.Policy & 1) != 0, (job.Policy & 2) != 0);
                lock (gate)
                {
                    if (stopped || epoch != job.Epoch || Elapsed(started) > validationTimeoutMilliseconds ||
                        Elapsed(job.QueuedAt) > JobLifetimeMilliseconds || !entry.Token.SameObject(current))
                    { rejectedCount++; return; }
                    if (job.Store != null) AddEntry(entry, true);
                    else
                    {
                        if (restored.Count == MaximumRestored) { restored.Dequeue(); rejectedCount++; }
                        restored.Enqueue(new PersistentSizeRestore(entry, job.Epoch)); restoredCount++;
                    }
                }
                if (job.Store == null) Notify();
            }
            catch (Exception ex)
            {
                lock (gate) { Error("Identidade não confirmada", ex); rejectedCount++; }
            }
            finally { lock (gate) busy = false; }
        }

        private void AddEntry(PersistentCacheEntry entry, bool changed)
        {
            PersistentCacheEntry previous;
            if (entries.TryGetValue(entry.Token.Path, out previous))
            { entries.Remove(entry.Token.Path); retainedCharacters -= previous.TextCharacters; }
            while (entries.Count >= MaximumEntries || retainedCharacters + entry.TextCharacters > MaximumTextCharacters)
            {
                string oldest = null; DateTime time = DateTime.MaxValue;
                foreach (var pair in entries)
                    if (oldest == null || pair.Value.FinishedUtc < time)
                    { oldest = pair.Key; time = pair.Value.FinishedUtc; }
                if (oldest == null) return;
                retainedCharacters -= entries[oldest].TextCharacters; entries.Remove(oldest);
            }
            entries.Add(entry.Token.Path, entry); retainedCharacters += entry.TextCharacters;
            lock (gate)
            {
                entryCount = entries.Count; entryCharacters = retainedCharacters;
                if (changed)
                {
                    storedCount++;
                    if (!dirty) nextWrite = Deadline(writeDelayMilliseconds);
                    dirty = true;
                }
            }
        }

        private void Flush()
        {
            try
            {
                BeginOperation();
                PersistentCacheFile.WriteAtomic(filePath, entries.Values);
                lock (gate) { dirty = false; writes++; }
            }
            catch (Exception ex)
            {
                Error("Cache não gravado; cálculo normal continua", ex);
                lock (gate) nextWrite = Deadline(30000);
            }
            finally { lock (gate) busy = false; }
        }

        private void BeginOperation() { lock (gate) { busy = true; busySince = Clock(); } }
        private void Error(string prefix, Exception error)
        {
            string message = prefix + ": " + error.GetType().Name + " — " + error.Message;
            lock (gate) lastError = message.Length > 300 ? message.Substring(0, 300) : message;
        }
        private void Notify()
        {
            Action handler = ResultsAvailable;
            if (handler != null) try { handler(); } catch { /* Optional notification cannot kill the cache. */ }
        }
        private static byte Policy(bool follow, bool cross) { return (byte)((follow ? 1 : 0) | (cross ? 2 : 0)); }
        private static long Clock() { return Stopwatch.GetTimestamp(); }
        private static long Deadline(int milliseconds) { return Clock() + (long)(milliseconds * (double)Stopwatch.Frequency / 1000d); }
        private static double Elapsed(long start) { return (Clock() - start) * 1000d / Stopwatch.Frequency; }
        private static double Remaining(long until) { return (until - Clock()) * 1000d / Stopwatch.Frequency; }
    }

    internal static class PersistentCacheFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PGBCACHE");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private const int Version = 1, HashBytes = 32;
        private const int MaximumStringBytes = PersistentCachePaths.MaximumPathCharacters * 3;
        private static readonly DateTime MinimumTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static bool AcceptTime(DateTime time, DateTime now)
        { return time.Kind == DateTimeKind.Utc && time >= MinimumTime && time <= now.AddMinutes(5) && time >= now.AddDays(-30); }

        internal static byte[] Encode(IEnumerable<PersistentCacheEntry> entries)
        {
            var values = new List<PersistentCacheEntry>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int characters = 0;
            foreach (PersistentCacheEntry entry in entries)
            {
                if (entry == null || entry.Token == null || entry.Bytes < 0 || entry.Policy > 3 ||
                    entry.FinishedUtc.Kind != DateTimeKind.Utc || entry.FinishedUtc < MinimumTime ||
                    !paths.Add(entry.Token.Path) || values.Count >= PersistentSizeCache.MaximumEntries)
                    throw new InvalidDataException("Entrada de cache inválida.");
                characters += entry.TextCharacters;
                if (characters > PersistentSizeCache.MaximumTextCharacters) throw new InvalidDataException("Texto do cache excede o limite.");
                values.Add(entry);
            }
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory, Utf8, true))
                {
                    writer.Write(Magic); writer.Write(Version); writer.Write(values.Count);
                    foreach (PersistentCacheEntry entry in values)
                    {
                        WriteString(writer, entry.Token.Path); WriteString(writer, entry.Token.Identity);
                        WriteString(writer, entry.Token.VolumeIdentity); WriteString(writer, entry.Token.PhysicalPath);
                        writer.Write(entry.Policy); writer.Write(entry.Bytes); writer.Write(entry.FinishedUtc.Ticks);
                    }
                }
                if (memory.Length + HashBytes > PersistentSizeCache.MaximumFileBytes) throw new InvalidDataException("Cache excede o limite de arquivo.");
                byte[] payload = memory.ToArray();
                byte[] checksum; using (SHA256 algorithm = SHA256.Create()) checksum = algorithm.ComputeHash(payload);
                byte[] result = new byte[payload.Length + HashBytes];
                Buffer.BlockCopy(payload, 0, result, 0, payload.Length); Buffer.BlockCopy(checksum, 0, result, payload.Length, HashBytes);
                return result;
            }
        }

        internal static List<PersistentCacheEntry> Decode(byte[] bytes, DateTime now)
        {
            if (bytes == null || bytes.Length < Magic.Length + 8 + HashBytes || bytes.Length > PersistentSizeCache.MaximumFileBytes)
                throw new InvalidDataException("Comprimento de cache inválido.");
            int payload = bytes.Length - HashBytes;
            byte[] checksum; using (SHA256 algorithm = SHA256.Create()) checksum = algorithm.ComputeHash(bytes, 0, payload);
            int difference = 0; for (int i = 0; i < HashBytes; i++) difference |= checksum[i] ^ bytes[payload + i];
            if (difference != 0) throw new InvalidDataException("Integridade do cache não confirmada.");
            var result = new List<PersistentCacheEntry>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int characters = 0;
            using (var memory = new MemoryStream(bytes, 0, payload, false))
            using (var reader = new BinaryReader(memory, Utf8))
            {
                foreach (byte expected in Magic) if (reader.ReadByte() != expected) throw new InvalidDataException("Arquivo de cache desconhecido.");
                if (reader.ReadInt32() != Version) throw new InvalidDataException("Versão de cache incompatível.");
                int count = reader.ReadInt32();
                if (count < 0 || count > PersistentSizeCache.MaximumEntries) throw new InvalidDataException("Número de entradas inválido.");
                for (int i = 0; i < count; i++)
                {
                    string path = ReadString(reader), identity = ReadString(reader), volume = ReadString(reader), physical = ReadString(reader);
                    byte policy = reader.ReadByte(); long size = reader.ReadInt64(), ticks = reader.ReadInt64();
                    PersistentIdentityToken token;
                    if (policy > 3 || size < 0 || ticks < MinimumTime.Ticks || ticks > DateTime.MaxValue.Ticks ||
                        !PersistentIdentityToken.TryCreate(path, identity, volume, physical, true, out token) || !paths.Add(token.Path))
                        throw new InvalidDataException("Entrada de cache inválida.");
                    var entry = new PersistentCacheEntry(token, size, new DateTime(ticks, DateTimeKind.Utc), policy);
                    characters += entry.TextCharacters;
                    if (characters > PersistentSizeCache.MaximumTextCharacters) throw new InvalidDataException("Texto do cache excede o limite.");
                    if (AcceptTime(entry.FinishedUtc, now)) result.Add(entry);
                }
                if (memory.Position != payload) throw new InvalidDataException("Conteúdo adicional no cache.");
            }
            return result;
        }

        internal static List<PersistentCacheEntry> Load(string filePath, DateTime now)
        {
            if (!File.Exists(filePath)) return new List<PersistentCacheEntry>();
            using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                long length = input.Length;
                if (length < 0 || length > PersistentSizeCache.MaximumFileBytes) throw new InvalidDataException("Cache excede o limite de arquivo.");
                byte[] bytes = new byte[(int)length]; int offset = 0;
                Stopwatch timer = Stopwatch.StartNew();
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, Math.Min(16384, bytes.Length - offset));
                    if (read == 0) throw new EndOfStreamException("Cache incompleto.");
                    offset += read;
                    if (timer.ElapsedMilliseconds > PersistentSizeCache.ValidationTimeoutMilliseconds)
                        throw new TimeoutException("Leitura do cache excedeu o prazo.");
                }
                if (input.ReadByte() != -1) throw new InvalidDataException("Cache alterado durante a leitura.");
                return Decode(bytes, now);
            }
        }

#if PERSISTENT_CACHE_TESTS
        internal static Action<string> BeforeCommitForTests;
#endif

        internal static void WriteAtomic(string filePath, IEnumerable<PersistentCacheEntry> entries)
        {
            byte[] bytes = Encode(entries);
            string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
            Directory.CreateDirectory(directory);
            string temporary = System.IO.Path.Combine(directory, ".tamanhos-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
#if PERSISTENT_CACHE_TESTS
                Action<string> beforeCommit = BeforeCommitForTests;
                if (beforeCommit != null) beforeCommit(temporary);
#endif
                if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
                else File.Move(temporary, filePath);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length == 0 || bytes.Length > MaximumStringBytes) throw new InvalidDataException("Texto inválido no cache.");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        private static string ReadString(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count <= 0 || count > MaximumStringBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Comprimento de texto inválido.");
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return Utf8.GetString(bytes);
        }
    }
}
