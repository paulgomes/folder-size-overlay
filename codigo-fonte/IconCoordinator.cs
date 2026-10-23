using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PastaGB;

namespace PastaGBIcons
{
    internal sealed partial class IconContext
    {
        private const int MaximumSizeRecords = 2048, MaximumDemand = 2048;
        private long sharedSubtotals;
        private readonly AutoResetEvent scannerWake = new AutoResetEvent(false);
        private long scannerDemandRevision, coordinatorPasses, coordinatorPublications, coordinatorDemandBuilds, coordinatorSignalCoalesces;
        private int coordinatorLastWait;

        // Signals are deliberately separate: a content change wakes scheduling
        // but does not rebuild the unchanged set of visible paths.
        private void WakeScanner()
        { try { scannerWake.Set(); } catch (ObjectDisposedException) { } }

        private void ScanDemandChanged()
        { Interlocked.Increment(ref scannerDemandRevision); WakeScanner(); }

        internal string CoordinatorDiagnostic
        {
            get { return "rodadas=" + Interlocked.Read(ref coordinatorPasses) +
                "; demandas reconstruídas=" + Interlocked.Read(ref coordinatorDemandBuilds) +
                "; publicações solicitadas=" + Interlocked.Read(ref coordinatorPublications) +
                "; agrupamentos de sinais=" + Interlocked.Read(ref coordinatorSignalCoalesces) +
                "; última espera=" + Volatile.Read(ref coordinatorLastWait) + " ms"; }
        }

        private sealed class DemandSnapshot
        {
            internal readonly HashSet<string> Wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Foreground = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> WatchRoots = new List<string>();
            internal readonly List<object> Snapshots = new List<object>();
            internal readonly List<IntPtr> Roots = new List<IntPtr>();
            internal readonly List<string> Locations = new List<string>();
            internal long Revision;
            internal IntPtr Focused;
            internal int Omitted;

            internal bool Matches(List<SurfaceState> surfaces, IntPtr focused, long revision)
            {
                if (Focused != focused || Revision != revision || Snapshots.Count != surfaces.Count) return false;
                for (int i = 0; i < surfaces.Count; i++)
                {
                    SurfaceState surface = surfaces[i];
                    if (!Object.ReferenceEquals(Snapshots[i], surface.Snapshot) ||
                        Roots[i] != (surface.Explorer == null ? IntPtr.Zero : surface.Explorer.RootHandle) ||
                        !String.Equals(Locations[i], surface.Explorer == null ? null : surface.Explorer.Location, StringComparison.Ordinal)) return false;
                }
                return true;
            }
        }

        private bool MediaCurrent(SizeRecord record)
        { return record != null && volumes.MatchesDependencies(record.VolumeStamp, record.Path, record.Dependencies); }

        private static SizeRecord Waiting(string path, long[] stamp)
        {
            return new SizeRecord { Path = path, Phase = SizePhase.Queued,
                Detail = "Aguardando uma oportunidade de leitura", VolumeStamp = stamp,
                Dependencies = new[] { path } };
        }

        private void ScanLoop()
        {
            var records = new Dictionary<string, SizeRecord>(StringComparer.OrdinalIgnoreCase);
            var lastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var firstWanted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var active = new List<ScanWork>();
            var debounce = new ChangeDebounce();
            DemandSnapshot demand = null;
            var watchRoots = new List<string>();
            var invalidPaths = new List<string>();
            long[] observedVolumes = null;
            int generation = Volatile.Read(ref refreshGeneration);
            int appliedPolicy;
            PerformanceMode appliedPerformance;
            lock (stateGate)
            {
                appliedPolicy = policyGeneration;
                appliedPerformance = preferences.Performance;
                foreach (var pair in sizes)
                {
                    SizeRecord value = pair.Value.Copy();
                    if (value.IsReading) value.Phase = value.HasValue ? SizePhase.Stale : SizePhase.Queued;
                    if (MediaCurrent(value)) { records[pair.Key] = value; lastSeen[pair.Key] = DateTime.UtcNow; }
                }
            }
            bool reserveForeground = false;
            bool wasPaused = paused, watchPlanDirty = true;
            DateTime nextActivity = DateTime.MinValue, nextEviction = DateTime.MinValue;
            try
            {
                while (!stopped)
                {
                    long passStarted = Stopwatch.GetTimestamp();
                    DateTime now = DateTime.UtcNow;
                    Interlocked.Increment(ref coordinatorPasses);
                    AppPreferences settings; int currentPolicy;
                    lock (stateGate) { settings = preferences; currentPolicy = policyGeneration; }
                    IntPtr focusedWindow = GetForegroundWindow();
                    List<SurfaceState> surfaces = paused ? new List<SurfaceState>() : CurrentSurfaces(false);
                    // Stable in-place partition: foreground first, retaining
                    // all previous tie ordering and the bounded-demand choice.
                    int foregroundEnd = 0;
                    for (int index = 0; index < surfaces.Count; index++)
                    {
                        SurfaceState surface = surfaces[index];
                        if (surface.Explorer == null || surface.Explorer.RootHandle != focusedWindow) continue;
                        if (index != foregroundEnd) { surfaces.RemoveAt(index); surfaces.Insert(foregroundEnd, surface); }
                        foregroundEnd++;
                    }
                    long demandRevision = Interlocked.Read(ref scannerDemandRevision);
                    bool demandChanged = demand == null || !demand.Matches(surfaces, focusedWindow, demandRevision);
                    if (demandChanged)
                    {
                        demand = new DemandSnapshot { Focused = focusedWindow, Revision = demandRevision };
                        foreach (SurfaceState surface in surfaces)
                        {
                            demand.Snapshots.Add(surface.Snapshot);
                            demand.Roots.Add(surface.Explorer == null ? IntPtr.Zero : surface.Explorer.RootHandle);
                            demand.Locations.Add(surface.Explorer == null ? null : surface.Explorer.Location);
                            if (surface.Explorer != null && VolumeRevisions.DriveIndex(surface.Explorer.Location) >= 0)
                                demand.WatchRoots.Add(surface.Explorer.Location);
                            foreach (var item in surface.Snapshot.Folders)
                            {
                                if (String.IsNullOrEmpty(item.Path)) continue;
                                if (demand.Wanted.Count >= MaximumDemand && !demand.Wanted.Contains(item.Path)) { demand.Omitted++; continue; }
                                demand.Wanted.Add(item.Path);
                                if (surface.Explorer != null && surface.Explorer.RootHandle == focusedWindow) demand.Foreground.Add(item.Path);
                                if (surface.Explorer == null)
                                {
                                    string parent = Path.GetDirectoryName(item.Path);
                                    demand.WatchRoots.Add(String.IsNullOrEmpty(parent) ? item.Path : parent);
                                }
                            }
                        }
                        foreach (string path in firstWanted.Keys.Where(x => !demand.Wanted.Contains(x)).ToArray()) firstWanted.Remove(path);
                        watchPlanDirty = true;
                        Interlocked.Increment(ref coordinatorDemandBuilds);
                    }
                    HashSet<string> wanted = demand.Wanted, foreground = demand.Foreground;
                    int omittedDemand = demand.Omitted;
                    foreach (string path in wanted)
                    { lastSeen[path] = now; if (!firstWanted.ContainsKey(path)) firstWanted[path] = now; }

                    bool publish = false;
                    bool policyChangedThisPass = currentPolicy != appliedPolicy;
                    if (settings.Performance != appliedPerformance)
                    {
                        appliedPerformance = settings.Performance;
                        foreach (ScanWork work in active) { work.CancelRequested = true; work.Session.Cancel(); }
                        RecordEvent("Perfil de desempenho alterado; leitores serão retomados com os novos limites.");
                    }
                    if (currentPolicy != appliedPolicy)
                    {
                        appliedPolicy = currentPolicy; records.Clear(); lastSeen.Clear(); debounce.Clear();
                        foreach (ScanWork work in active) { work.CancelRequested = true; work.Session.Cancel(); }
                        RecordEvent("Política de ligações alterada; resultados anteriores retirados.");
                        publish = true; watchPlanDirty = true;
                    }
                    if (observedVolumes == null || !volumes.MatchesAll(observedVolumes))
                    {
                        observedVolumes = volumes.Capture(); invalidPaths.Clear();
                        foreach (var pair in records) if (!MediaCurrent(pair.Value)) invalidPaths.Add(pair.Key);
                        foreach (string path in invalidPaths)
                        { records.Remove(path); lastSeen.Remove(path); debounce.Forget(path); if (wanted.Contains(path)) firstWanted[path] = now; publish = true; }
                        if (invalidPaths.Count > 0) watchPlanDirty = true;
                    }

                    bool resumed = wasPaused && !paused;
                    if (wasPaused != paused) { wasPaused = paused; watchPlanDirty = true; }
                    if (!paused && (demandChanged || policyChangedThisPass)) RequestCachedSizes(wanted, settings, currentPolicy);
                    if (!paused)
                    {
                        foreach (string path in wanted)
                        {
                            SizeRecord prior, restored;
                            records.TryGetValue(path, out prior);
                            if (prior != null && prior.HasValue) continue;
                            ScanWork readingWork = null;
                            if (prior != null && prior.IsReading)
                            {
                                foreach (ScanWork work in active)
                                    if (String.Equals(work.Path, path, StringComparison.OrdinalIgnoreCase))
                                    { readingWork = work; break; }
                                if (readingWork == null || readingWork.PolicyGeneration != currentPolicy ||
                                    !volumes.MatchesAll(readingWork.VolumeStamp)) continue;
                            }
                            if (!TryRestoreCachedSize(path, settings, currentPolicy, out restored) || restored == null ||
                                !restored.HasValue || !String.Equals(restored.Path, path, StringComparison.OrdinalIgnoreCase) || !MediaCurrent(restored)) continue;
                            restored = restored.Copy(); restored.Phase = SizePhase.Stale;
                            if (prior != null && prior.NextAttemptUtc > restored.NextAttemptUtc)
                            { restored.NextAttemptUtc = prior.NextAttemptUtc; restored.FailureCount = prior.FailureCount; }
                            if (readingWork != null)
                            {
                                // Identity validation is asynchronous; a large
                                // folder's normal read may already be running.
                                // Attach the last known total to that same work
                                // without restarting or treating it as current.
                                readingWork.Previous = restored;
                                SizeRecord reading = restored.Copy(); reading.Phase = SizePhase.Refreshing;
                                reading.NextAttemptUtc = prior.NextAttemptUtc;
                                reading.Detail = "Último tamanho conhecido; leitura em processo independente";
                                records[path] = reading;
                            }
                            else records[path] = restored;
                            publish = true; watchPlanDirty = true;
                        }
                    }

                    int requested = Volatile.Read(ref refreshGeneration);
                    bool manualRefresh = requested != generation;
                    if (manualRefresh) { generation = requested; debounce.Clear(); }
                    FolderChangeBatch batch = changes.Drain();
                    // Most rounds contain no notifications. Do not allocate
                    // per-record predicates or a key snapshot for an empty batch.
                    if (manualRefresh || resumed || batch.ReconcileAll || batch.Paths.Length > 0)
                    {
                        foreach (string path in records.Keys.ToArray())
                        {
                            SizeRecord record = records[path];
                            bool dirty = manualRefresh || resumed || batch.ReconcileAll ||
                                batch.Paths.Any(x => PathRelations.Affects(path, record.Dependencies, x));
                            if (!dirty) continue;
                            SizeRecord changed = record.Invalidate(manualRefresh ? "Atualização solicitada" : "Conteúdo alterado; aguardando conferência");
                            // Bound the quiet-period debounce by the first
                            // event, so constant writes cannot postpone reads.
                            if (changed.HasValue && !changed.IsReading && !manualRefresh && !resumed)
                                changed.NextAttemptUtc = debounce.Mark(path, now);
                            else if (manualRefresh || resumed) debounce.Forget(path);
                            records[path] = changed;
                            if (record.Phase != changed.Phase || !String.Equals(record.Detail, changed.Detail, StringComparison.Ordinal)) publish = true;
                        }
                    }

                    foreach (ScanWork work in active.ToArray())
                    {
                        if (wanted.Contains(work.Path)) work.LastWantedUtc = now;
                        if (manualRefresh || batch.ReconcileAll || batch.Paths.Any(x =>
                            PathRelations.Affects(work.Path, work.Previous == null ? null : work.Previous.Dependencies, x)))
                            work.ChangedDuringRead = true;
                        bool mediaChanged = !volumes.MatchesAll(work.VolumeStamp);
                        bool policyChanged = currentPolicy != work.PolicyGeneration;
                        if (paused || mediaChanged || policyChanged || (now - work.LastWantedUtc).TotalSeconds > 10)
                        { work.Session.Cancel(); work.CancelRequested = true; }
                        if (!work.Session.Completed) continue;
                        ScanResult result = work.Session.Result;
                        active.Remove(work);
                        try
                        {
                            if (mediaChanged || policyChanged || !volumes.MatchesAll(work.VolumeStamp))
                            {
                                records.Remove(work.Path); lastSeen.Remove(work.Path);
                                if (wanted.Contains(work.Path)) firstWanted[work.Path] = now;
                            }
                            else if (work.CancelRequested || (result != null && result.Cancelled))
                            {
                                SizeRecord previous = work.Previous;
                                if (previous != null && previous.HasValue && MediaCurrent(previous))
                                { SizeRecord kept = previous.Copy(); kept.Phase = SizePhase.Stale;
                                    kept.Detail = "Último total conhecido; leitura interrompida será retomada";
                                    kept.NextAttemptUtc = now.AddSeconds(1); records[work.Path] = kept; }
                                else records[work.Path] = Waiting(work.Path, work.VolumeStamp);
                            }
                            else
                            {
                                bool stale = work.ChangedDuringRead || work.Generation != generation;
                                // Dependencies found through links may have received notifications
                                // outside the lexical root while this read was running.
                                if (result != null && result.DependencyPaths != null && result.DependencyPaths.Count > 1 &&
                                    work.ChangeRevision != changes.Revision) stale = true;
                                SizeRecord ready = SizeResultPolicy.Create(work.Path, work.VolumeStamp, result, now, stale, work.Previous);
                                records[work.Path] = ready;
                                if (ready.Phase == SizePhase.Ready)
                                    RememberCompletedSize(ready, result, settings, currentPolicy);
                                if (ready.Phase == SizePhase.Unavailable)
                                { lock (stateGate) { scannerError = ready.Detail; } RecordEvent("Falha de leitura: " + ready.Detail); }
                                else
                                {
                                    lock (stateGate) { scannerError = null; }
                                    // Only completed, valid subtrees can supply a different folder's value.
                                    if (!stale && result != null && result.Subtotals != null)
                                    {
                                        foreach (FolderSummary child in result.Subtotals)
                                        {
                                            if (child == null || String.IsNullOrEmpty(child.Path) ||
                                                String.Equals(child.Path, work.Path, StringComparison.OrdinalIgnoreCase) ||
                                                active.Any(x => PathRelations.Overlaps(x.Path, child.Path))) continue;
                                            SizeRecord childRecord = SizeResultPolicy.Subtotal(ready, child);
                                            if (!MediaCurrent(childRecord)) continue;
                                            records[child.Path] = childRecord;
                                            if (!lastSeen.ContainsKey(child.Path)) lastSeen[child.Path] = now;
                                            Interlocked.Increment(ref sharedSubtotals);
                                        }
                                    }
                                }
                            }
                        }
                        finally { work.Session.Dispose(); }
                        publish = true; watchPlanDirty = true;
                    }

                    // Register dependencies as well as paths shown by the Shell. Watch creation is asynchronous.
                    if (watchPlanDirty)
                    {
                        watchRoots.Clear();
                        if (!paused)
                        {
                            watchRoots.AddRange(demand.WatchRoots);
                            foreach (string path in wanted)
                            {
                                watchRoots.Add(path);
                                SizeRecord value;
                                if (records.TryGetValue(path, out value)) watchRoots.AddRange(value.Dependencies);
                            }
                        }
                        changes.Track(watchRoots); watchPlanDirty = false;
                    }

                    Func<string, bool> due = delegate(string path)
                    {
                        SizeRecord record;
                        records.TryGetValue(path, out record);
                        bool monitored = SizeRefreshPolicy.NeedsCoverage(record, now, settings.ReconcileSeconds) &&
                            record.Dependencies.Length > 0;
                        if (monitored)
                            foreach (string dependency in record.Dependencies)
                                if (!changes.IsCovered(dependency)) { monitored = false; break; }
                        return SizeRefreshPolicy.Due(record, now, monitored, settings.ReconcileSeconds);
                    };

                    if (!paused)
                    {
                        // Avoid the per-path closure required by nested Any
                        // predicates, including when every value is fresh.
                        bool foregroundWaiting = false;
                        foreach (string path in foreground)
                        {
                            if (!due(path)) continue;
                            bool reading = false;
                            foreach (ScanWork work in active)
                                if (String.Equals(work.Path, path, StringComparison.OrdinalIgnoreCase))
                                { reading = true; break; }
                            if (!reading) { foregroundWaiting = true; break; }
                        }
                        if (!foregroundWaiting) reserveForeground = false;
                        if (foregroundWaiting)
                        {
                            // An active ancestor can block a foreground child even
                            // when a numerical slot is free. Let that background
                            // read yield, preserving the aged first-read guarantee.
                            ScanWork yielding = active.FirstOrDefault(w => !w.ProtectedFirstRead &&
                                !w.CancelRequested && !foreground.Contains(w.Path) &&
                                foreground.Any(path => due(path) && PathRelations.Overlaps(w.Path, path)));
                            if (yielding == null && active.Count >= settings.MaximumReaders &&
                                !active.Any(w => foreground.Contains(w.Path) || w.CancelRequested))
                                yielding = active.LastOrDefault(w => !w.ProtectedFirstRead && !w.CancelRequested);
                            if (yielding != null)
                            { yielding.Session.Cancel(); yielding.CancelRequested = true; reserveForeground = true; }
                        }
                        // A full reader pool has no queue decision to make.
                        // Only due work needs priority objects or sorting; the
                        // unchanged tie-breaks still protect aged first reads.
                        if (active.Count < settings.MaximumReaders)
                        {
                            var queue = wanted.Where(x => due(x)).Select(x => new { Path = x,
                                    Priority = ScanQueuePolicy.GetPriority(foreground.Contains(x),
                                        records.ContainsKey(x) && records[x].HasValue, firstWanted[x], now, reserveForeground),
                                    FirstWantedUtc = firstWanted[x],
                                    FinishedUtc = records.ContainsKey(x) ? records[x].FinishedUtc : DateTime.MinValue })
                                .OrderBy(x => x.Priority).ThenBy(x => x.Priority == 0 ? x.FirstWantedUtc : DateTime.MinValue)
                                .ThenBy(x => x.FinishedUtc).ThenBy(x => x.Path.Length)
                                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                            foreach (var candidate in queue)
                            {
                                string path = candidate.Path;
                                if (active.Count >= settings.MaximumReaders) break;
                                if (active.Any(x => PathRelations.Overlaps(x.Path, path)) || !due(path)) continue;
                                SizeRecord previous; records.TryGetValue(path, out previous);
                                if (ProcessScanSession.AvailableSlots <= 0)
                                {
                                    if (active.Count == 0)
                                    {
                                        SizeRecord blocked = previous != null && previous.HasValue ? previous.Copy() : Waiting(path, volumes.Capture());
                                        bool quarantined = ProcessScanSession.QuarantinedProcessCount > 0;
                                        blocked.Phase = blocked.HasValue ? SizePhase.Stale :
                                            quarantined ? SizePhase.Unavailable : SizePhase.Queued;
                                        blocked.Detail = quarantined ?
                                            "Aguardando encerramento confirmado de um leitor; recuperação limitada em andamento" :
                                            "Aguardando liberação do processo que concluiu a leitura anterior";
                                        blocked.NextAttemptUtc = now.AddMilliseconds(quarantined ? 5000 : 250);
                                        records[path] = blocked; publish = true;
                                    }
                                    break;
                                }
                                var options = new ScanOptions { FollowDirectoryLinks = settings.FollowDirectoryLinks,
                                    CrossVolumeLinks = settings.CrossVolumeLinks,
                                    MaxEntriesPerSecond = settings.EntriesPerSecond };
                                if (settings.Performance == PerformanceMode.Economical)
                                { options.MaxSubtotals = 1024; options.MaxDirectories = 75000; }
                                var work = new ScanWork { Path = path, Generation = generation, PolicyGeneration = currentPolicy,
                                    VolumeStamp = volumes.Capture(), Previous = previous, LastWantedUtc = now,
                                    ChangeRevision = changes.Revision,
                                    ProtectedFirstRead = ScanQueuePolicy.GetPriority(foreground.Contains(path),
                                        previous != null && previous.HasValue, firstWanted[path], now) == 0 };
                                work.Session = ProcessScanSession.Start(path, options, 120, settings.ReaderCpuPercent);
                                debounce.Forget(path);
                                active.Add(work);
                                SizeRecord reading = previous != null && previous.HasValue ? previous.Copy() : Waiting(path, work.VolumeStamp);
                                reading.Phase = reading.HasValue ? SizePhase.Refreshing : SizePhase.Scanning;
                                reading.Detail = "Leitura em processo independente"; reading.NextAttemptUtc = DateTime.MinValue;
                                records[path] = reading; publish = true;
                                if (reserveForeground && foreground.Contains(path)) { reserveForeground = false; break; }
                            }
                        }
                    }
                    foreach (string path in wanted)
                        if (!records.ContainsKey(path)) { records[path] = Waiting(path, volumes.Capture()); publish = true; }

                    if (now >= nextEviction || records.Count > MaximumSizeRecords)
                    {
                        var idle = records.Keys.Where(x => !wanted.Contains(x) && !active.Any(w =>
                            String.Equals(w.Path, x, StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(x => lastSeen.ContainsKey(x) ? lastSeen[x] : DateTime.MinValue).ToArray();
                        DateTime earliestExpiry = now.AddSeconds(30);
                        foreach (string path in idle)
                        {
                            DateTime seen;
                            if (!lastSeen.TryGetValue(path, out seen)) seen = DateTime.MinValue;
                            if ((now - seen).TotalMinutes <= 10 && records.Count <= MaximumSizeRecords)
                            {
                                DateTime expiry = seen.AddMinutes(10).AddMilliseconds(1);
                                if (expiry < earliestExpiry) earliestExpiry = expiry;
                                continue;
                            }
                            records.Remove(path); lastSeen.Remove(path); debounce.Forget(path); publish = true;
                        }
                        nextEviction = earliestExpiry;
                    }
                    if (publish)
                    {
                        PublishSizes(records, currentPolicy);
                        Interlocked.Increment(ref coordinatorPublications);
                    }
                    if (publish || demandChanged || (active.Count > 0 && now >= nextActivity))
                    {
                        var activity = new StringBuilder();
                        activity.AppendLine(active.Count + "/" + settings.MaximumReaders + " leitura(s); " + wanted.Count +
                            " pastas na demanda; " + records.Count + " resultados em memória; " + Interlocked.Read(ref sharedSubtotals) +
                            " subtotais reaproveitados.");
                        if (omittedDemand > 0) activity.AppendLine(omittedDemand + " itens excederam o limite de demanda desta rodada.");
                        foreach (ScanWork work in active)
                        {
                            ScanProgress progress = work.Session.GetProgress();
                            activity.AppendLine(work.Path + " | " + Math.Max(0, (now - progress.StartedUtc).TotalSeconds)
                                .ToString("0", CultureInfo.InvariantCulture) + " s | arquivos=" + progress.FileCount +
                                " | pastas=" + progress.FolderCount + " | cancelando=" + work.CancelRequested +
                                " | última entrada=" + progress.CurrentPath + " | " + work.Session.Diagnostic);
                        }
                        lock (stateGate)
                            if (policyGeneration == currentPolicy) scannerActivity = activity.ToString();
                        nextActivity = now.AddSeconds(1);
                    }
                    int wait = CoordinatorWaitPolicy.Begin(active.Count, settings.ScanInterval, paused);
                    DateTime waitNow = DateTime.UtcNow;
                    if (!paused)
                        foreach (string path in wanted)
                        {
                            SizeRecord record;
                            if (records.TryGetValue(path, out record))
                                wait = CoordinatorWaitPolicy.Include(wait, record, waitNow, settings.ReconcileSeconds);
                        }
                    Volatile.Write(ref coordinatorLastWait, wait);
                    if (!stopped && wait > 0)
                    {
                        long waitStarted = Stopwatch.GetTimestamp();
                        if (scannerWake.WaitOne(wait) && !stopped)
                        {
                            long signaled = Stopwatch.GetTimestamp();
                            int settle = CoordinatorWaitPolicy.AfterSignal(wait,
                                (signaled - passStarted) * 1000d / Stopwatch.Frequency,
                                (signaled - waitStarted) * 1000d / Stopwatch.Frequency);
                            if (settle > 0)
                            { Interlocked.Increment(ref coordinatorSignalCoalesces); Thread.Sleep(settle); }
                            // Every signal consumed here happened before the
                            // next pass observes demand/content/policy state.
                            scannerWake.WaitOne(0);
                        }
                    }
                }
            }
            finally
            {
                foreach (ScanWork work in active)
                    try { work.Session.Cancel(); work.Session.Dispose(); } catch { }
            }
        }
    }
}
