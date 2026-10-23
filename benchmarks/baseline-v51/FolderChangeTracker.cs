using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PastaGBIcons
{
    internal sealed class FolderChangeBatch
    {
        internal bool ReconcileAll;
        internal string[] Paths = new string[0];
        internal long Revision;
    }

    // Recursive content watches are paired with shallow parent guards. A guard
    // observes replacement of the watched directory itself, without treating
    // all its siblings (or all of C:\) as dependencies of that directory.
    // All native watch creation/disposal stays on this single background thread.
    internal sealed class FolderChangeTracker : IDisposable
    {
        private const int MaximumRecursiveWatchers = 12, MaximumGuardWatchers = 12;
        private const int MaximumPendingPaths = 256, MaximumCandidates = 4096;
        private const int MaximumCachedCandidateCharacters = 262144;
        private static readonly StringComparer Comparer = Path.DirectorySeparatorChar == '\\' ?
            StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly object gate = new object();
        private readonly object requestGate = new object();
        private readonly List<string> lastCandidates = new List<string>();
        private bool hasCandidateSnapshot;
        private readonly Dictionary<string, WatchRegistration> recursive = new Dictionary<string, WatchRegistration>(Comparer);
        private readonly Dictionary<string, WatchRegistration> guards = new Dictionary<string, WatchRegistration>(Comparer);
        private readonly Dictionary<string, DateTime> rootRetry = new Dictionary<string, DateTime>(Comparer);
        private readonly Dictionary<string, DateTime> guardRetry = new Dictionary<string, DateTime>(Comparer);
        // At most the desired recursive roots and their guard parents. A newer
        // topology event must win over an older watch-construction outcome.
        private readonly Dictionary<string, long> topology = new Dictionary<string, long>(Comparer);
        private readonly HashSet<string> invalidRoots = new HashSet<string>(Comparer);
        private readonly HashSet<string> invalidGuards = new HashSet<string>(Comparer);
        private readonly HashSet<string> pending = new HashSet<string>(Comparer);
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly Action beforeCycle;
#if WATCHER_RACE_TESTS
        private readonly Action<string, bool> beforeRegistrationFailure;
#endif
        private readonly int recoveryWaitMilliseconds;
        private string[] desired = new string[0];
        private string error;
        private volatile bool stopped;
        private bool reconcileAll;
        private long revision, notifications, overflows, lastPulseTicks, recoveries;

        private sealed class WatchRegistration
        {
            internal string Path;
            internal bool Recursive, Enabled;
            internal long TopologyGeneration;
            internal FileSystemWatcher Watcher;
            internal HashSet<string> Entries = new HashSet<string>(Comparer);
        }

        internal FolderChangeTracker() : this(null, 5000) { }

        // The optional cycle callback is used only by the in-assembly regression harness.
        internal FolderChangeTracker(Action beforeCycle, int recoveryWaitMilliseconds)
        {
            this.beforeCycle = beforeCycle;
            this.recoveryWaitMilliseconds = Math.Max(1, recoveryWaitMilliseconds);
            worker = new Thread(Run) { IsBackground = true, Name = "PastaGB.ChangeTracker" };
            worker.Start();
        }

#if WATCHER_RACE_TESTS
        // Compiled only into the race harness, never into the shipped executable.
        internal FolderChangeTracker(Action beforeCycle, int recoveryWaitMilliseconds,
            Action<string, bool> beforeRegistrationFailure) : this(beforeCycle, recoveryWaitMilliseconds)
        { this.beforeRegistrationFailure = beforeRegistrationFailure; }
#endif

        internal long Revision { get { return Interlocked.Read(ref revision); } }
        internal int RecursiveWatcherCount { get { lock (gate) return recursive.Count; } }
        internal int GuardWatcherCount { get { lock (gate) return guards.Count; } }
        internal long Recoveries { get { return Interlocked.Read(ref recoveries); } }

        internal void Track(IEnumerable<string> paths)
        {
            // Explorer supplies the same bounded demand on most coordinator
            // passes. Compare it before full-path normalization and ancestor
            // collapsing. This cache concerns requested paths only: the worker
            // still retries failed watches and IsCovered still verifies health.
            lock (requestGate)
            {
                bool unchanged = hasCandidateSnapshot;
                hasCandidateSnapshot = false;
                bool cacheable = true;
                int characters = 0;
                try
                {
                    int examined = 0;
                    foreach (string candidate in paths ?? new string[0])
                    {
                        if (examined >= MaximumCandidates) break;
                        if (cacheable && candidate != null)
                        {
                            if (candidate.Length > MaximumCachedCandidateCharacters - characters) cacheable = false;
                            else characters += candidate.Length;
                        }
                        if (examined >= lastCandidates.Count)
                        { lastCandidates.Add(candidate); unchanged = false; }
                        else if (!String.Equals(lastCandidates[examined], candidate, StringComparison.Ordinal))
                        { lastCandidates[examined] = candidate; unchanged = false; }
                        examined++;
                    }
                    if (examined < lastCandidates.Count)
                    { lastCandidates.RemoveRange(examined, lastCandidates.Count - examined); unchanged = false; }
                    if (unchanged) { hasCandidateSnapshot = true; return; }

                    var accepted = new List<string>();
                    foreach (string candidate in lastCandidates)
                    {
                        string path = NormalizeLocal(candidate);
                        if (path == null) continue;
                        if (accepted.Any(x => Contains(x, path))) continue;
                        // Prefer one watched ancestor to many ordinary child handles.
                        // External junction targets remain separate dependencies.
                        accepted.RemoveAll(x => Contains(path, x));
                        if (accepted.Count < MaximumRecursiveWatchers) accepted.Add(path);
                    }
                    lock (gate)
                    {
                        if (desired.SequenceEqual(accepted, Comparer)) { hasCandidateSnapshot = true; return; }
                        desired = accepted.ToArray();
                    }
                    hasCandidateSnapshot = true;
                }
                finally
                {
                    // Oversized or interrupted requests still follow the normal
                    // bounded watch-selection path. Only their reuse cache is
                    // dropped, so long/invalid strings cannot stay retained.
                    if (!cacheable || !hasCandidateSnapshot)
                    { lastCandidates.Clear(); hasCandidateSnapshot = false; }
                }
            }
            Signal();
        }

        private static string NormalizeLocal(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            if (Path.DirectorySeparatorChar == '\\')
            {
                if (path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' ||
                    (path[2] != '\\' && path[2] != '/')) return null;
            }
            else if (!path.StartsWith("/", StringComparison.Ordinal)) return null;
            try
            {
                string full = Path.GetFullPath(path), root = Path.GetPathRoot(full);
                return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return null;
                throw;
            }
        }

        private static bool Contains(string parent, string child)
        {
            if (Comparer.Equals(parent, child)) return true;
            int length = parent.Length;
            while (length > 0 && (parent[length - 1] == Path.DirectorySeparatorChar ||
                parent[length - 1] == Path.AltDirectorySeparatorChar)) length--;
            return child.Length > length && child[length] == Path.DirectorySeparatorChar &&
                String.Compare(parent, 0, child, 0, length, Path.DirectorySeparatorChar == '\\' ?
                    StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;
        }

        private static string ParentOf(string path)
        {
            string root = Path.GetPathRoot(path);
            // A filesystem volume root cannot be renamed through a parent.
            // Removal is covered by watch errors plus the volume monitor.
            return Comparer.Equals(root, path) ? null : Path.GetDirectoryName(path);
        }

        internal bool IsCovered(string path)
        {
            path = NormalizeLocal(path);
            if (path == null || stopped || !worker.IsAlive ||
                DateTime.UtcNow.Ticks - Interlocked.Read(ref lastPulseTicks) > TimeSpan.FromSeconds(8).Ticks)
                return false;
            lock (gate)
            {
                foreach (WatchRegistration registration in recursive.Values)
                {
                    if (!registration.Enabled || invalidRoots.Contains(registration.Path) || !Contains(registration.Path, path)) continue;
                    string parent = ParentOf(registration.Path);
                    if (parent == null) return true;
                    WatchRegistration guard;
                    if (guards.TryGetValue(parent, out guard) && guard.Enabled && !invalidGuards.Contains(parent) &&
                        guard.Entries.Contains(registration.Path)) return true;
                    // A healthy recursive ancestor can itself observe this
                    // root's entry. Normally Track has already collapsed these.
                    foreach (WatchRegistration ancestor in recursive.Values)
                        if (!Comparer.Equals(ancestor.Path, registration.Path) && ancestor.Enabled &&
                            !invalidRoots.Contains(ancestor.Path) && Contains(ancestor.Path, registration.Path) && HasGuard(ancestor)) return true;
                }
                return false;
            }
        }

        private bool HasGuard(WatchRegistration registration)
        {
            string parent = ParentOf(registration.Path);
            if (parent == null) return true;
            WatchRegistration guard;
            return guards.TryGetValue(parent, out guard) && guard.Enabled && !invalidGuards.Contains(parent) &&
                guard.Entries.Contains(registration.Path);
        }

        internal FolderChangeBatch Drain()
        {
            lock (gate)
            {
                var result = new FolderChangeBatch { ReconcileAll = reconcileAll,
                    Paths = pending.ToArray(), Revision = Interlocked.Read(ref revision) };
                pending.Clear(); reconcileAll = false;
                return result;
            }
        }

        private bool IsCurrent(WatchRegistration registration)
        {
            WatchRegistration active;
            return (registration.Recursive ? recursive : guards).TryGetValue(registration.Path, out active) &&
                Object.ReferenceEquals(active, registration);
        }

        private void OnContentChange(WatchRegistration registration, string path)
        {
            if (stopped) return;
            lock (gate)
            {
                if (!IsCurrent(registration)) return;
                QueueChange(path);
            }
        }

        private void OnGuardChange(WatchRegistration registration, string path)
        {
            if (stopped) return;
            path = NormalizeLocal(path);
            lock (gate)
            {
                if (path == null || !IsCurrent(registration) || !registration.Entries.Contains(path)) return;
                // Even a live watcher can remain bound to the old directory
                // after rename or junction replacement. Retire it and create
                // another before claiming coverage of the original name.
                TouchTopology(path);
                invalidRoots.Add(path); rootRetry.Remove(path);
                QueueChange(path);
            }
            Signal();
        }

        // Call with gate held. Only exact event paths enter the bounded queue.
        private void QueueChange(string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            Interlocked.Increment(ref revision); Interlocked.Increment(ref notifications);
            if (reconcileAll) return;
            if (pending.Contains(path)) return;
            if (pending.Count >= MaximumPendingPaths)
            { pending.Clear(); reconcileAll = true; Interlocked.Increment(ref overflows); }
            else pending.Add(path);
        }

        private void OnError(WatchRegistration registration, Exception exception)
        {
            if (stopped) return;
            lock (gate)
            {
                if (!IsCurrent(registration)) return;
                Interlocked.Increment(ref revision); Interlocked.Increment(ref overflows);
                pending.Clear(); reconcileAll = true;
                (registration.Recursive ? invalidRoots : invalidGuards).Add(registration.Path);
                if (TopologyVersion(registration.Path) == registration.TopologyGeneration)
                {
                    TouchTopology(registration.Path);
                    (registration.Recursive ? rootRetry : guardRetry)[registration.Path] = DateTime.UtcNow.AddSeconds(5);
                }
                error = "Monitoramento será revalidado: " + (exception == null ? "notificação perdida" : exception.Message);
            }
            Signal();
        }

        private void Signal()
        { try { changed.Set(); } catch (ObjectDisposedException) { } }

        // Call with gate held. Generations are scoped to an observed path, so a
        // busy unrelated directory cannot suppress another root's backoff.
        private long TopologyVersion(string path)
        { long value; return topology.TryGetValue(path, out value) ? value : 0; }

        private void TouchTopology(string path)
        { topology[path] = unchecked(TopologyVersion(path) + 1); }

        private void Run()
        {
            try
            {
                while (!stopped)
                {
                    try { ReconcileLoop(); }
                    catch (Exception ex)
                    {
                        lock (gate)
                        {
                            error = "Monitoramento em recuperação: " + ex.Message;
                            pending.Clear(); reconcileAll = true;
                            Interlocked.Increment(ref revision); Interlocked.Increment(ref recoveries);
                            Interlocked.Exchange(ref lastPulseTicks, 0);
                        }
                        ClearWatches();
                        if (!stopped) changed.WaitOne(recoveryWaitMilliseconds);
                    }
                }
            }
            finally
            {
                ClearWatches();
                changed.Dispose();
            }
        }

        private void ClearWatches()
        {
            WatchRegistration[] remaining;
            lock (gate) remaining = recursive.Values.Concat(guards.Values).ToArray();
            foreach (WatchRegistration registration in remaining) Remove(registration);
        }

        private void ReconcileLoop()
        {
            while (!stopped)
            {
                if (beforeCycle != null) beforeCycle();
                Interlocked.Exchange(ref lastPulseTicks, DateTime.UtcNow.Ticks);
                string[] roots;
                lock (gate) roots = desired;
                var wantedGuards = new Dictionary<string, HashSet<string>>(Comparer);
                foreach (string path in roots)
                {
                    string parent = ParentOf(path);
                    if (parent == null) continue;
                    HashSet<string> entries;
                    if (!wantedGuards.TryGetValue(parent, out entries))
                    {
                        if (wantedGuards.Count >= MaximumGuardWatchers) continue;
                        entries = new HashSet<string>(Comparer); wantedGuards[parent] = entries;
                    }
                    entries.Add(path);
                }
                ReconcileExisting(roots, wantedGuards);
                foreach (var pair in wantedGuards)
                { if (stopped) break; EnsureWatch(pair.Key, false, pair.Value); }
                foreach (string path in roots)
                { if (stopped) break; EnsureWatch(path, true, null); }
                Interlocked.Exchange(ref lastPulseTicks, DateTime.UtcNow.Ticks);
                changed.WaitOne(1000);
            }
        }

        private void ReconcileExisting(string[] roots, Dictionary<string, HashSet<string>> wantedGuards)
        {
            WatchRegistration[] existing;
            lock (gate) existing = recursive.Values.Concat(guards.Values).ToArray();
            foreach (WatchRegistration registration in existing)
            {
                bool remove;
                lock (gate)
                    remove = registration.Recursive ? !roots.Contains(registration.Path, Comparer) || invalidRoots.Contains(registration.Path) :
                        !wantedGuards.ContainsKey(registration.Path) || invalidGuards.Contains(registration.Path);
                if (remove) Remove(registration);
            }
            lock (gate)
            {
                foreach (string path in rootRetry.Keys.Where(x => !roots.Contains(x, Comparer)).ToArray()) rootRetry.Remove(path);
                foreach (string path in guardRetry.Keys.Where(x => !wantedGuards.ContainsKey(x)).ToArray()) guardRetry.Remove(path);
                invalidRoots.RemoveWhere(x => !roots.Contains(x, Comparer));
                invalidGuards.RemoveWhere(x => !wantedGuards.ContainsKey(x));
                foreach (string path in topology.Keys.Where(x => !roots.Contains(x, Comparer) && !wantedGuards.ContainsKey(x)).ToArray())
                    topology.Remove(path);
                foreach (var pair in wantedGuards)
                {
                    WatchRegistration registration;
                    if (guards.TryGetValue(pair.Key, out registration)) registration.Entries = pair.Value;
                }
            }
        }

        private void EnsureWatch(string path, bool recursiveWatch, HashSet<string> entries)
        {
            Dictionary<string, WatchRegistration> collection = recursiveWatch ? recursive : guards;
            Dictionary<string, DateTime> retry = recursiveWatch ? rootRetry : guardRetry;
            HashSet<string> invalid = recursiveWatch ? invalidRoots : invalidGuards;
            long attemptTopology;
            lock (gate)
            {
                DateTime next;
                if (collection.ContainsKey(path) || (retry.TryGetValue(path, out next) && DateTime.UtcNow < next)) return;
                if (collection.Count >= (recursiveWatch ? MaximumRecursiveWatchers : MaximumGuardWatchers)) return;
                attemptTopology = TopologyVersion(path);
            }
            WatchRegistration registration = new WatchRegistration
            {
                Path = path, Recursive = recursiveWatch,
                TopologyGeneration = attemptTopology,
                Entries = entries ?? new HashSet<string>(Comparer)
            };
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(path); registration.Watcher = watcher;
                watcher.IncludeSubdirectories = recursiveWatch;
                watcher.NotifyFilter = recursiveWatch ? NotifyFilters.FileName | NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Attributes :
                    NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime | NotifyFilters.Attributes;
                watcher.InternalBufferSize = 8192;
                watcher.Changed += delegate(object sender, FileSystemEventArgs e) { Changed(registration, e.FullPath); };
                watcher.Created += delegate(object sender, FileSystemEventArgs e) { Changed(registration, e.FullPath); };
                watcher.Deleted += delegate(object sender, FileSystemEventArgs e) { Changed(registration, e.FullPath); };
                watcher.Renamed += delegate(object sender, RenamedEventArgs e)
                { Changed(registration, e.OldFullPath); Changed(registration, e.FullPath); };
                watcher.Error += delegate(object sender, ErrorEventArgs e) { OnError(registration, e.GetException()); };
                bool superseded;
                lock (gate)
                {
                    superseded = TopologyVersion(path) != attemptTopology;
                    if (!superseded)
                    { collection[path] = registration; invalid.Remove(path); retry.Remove(path); }
                }
                if (superseded) { watcher.Dispose(); Signal(); return; }
                watcher.EnableRaisingEvents = true;
                lock (gate)
                {
                    if (IsCurrent(registration))
                    {
                        // Events delivered while EnableRaisingEvents was in
                        // progress must not be hidden by a successful return.
                        if (TopologyVersion(path) == attemptTopology && !invalid.Contains(path)) registration.Enabled = true;
                        else invalid.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
#if WATCHER_RACE_TESTS
                if (beforeRegistrationFailure != null) beforeRegistrationFailure(path, recursiveWatch);
#endif
                lock (gate)
                {
                    if (IsCurrent(registration)) collection.Remove(path);
                    // A guard may already have observed this name being
                    // recreated after the failed attempt. Preserve that newer
                    // invalidation/retry decision instead of imposing 30 s.
                    if (TopologyVersion(path) == attemptTopology)
                    { invalid.Remove(path); retry[path] = DateTime.UtcNow.AddSeconds(30); }
                    error = "Reconciliação periódica em uso: " + ex.Message;
                    // Registration failed: changes in this interval are unknown.
                    pending.Clear(); reconcileAll = true; Interlocked.Increment(ref revision);
                }
                if (registration.Watcher != null) try { registration.Watcher.Dispose(); } catch { }
            }
        }

        private void Changed(WatchRegistration registration, string path)
        {
            if (registration.Recursive) OnContentChange(registration, path);
            else OnGuardChange(registration, path);
        }

        private void Remove(WatchRegistration registration)
        {
            lock (gate)
            {
                if (!IsCurrent(registration)) return;
                (registration.Recursive ? recursive : guards).Remove(registration.Path);
                (registration.Recursive ? invalidRoots : invalidGuards).Remove(registration.Path);
                registration.Enabled = false;
            }
            try { registration.Watcher.Dispose(); } catch { }
        }

        internal string Diagnostic
        {
            get
            {
                lock (gate) return recursive.Count + "/" + MaximumRecursiveWatchers + " raízes e " +
                    guards.Count + "/" + MaximumGuardWatchers + " guardas de substituição; " +
                    Interlocked.Read(ref notifications) + " notificações; " + Interlocked.Read(ref overflows) +
                    " reconciliações por perda/volume de eventos; " + (worker.IsAlive ? "ativo" : "inativo") +
                    "; recuperações=" + Interlocked.Read(ref recoveries) +
                    (String.IsNullOrEmpty(error) ? String.Empty : "; " + error);
            }
        }

        public void Dispose()
        {
            if (stopped) return;
            stopped = true; Signal();
            // A blocked native watch never holds shutdown or the foreground UI.
            worker.Join(150);
        }
    }
}
