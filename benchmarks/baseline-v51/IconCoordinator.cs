using System;
using System.Collections.Generic;
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
            DateTime nextPublish = DateTime.MinValue;
            try
            {
                while (!stopped)
                {
                    DateTime now = DateTime.UtcNow;
                    AppPreferences settings; int currentPolicy;
                    lock (stateGate) { settings = preferences; currentPolicy = policyGeneration; }
                    var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var foreground = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var watchRoots = new List<string>();
                    IntPtr focusedWindow = GetForegroundWindow();
                    List<SurfaceState> surfaces = CurrentSurfaces(false);
                    surfaces = surfaces.OrderBy(x => x.Explorer != null && x.Explorer.RootHandle == focusedWindow ? 0 : 1).ToList();
                    int omittedDemand = 0;
                    foreach (SurfaceState surface in surfaces)
                    {
                        if (surface.Explorer != null && VolumeRevisions.DriveIndex(surface.Explorer.Location) >= 0)
                            watchRoots.Add(surface.Explorer.Location);
                        foreach (var item in surface.Snapshot.Folders)
                        {
                            if (String.IsNullOrEmpty(item.Path)) continue;
                            if (wanted.Count >= MaximumDemand && !wanted.Contains(item.Path)) { omittedDemand++; continue; }
                            wanted.Add(item.Path); lastSeen[item.Path] = now;
                            if (!firstWanted.ContainsKey(item.Path)) firstWanted[item.Path] = now;
                            if (surface.Explorer != null && surface.Explorer.RootHandle == focusedWindow) foreground.Add(item.Path);
                            if (surface.Explorer == null)
                            {
                                string parent = Path.GetDirectoryName(item.Path);
                                watchRoots.Add(String.IsNullOrEmpty(parent) ? item.Path : parent);
                            }
                        }
                    }
                    foreach (string path in firstWanted.Keys.Where(x => !wanted.Contains(x)).ToArray()) firstWanted.Remove(path);

                    bool publish = false;
                    if (settings.Performance != appliedPerformance)
                    {
                        appliedPerformance = settings.Performance;
                        foreach (ScanWork work in active) { work.CancelRequested = true; work.Session.Cancel(); }
                        RecordEvent("Perfil de desempenho alterado; leitores serão retomados com os novos limites.");
                    }
                    if (currentPolicy != appliedPolicy)
                    {
                        appliedPolicy = currentPolicy; records.Clear(); lastSeen.Clear();
                        foreach (ScanWork work in active) { work.CancelRequested = true; work.Session.Cancel(); }
                        RecordEvent("Política de ligações alterada; resultados anteriores retirados.");
                        publish = true;
                    }
                    foreach (string path in records.Keys.ToArray())
                        if (!MediaCurrent(records[path]))
                        { records.Remove(path); lastSeen.Remove(path); if (wanted.Contains(path)) firstWanted[path] = now; publish = true; }

                    int requested = Volatile.Read(ref refreshGeneration);
                    bool manualRefresh = requested != generation;
                    if (manualRefresh) generation = requested;
                    FolderChangeBatch batch = changes.Drain();
                    // Most rounds contain no notifications. Do not allocate
                    // per-record predicates or a key snapshot for an empty batch.
                    if (manualRefresh || batch.ReconcileAll || batch.Paths.Length > 0)
                    {
                        foreach (string path in records.Keys.ToArray())
                        {
                            SizeRecord record = records[path];
                            bool dirty = manualRefresh || batch.ReconcileAll ||
                                batch.Paths.Any(x => PathRelations.Affects(path, record.Dependencies, x));
                            if (!dirty) continue;
                            SizeRecord changed = record.Invalidate(manualRefresh ? "Atualização solicitada" : "Conteúdo alterado; aguardando conferência");
                            // Small change bursts share one read. A first calculation still gets a turn.
                            if (changed.HasValue && !manualRefresh) changed.NextAttemptUtc = now.AddMilliseconds(800);
                            records[path] = changed; publish = true;
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
                        publish = true;
                    }

                    // Register dependencies as well as paths shown by the Shell. Watch creation is asynchronous.
                    foreach (string path in wanted)
                    {
                        watchRoots.Add(path);
                        SizeRecord value;
                        if (records.TryGetValue(path, out value)) watchRoots.AddRange(value.Dependencies);
                    }
                    changes.Track(paused ? (IEnumerable<string>)new string[0] : watchRoots);

                    Func<string, bool> due = delegate(string path)
                    {
                        SizeRecord record;
                        records.TryGetValue(path, out record);
                        bool monitored = SizeRefreshPolicy.NeedsCoverage(record, now, settings.ReconcileSeconds) &&
                            record.Dependencies.Length > 0 &&
                            record.Dependencies.All(x => changes.IsCovered(x));
                        return SizeRefreshPolicy.Due(record, now, monitored, settings.ReconcileSeconds);
                    };

                    if (!paused)
                    {
                        bool foregroundWaiting = foreground.Any(x => due(x) && !active.Any(w =>
                            String.Equals(w.Path, x, StringComparison.OrdinalIgnoreCase)));
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

                    var idle = records.Keys.Where(x => !wanted.Contains(x) && !active.Any(w =>
                        String.Equals(w.Path, x, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(x => lastSeen.ContainsKey(x) ? lastSeen[x] : DateTime.MinValue).ToArray();
                    foreach (string path in idle)
                    {
                        DateTime seen;
                        if (!lastSeen.TryGetValue(path, out seen)) seen = DateTime.MinValue;
                        if ((now - seen).TotalMinutes <= 10 && records.Count <= MaximumSizeRecords) continue;
                        records.Remove(path); lastSeen.Remove(path); publish = true;
                    }
                    if (publish || now >= nextPublish)
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
                        PublishSizes(records, currentPolicy); nextPublish = now.AddSeconds(1);
                    }
                    Thread.Sleep(settings.ScanInterval);
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
