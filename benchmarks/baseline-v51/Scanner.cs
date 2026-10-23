// PastaGB - metadata-only folder scanner.
// Compatible with C# 5 and .NET Framework 4.x. No files are opened for content,
// modified, deleted, or uploaded. Sizes are logical file lengths, not allocated
// disk space; hard links are counted for each directory entry encountered.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;

namespace PastaGB
{
    public sealed class ScanIssue
    {
        public string Path { get; internal set; }
        public string Message { get; internal set; }
        public bool IsSkippedLink { get; internal set; }
    }

    public sealed class FileSummary
    {
        public string Name { get; internal set; }
        public string Path { get; internal set; }
        public long Bytes { get; internal set; }
    }

    public sealed class FolderSummary
    {
        public string Name { get; internal set; }
        public string Path { get; internal set; }
        public long Bytes { get; internal set; }
        public long FileCount { get; internal set; }
        // Descendant directories, excluding this summary's own directory.
        public long FolderCount { get; internal set; }
        public long ErrorCount { get; internal set; }
        public long SkippedLinks { get; internal set; }
        public bool IsDirectFiles { get; internal set; }
        public bool HasErrors { get { return ErrorCount != 0; } }
        public bool IsPartial { get { return HasErrors || SkippedLinks != 0; } }
    }

    public sealed class ScanProgress
    {
        public string RootPath { get; internal set; }
        public string CurrentPath { get; internal set; }
        public long TotalBytes { get; internal set; }
        public long FileCount { get; internal set; }
        public long FolderCount { get; internal set; }
        public long ErrorCount { get; internal set; }
        public long SkippedLinks { get; internal set; }
        public DateTime StartedUtc { get; internal set; }
        public bool CancelRequested { get; internal set; }

        internal ScanProgress Copy()
        {
            return (ScanProgress)MemberwiseClone();
        }
    }

    public sealed class ScanResult
    {
        public string RootPath { get; internal set; }
        public long TotalBytes { get; internal set; }
        public long FileCount { get; internal set; }
        public long FolderCount { get; internal set; }
        public long ErrorCount { get; internal set; }
        public long SkippedLinks { get; internal set; }
        public long OmittedIssueCount { get; internal set; }
        public DateTime StartedUtc { get; internal set; }
        public DateTime EndedUtc { get; internal set; }
        public bool Cancelled { get; internal set; }
        // Null on a scan without a fatal/root error. Partial results may still
        // be useful when this is populated; never interpret a failed root as 0 B.
        public string FatalError { get; internal set; }
        public List<FolderSummary> Children { get; internal set; }
        // Finished, context-independent subtree totals only. Skips/errors remain
        // explicit in each summary; ancestors of a cycle/deduplicated target are
        // excluded because their totals depend on the parent traversal context.
        public List<FolderSummary> Subtotals { get; internal set; }
        // Normal local watcher paths: logical root and followed-link targets.
        // Never more than 64; a link is skipped before crossing that budget.
        public List<string> DependencyPaths { get; internal set; }
        public List<FileSummary> TopFiles { get; internal set; }
        public List<ScanIssue> Issues { get; internal set; }
        public bool IsPartial
        {
            get
            {
                return Cancelled || ErrorCount != 0 || SkippedLinks != 0 ||
                    !String.IsNullOrEmpty(FatalError);
            }
        }
    }

    public sealed class ScanOptions
    {
        // Constructor defaults enable recognized local directory links, including
        // other local volumes. Legacy Scanner.Start(path) explicitly disables
        // both flags. Options are copied and clamped before starting the thread.
        public bool FollowDirectoryLinks { get; set; }
        public bool CrossVolumeLinks { get; set; }
        public int MaxDepth { get; set; }
        public int MaxDirectories { get; set; }
        public int MaxEntries { get; set; }
        public int MaxSubtotals { get; set; }
        // Sustained metadata-entry rate per reader; bounded 100 ms burst.
        // This does not claim to limit physical disk bytes per second.
        public int MaxEntriesPerSecond { get; set; }

        public ScanOptions()
        {
            FollowDirectoryLinks = true; CrossVolumeLinks = true;
            MaxDepth = 256; MaxDirectories = 100000;
            MaxEntries = 2000000; MaxSubtotals = 2048; MaxEntriesPerSecond = 12000;
        }

        internal ScanOptions Snapshot()
        {
            return new ScanOptions
            {
                FollowDirectoryLinks = FollowDirectoryLinks,
                CrossVolumeLinks = CrossVolumeLinks,
                MaxDepth = Bound(MaxDepth, 1, 512),
                MaxDirectories = Bound(MaxDirectories, 1, 100000),
                MaxEntries = Bound(MaxEntries, 1, 2000000),
                MaxSubtotals = Bound(MaxSubtotals, 1, 4096),
                MaxEntriesPerSecond = Bound(MaxEntriesPerSecond, 100, 50000)
            };
        }

        private static int Bound(int value, int minimum, int maximum)
        { return Math.Max(minimum, Math.Min(maximum, value)); }
    }

    // Pure token budget driven by a monotonic elapsed clock. Idle time cannot
    // accumulate an unlimited burst. Waits are performed by the session in
    // cancellable slices, never inside this calculation or on the UI thread.
    internal sealed class ScanRateBudget
    {
        private readonly int entriesPerSecond, capacity;
        private double available;
        private long lastMilliseconds;

        internal ScanRateBudget(int rate, long nowMilliseconds)
        {
            entriesPerSecond = Math.Max(100, Math.Min(50000, rate));
            capacity = Math.Max(1, entriesPerSecond / 10);
            available = capacity; lastMilliseconds = nowMilliseconds;
        }

        internal bool TryTake(long nowMilliseconds, out int waitMilliseconds)
        {
            long elapsed = Math.Max(0, nowMilliseconds - lastMilliseconds);
            if (elapsed > 0)
            {
                available = Math.Min(capacity, available + elapsed * (double)entriesPerSecond / 1000.0);
                lastMilliseconds = nowMilliseconds;
            }
            if (available >= 1.0)
            {
                available -= 1.0; waitMilliseconds = 0; return true;
            }
            waitMilliseconds = Math.Max(1, (int)Math.Ceiling((1.0 - available) * 1000.0 / entriesPerSecond));
            return false;
        }
    }

    public static class Scanner
    {
        public static ScanSession Start(string path)
        {
            return Start(path, new ScanOptions { FollowDirectoryLinks = false, CrossVolumeLinks = false });
        }

        public static ScanSession Start(string path, ScanOptions options)
        {
            return new ScanSession(path, (options ?? new ScanOptions()).Snapshot(), DirectoryResolvers.Create());
        }

        // Deterministic identity / retargeting injection for the portable harness.
        // The shipping entry point always selects the native Windows resolver.
        internal static ScanSession StartForTest(string path, ScanOptions options, IDirectoryResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException("resolver");
            return new ScanSession(path, (options ?? new ScanOptions()).Snapshot(), resolver);
        }
    }

    public sealed class ScanSession
    {
        private readonly object gate = new object();
        private readonly string requestedPath;
        private readonly DateTime startedUtc;
        private readonly ScanOptions options;
        private readonly IDirectoryResolver resolver;
        private volatile bool cancelRequested;
        private bool completed;
        private ScanProgress progress;
        private ScanResult result;

        internal ScanSession(string path, ScanOptions scanOptions, IDirectoryResolver directoryResolver)
        {
            options = scanOptions; resolver = directoryResolver;
            requestedPath = path;
            startedUtc = DateTime.UtcNow;
            progress = new ScanProgress
            {
                RootPath = path ?? String.Empty,
                CurrentPath = path ?? String.Empty,
                StartedUtc = startedUtc
            };
            Thread worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "PastaGB.Scan";
            try { worker.Priority = ThreadPriority.BelowNormal; } catch (System.Security.SecurityException) { }
            worker.Start();
        }

        public bool Completed
        {
            get { lock (gate) { return completed; } }
        }

        // The result is published together with Completed, under the same lock.
        // Before completion this property returns null. Published collections
        // are no longer mutated by the worker.
        public ScanResult Result
        {
            get { lock (gate) { return result; } }
        }

        public void Cancel()
        {
            lock (gate)
            {
                if (!completed) cancelRequested = true;
            }
        }

        public ScanProgress GetProgress()
        {
            lock (gate)
            {
                ScanProgress snapshot = progress.Copy();
                snapshot.CancelRequested = cancelRequested;
                return snapshot;
            }
        }

        private void Publish(ScanProgress snapshot)
        {
            lock (gate) { progress = snapshot; }
        }

        private void Run()
        {
            WorkerState state = new WorkerState(this, requestedPath, startedUtc, options, resolver);
            try
            {
                state.Execute();
            }
            catch (Exception ex)
            {
                // Unexpected errors also become an explicit failed result.
                state.SetFatal(state.CurrentPath, "A análise foi interrompida: " + ex.Message);
            }
            finally
            {
                state.CloseAllFrames();
                ScanResult finished = state.BuildResult();
                ScanProgress lastProgress = state.BuildProgress();
                lock (gate)
                {
                    result = finished;
                    progress = lastProgress;
                    completed = true;
                }
            }
        }

        private sealed class MutableFolder
        {
            internal string Name;
            internal string Path;
            internal long Bytes, FileCount, FolderCount, ErrorCount, SkippedLinks;
            internal bool IsDirectFiles;
            internal bool Reusable = true;

            internal FolderSummary Snapshot()
            {
                return new FolderSummary
                {
                    Name = Name, Path = Path, Bytes = Bytes, FileCount = FileCount,
                    FolderCount = FolderCount, ErrorCount = ErrorCount,
                    SkippedLinks = SkippedLinks, IsDirectFiles = IsDirectFiles
                };
            }

            internal void Include(MutableFolder child)
            {
                Bytes = checked(Bytes + child.Bytes);
                FileCount = checked(FileCount + child.FileCount);
                FolderCount = checked(FolderCount + child.FolderCount);
                ErrorCount = checked(ErrorCount + child.ErrorCount);
                SkippedLinks = checked(SkippedLinks + child.SkippedLinks);
                Reusable &= child.Reusable;
            }
        }

        private sealed class DirectoryFrame
        {
            internal DirectoryInfo Directory;
            internal ResolvedDirectory Resolved;
            internal MutableFolder Summary;
            internal bool IsRoot, Initialized, Finished, PathsMatch;
            internal int Depth;
            internal IEnumerator<FileSystemInfo> Enumerator;
        }

        private sealed class LinkObservation
        {
            internal string LogicalPath;
            internal ResolvedDirectory Resolved;
        }

        private sealed class WorkerState
        {
            private const int MaxIssues = 200;
            private const int MaxTopFiles = 100;
            private const int MaxDependencies = 64;
            private readonly ScanSession owner;
            private readonly string requestedPath;
            private readonly DateTime startedUtc;
            private readonly ScanOptions options;
            private readonly IDirectoryResolver resolver;
            private readonly ScanRateBudget rateBudget;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly Stack<DirectoryFrame> frames = new Stack<DirectoryFrame>();
            private readonly List<MutableFolder> children = new List<MutableFolder>();
            private readonly List<FolderSummary> subtotals = new List<FolderSummary>();
            private readonly HashSet<string> visitedDirectories = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<string> dependencies = new List<string>();
            private readonly List<LinkObservation> followedLinks = new List<LinkObservation>();
            private readonly List<FileSummary> topHeap = new List<FileSummary>(MaxTopFiles);
            private readonly List<ScanIssue> issues = new List<ScanIssue>();
            private MutableFolder directFiles;
            private ResolvedDirectory rootIdentity;
            private long totalBytes, fileCount, folderCount, errorCount, skippedLinks, omittedIssueCount;
            private long lastPublishMs = -100;
            private int entriesSinceTimerCheck, examinedEntries, openedDirectories;
            private bool wasCancelled, workLimitReached, childrenLimitReported, subtotalLimitReported, topLimitReported;
            private int summaryTextCharacters, topTextCharacters;
            private const int MaxSummaryTextCharacters = 1048576;
            private const int MaxTopTextCharacters = 131072;
            private string fatalError;
            internal string RootPath;
            internal string CurrentPath;

            internal WorkerState(ScanSession session, string path, DateTime start, ScanOptions scanOptions, IDirectoryResolver directoryResolver)
            {
                owner = session; requestedPath = path; startedUtc = start;
                options = scanOptions; resolver = directoryResolver;
                rateBudget = new ScanRateBudget(options.MaxEntriesPerSecond, 0);
                RootPath = path ?? String.Empty; CurrentPath = RootPath;
            }

            internal void Execute()
            {
                if (ObserveCancellation()) return;
                try
                {
                    if (String.IsNullOrWhiteSpace(requestedPath)) throw new ArgumentException("Selecione uma pasta para analisar.");
                    RootPath = DirectoryResolvers.Normalize(requestedPath); CurrentPath = RootPath;
                    EnsureLocalPath(RootPath);
                    rootIdentity = resolver.Resolve(RootPath, options);
                    AddRootDependencies(rootIdentity);
                    if (rootIdentity.HasLink)
                        followedLinks.Add(new LinkObservation { LogicalPath = RootPath, Resolved = rootIdentity });
                }
                catch (Exception ex)
                {
                    SetFatal(RootPath, "Não foi possível abrir a pasta selecionada. " + ex.Message);
                    return;
                }

                directFiles = new MutableFolder { Name = "Arquivos nesta pasta", Path = RootPath, IsDirectFiles = true };
                children.Add(directFiles);
                visitedDirectories.Add(rootIdentity.Identity); openedDirectories = 1;
                frames.Push(new DirectoryFrame
                {
                    Directory = new DirectoryInfo(rootIdentity.PhysicalPath), Resolved = rootIdentity,
                    Summary = new MutableFolder { Name = new DirectoryInfo(RootPath).Name, Path = RootPath }, IsRoot = true
                });
                PublishProgress(true);
                // Explicit DFS and bounded output: no per-file materialization.
                while (frames.Count != 0 && !workLimitReached)
                {
                    if (ObserveCancellation()) break;
                    DirectoryFrame frame = frames.Peek(); CurrentPath = frame.Summary.Path;
                    if (!frame.Initialized && !OpenFrame(frame)) { PopFrame(false); continue; }
                    FileSystemInfo entry;
                    bool hasNext;
                    try
                    {
                        hasNext = frame.Enumerator.MoveNext();
                        entry = hasNext ? frame.Enumerator.Current : null;
                    }
                    catch (Exception ex)
                    {
                        if (!IsFileSystemFailure(ex)) throw;
                        RecordFrameFailure(frame, "Não foi possível continuar a leitura desta pasta. " + ex.Message);
                        PopFrame(false); continue;
                    }
                    if (!hasNext) { PopFrame(true); continue; }
                    if (examinedEntries >= options.MaxEntries)
                    {
                        StopAtLimit(frame, "O limite de itens por leitura foi atingido; o total é parcial."); break;
                    }
                    if (!WaitForEntryBudget()) break;
                    examinedEntries++;
                    ProcessEntry(frame, entry);
                    if (++entriesSinceTimerCheck >= 64)
                    {
                        entriesSinceTimerCheck = 0; PublishProgress(false);
                    }
                }
                // A newly discovered external alias may not have had a watcher
                // during this first read. Recheck all accepted links after their
                // subtrees finish, before exporting any logical-path subtotal.
                if (!wasCancelled)
                {
                    foreach (LinkObservation link in followedLinks)
                    {
                        if (ObserveCancellation()) break;
                        if (resolver.IsSame(link.Resolved, options)) continue;
                        SetFatal(link.LogicalPath, "O destino de uma ligação mudou após a leitura da subpasta. O resultado precisa ser recalculado.");
                        subtotals.Clear(); break;
                    }
                }
                // Even a root alias must still resolve to the original volume
                // and directory at publication. Late data never becomes a valid
                // zero or a reusable subtotal for a replacement root.
                if (!wasCancelled && rootIdentity != null && !resolver.IsSame(rootIdentity, options))
                {
                    SetFatal(RootPath, "A pasta ou o destino do link mudou durante a leitura. O resultado precisa ser recalculado.");
                    subtotals.Clear();
                }
            }

            private static void EnsureLocalPath(string path)
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new IOException("Esta versão analisa apenas pastas locais. Caminhos de rede e de dispositivo não são aceitos.");
                if (System.IO.Path.DirectorySeparatorChar == '\\')
                {
                    string driveRoot = System.IO.Path.GetPathRoot(path);
                    if (!String.IsNullOrEmpty(driveRoot) && new DriveInfo(driveRoot).DriveType == DriveType.Network)
                        throw new IOException("Unidades de rede mapeadas não são aceitas.");
                }
            }

            private bool OpenFrame(DirectoryFrame frame)
            {
                frame.Initialized = true; PublishProgress(false);
                try
                {
                    if (!resolver.IsSame(frame.Resolved, options))
                    {
                        RecordFrameFailure(frame, "A identidade ou o destino desta pasta mudou antes da leitura.");
                        return false;
                    }
                    // A physical path can also be the logical path for ordinary
                    // folders. Keep the comparison outside the per-file loop;
                    // aliases still retain their requested path in every result.
                    frame.PathsMatch = String.Equals(frame.Summary.Path, frame.Directory.FullName, StringComparison.Ordinal);
                    frame.Enumerator = frame.Directory.EnumerateFileSystemInfos().GetEnumerator();
                    return true;
                }
                catch (Exception ex)
                {
                    if (!IsFileSystemFailure(ex)) throw;
                    RecordFrameFailure(frame, "Não foi possível ler esta pasta. " + ex.Message); return false;
                }
            }

            private void ProcessEntry(DirectoryFrame frame, FileSystemInfo entry)
            {
                // Most files only contribute their length. Build a logical alias
                // path only for directories, diagnostics or top-file candidates.
                string logicalPath = null;
                MutableFolder child = null;
                bool directory = entry is DirectoryInfo;
                try
                {
                    FileAttributes attributes = ReadEnumeratedAttributes(entry);
                    directory = (attributes & FileAttributes.Directory) != 0;
                    if (directory)
                    {
                        logicalPath = EntryPath(frame, entry);
                        child = new MutableFolder { Name = entry.Name, Path = logicalPath };
                        if (frame.IsRoot) AddChild(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 && !options.FollowDirectoryLinks)
                        {
                            AddSkipped(logicalPath, "Link de diretório ignorado porque o acompanhamento está desativado.", child);
                            frame.Summary.Include(child); return;
                        }
                        if (frame.Depth >= options.MaxDepth)
                        {
                            child.Reusable = false;
                            AddError(logicalPath, "O limite de profundidade foi atingido; esta subpasta não foi percorrida.", child);
                            frame.Summary.Include(child); return;
                        }
                        if (openedDirectories >= options.MaxDirectories)
                        {
                            AddError(logicalPath, "O limite de diretórios por leitura foi atingido; o total é parcial.", child);
                            frame.Summary.Include(child); workLimitReached = true; return;
                        }
                        ResolvedDirectory resolved;
                        try { resolved = resolver.Resolve(entry.FullName, options); }
                        catch (DirectoryResolutionException ex)
                        {
                            if (!ex.SkippedLink) throw;
                            AddSkipped(logicalPath, ex.Message, child); frame.Summary.Include(child); return;
                        }
                        if (!options.CrossVolumeLinks && !String.Equals(resolved.VolumeIdentity, rootIdentity.VolumeIdentity, StringComparison.Ordinal))
                        {
                            AddSkipped(logicalPath, "Destino em outra unidade ignorado pelas preferências.", child);
                            frame.Summary.Include(child); return;
                        }
                        if (visitedDirectories.Contains(resolved.Identity))
                        {
                            child.Reusable = false;
                            AddSkipped(logicalPath, "Destino já contado ou ciclo detectado; esta ligação não foi somada novamente.", child);
                            frame.Summary.Include(child); return;
                        }
                        if (resolved.HasLink && followedLinks.Count >= MaxDependencies)
                        {
                            AddSkipped(logicalPath, "O limite de ligações revalidáveis foi atingido; destino ignorado.", child);
                            frame.Summary.Include(child); return;
                        }
                        if (!TryAddDependencies(resolved))
                        {
                            AddSkipped(logicalPath, "O limite de destinos acompanhados foi atingido; link ignorado para preservar a validade da soma.", child);
                            frame.Summary.Include(child); return;
                        }
                        if (resolved.HasLink)
                            followedLinks.Add(new LinkObservation { LogicalPath = logicalPath, Resolved = resolved });
                        visitedDirectories.Add(resolved.Identity); openedDirectories++;
                        folderCount = checked(folderCount + 1);
                        frame.Summary.FolderCount = checked(frame.Summary.FolderCount + 1);
                        frames.Push(new DirectoryFrame
                        {
                            Directory = new DirectoryInfo(resolved.PhysicalPath), Resolved = resolved,
                            Summary = child, Depth = frame.Depth + 1
                        });
                        return;
                    }
                    MutableFolder group = frame.IsRoot ? directFiles : frame.Summary;
                    // File links and unknown/cloud placeholder files are never
                    // opened for content or followed by the logical-length scan.
                    const int offlineOrRecall = 0x1000 | 0x40000 | 0x400000;
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || ((int)attributes & offlineOrRecall) != 0)
                    {
                        logicalPath = EntryPath(frame, entry);
                        AddSkipped(logicalPath, "Link de arquivo, conteúdo de nuvem ou offline ignorado.", group);
                        if (frame.IsRoot) frame.Summary.SkippedLinks++;
                        return;
                    }
                    FileInfo file = entry as FileInfo ?? new FileInfo(entry.FullName);
                    long bytes = file.Length;
                    if (bytes < 0) throw new IOException("O tamanho informado para o arquivo é inválido.");
                    totalBytes = checked(totalBytes + bytes); fileCount = checked(fileCount + 1);
                    frame.Summary.Bytes = checked(frame.Summary.Bytes + bytes);
                    frame.Summary.FileCount = checked(frame.Summary.FileCount + 1);
                    if (frame.IsRoot)
                    {
                        directFiles.Bytes = checked(directFiles.Bytes + bytes);
                        directFiles.FileCount = checked(directFiles.FileCount + 1);
                    }
                    ConsiderTopFile(frame, entry, bytes);
                }
                catch (Exception ex)
                {
                    if (!IsFileSystemFailure(ex)) throw;
                    if (logicalPath == null) logicalPath = EntryPath(frame, entry);
                    if (directory)
                    {
                        if (child == null)
                        {
                            child = new MutableFolder { Name = entry.Name, Path = logicalPath };
                            if (frame.IsRoot) AddChild(child);
                        }
                        AddError(logicalPath, "Não foi possível ler os metadados desta pasta. " + ex.Message, child);
                        frame.Summary.Include(child);
                    }
                    else
                    {
                        AddError(logicalPath, "Não foi possível ler os metadados deste item. " + ex.Message, frame.IsRoot ? directFiles : frame.Summary);
                        if (frame.IsRoot) frame.Summary.ErrorCount++;
                    }
                }
            }

            private void AddRootDependencies(ResolvedDirectory root)
            {
                dependencies.Add(RootPath);
                if (!TryAddDependencies(root)) throw new IOException("A raiz excede o limite de destinos acompanhados.");
            }

            private bool TryAddDependencies(ResolvedDirectory resolved)
            {
                List<string> additions = new List<string>();
                foreach (string path in resolved.Dependencies)
                    if (!ContainsDependency(dependencies, path) && !ContainsDependency(additions, path)) additions.Add(path);
                if (resolved.HasLink && !ContainsDependency(dependencies, resolved.PhysicalPath) && !ContainsDependency(additions, resolved.PhysicalPath))
                    additions.Add(resolved.PhysicalPath);
                if (dependencies.Count + additions.Count > MaxDependencies) return false;
                dependencies.AddRange(additions); return true;
            }

            private static bool ContainsDependency(List<string> values, string path)
            {
                foreach (string existing in values) if (DirectoryResolvers.PathComparer.Equals(existing, path)) return true;
                return false;
            }

            private void AddChild(MutableFolder child)
            {
                if (children.Count <= options.MaxSubtotals && ReserveSummaryText(child.Path, child.Name)) children.Add(child);
                else if (!childrenLimitReported)
                {
                    childrenLimitReported = true;
                    AddIssue(RootPath, "A lista de detalhes das subpastas foi limitada; a soma continua incluindo os diretórios percorridos.", false);
                }
            }

            private bool ObserveCancellation()
            {
                if (!owner.cancelRequested) return false;
                wasCancelled = true; return true;
            }

            private bool WaitForEntryBudget()
            {
                int wait;
                while (!rateBudget.TryTake(clock.ElapsedMilliseconds, out wait))
                {
                    if (ObserveCancellation()) return false;
                    Thread.Sleep(Math.Min(20, wait));
                }
                return !ObserveCancellation();
            }

            private static bool IsFileSystemFailure(Exception ex)
            {
                return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
                    ex is ArgumentException || ex is NotSupportedException || ex is OverflowException;
            }

            private static string EntryPath(DirectoryFrame frame, FileSystemInfo entry)
            {
                return frame.PathsMatch ? entry.FullName : System.IO.Path.Combine(frame.Summary.Path, entry.Name);
            }

            private static FileAttributes ReadEnumeratedAttributes(FileSystemInfo entry)
            {
                // EnumerateFileSystemInfos pre-populates attributes and file
                // length. Refresh would discard that snapshot and ask Windows
                // for the same metadata again for every item. Each scan opens a
                // new enumeration; identity/link validation remains separate.
                // As with any live traversal, concurrent writes are handled by
                // watcher invalidation/reconciliation, not an atomic snapshot.
                FileAttributes attributes = entry.Attributes;
                if ((int)attributes == -1) throw new IOException("O item deixou de estar disponível durante a análise.");
                return attributes;
            }

            private void StopAtLimit(DirectoryFrame frame, string message)
            { AddError(frame.Summary.Path, message, frame.Summary); workLimitReached = true; }

            private void RecordFrameFailure(DirectoryFrame frame, string message)
            {
                frame.Summary.Reusable = false;
                if (frame.IsRoot) SetFatal(frame.Summary.Path, message);
                else AddError(frame.Summary.Path, message, frame.Summary);
            }

            internal void SetFatal(string path, string message)
            {
                if (String.IsNullOrEmpty(fatalError)) fatalError = message;
                AddError(path ?? RootPath, message, null);
            }

            private void AddError(string path, string message, MutableFolder group)
            {
                errorCount++; if (group != null) group.ErrorCount++;
                AddIssue(path, message, false);
            }

            private void AddSkipped(string path, string message, MutableFolder group)
            {
                skippedLinks++; if (group != null) group.SkippedLinks++;
                AddIssue(path, message, true);
            }

            private void AddIssue(string path, string message, bool skipped)
            {
                if (issues.Count < MaxIssues) issues.Add(new ScanIssue
                {
                    Path = ShortDiagnostic(path), Message = ShortDiagnostic(message), IsSkippedLink = skipped
                });
                else omittedIssueCount++;
            }

            private static string ShortDiagnostic(string text)
            { return text == null || text.Length <= 2048 ? text : text.Substring(0, 2047) + "…"; }

            private bool ReserveSummaryText(string path, string name)
            {
                int needed = (path == null ? 0 : path.Length) + (name == null ? 0 : name.Length);
                if (needed > MaxSummaryTextCharacters - summaryTextCharacters) return false;
                summaryTextCharacters += needed; return true;
            }

            private void PopFrame(bool enumerationFinished)
            {
                DirectoryFrame frame = frames.Pop();
                bool reusable = enumerationFinished;
                if (frame.Enumerator != null)
                {
                    try { frame.Enumerator.Dispose(); }
                    catch (Exception ex)
                    {
                        if (!IsFileSystemFailure(ex)) throw;
                        RecordFrameFailure(frame, "Falha ao encerrar a leitura desta pasta. " + ex.Message); reusable = false;
                    }
                }
                if (enumerationFinished && !resolver.IsSame(frame.Resolved, options))
                {
                    RecordFrameFailure(frame, "A identidade ou o destino desta pasta mudou durante a leitura."); reusable = false;
                    // Descendant values use logical paths under this alias.
                    // If it was replaced, even previously finished descendants
                    // can no longer be published for that location.
                    subtotals.RemoveAll(delegate(FolderSummary summary)
                    { return DirectoryResolvers.IsWithin(summary.Path, frame.Summary.Path); });
                }
                frame.Finished = reusable;
                if (!reusable) frame.Summary.Reusable = false;
                if (frames.Count != 0) frames.Peek().Summary.Include(frame.Summary);
                if (!frame.IsRoot && reusable && frame.Summary.Reusable)
                {
                    if (subtotals.Count < options.MaxSubtotals && ReserveSummaryText(frame.Summary.Path, frame.Summary.Name))
                        subtotals.Add(frame.Summary.Snapshot());
                    else if (!subtotalLimitReported)
                    {
                        subtotalLimitReported = true;
                        AddIssue(RootPath, "O limite de subtotais reutilizáveis foi atingido; o total calculado permanece completo para os itens percorridos.", false);
                    }
                }
                PublishProgress(false);
            }

            internal void CloseAllFrames()
            {
                // A cancelled/interrupted subtree is never exported as complete.
                // Include its observed values in ancestors for honest Children.
                while (frames.Count != 0)
                {
                    DirectoryFrame frame = frames.Pop(); frame.Summary.Reusable = false;
                    if (frame.Enumerator != null)
                    {
                        try { frame.Enumerator.Dispose(); }
                        catch (Exception ex) { AddError(frame.Summary.Path, "Falha ao encerrar a leitura desta pasta. " + ex.Message, frame.Summary); }
                    }
                    if (frames.Count != 0) frames.Peek().Summary.Include(frame.Summary);
                }
            }

            private void PublishProgress(bool force)
            {
                long now = clock.ElapsedMilliseconds;
                if (!force && now - lastPublishMs < 100) return;
                lastPublishMs = now; owner.Publish(BuildProgress());
            }

            internal ScanProgress BuildProgress()
            {
                return new ScanProgress
                {
                    RootPath = RootPath, CurrentPath = CurrentPath, TotalBytes = totalBytes,
                    FileCount = fileCount, FolderCount = folderCount, ErrorCount = errorCount,
                    SkippedLinks = skippedLinks, StartedUtc = startedUtc, CancelRequested = owner.cancelRequested
                };
            }

            internal ScanResult BuildResult()
            {
                List<FolderSummary> summaries = new List<FolderSummary>(children.Count);
                foreach (MutableFolder folder in children) summaries.Add(folder.Snapshot());
                summaries.Sort(delegate(FolderSummary a, FolderSummary b)
                {
                    int sizeOrder = b.Bytes.CompareTo(a.Bytes);
                    return sizeOrder != 0 ? sizeOrder : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                });
                List<FileSummary> top = new List<FileSummary>(topHeap);
                top.Sort(delegate(FileSummary a, FileSummary b) { return -CompareImportance(a, b); });
                return new ScanResult
                {
                    RootPath = RootPath, TotalBytes = totalBytes, FileCount = fileCount,
                    FolderCount = folderCount, ErrorCount = errorCount, SkippedLinks = skippedLinks,
                    OmittedIssueCount = omittedIssueCount, StartedUtc = startedUtc, EndedUtc = DateTime.UtcNow,
                    Cancelled = wasCancelled, FatalError = fatalError, Children = summaries,
                    Subtotals = String.IsNullOrEmpty(fatalError) ? new List<FolderSummary>(subtotals) : new List<FolderSummary>(),
                    DependencyPaths = new List<string>(dependencies), TopFiles = top, Issues = new List<ScanIssue>(issues)
                };
            }

            // A minimum heap retains only the largest 100 files globally.
            // Equal-sized files use a stable lexical path ordering.
            private void ConsiderTopFile(DirectoryFrame frame, FileSystemInfo entry, long bytes)
            {
                // Reject ordinary small files before materializing an alias
                // path or a summary object. The heap remains bounded at 100.
                // Keep the oversized-detail diagnostic even for an otherwise
                // rejected candidate. This conservative length bound avoids
                // allocating a path for the normal case and falls through at
                // the boundary (a root may already end with a separator).
                if (topHeap.Count == MaxTopFiles && bytes < topHeap[0].Bytes &&
                    (long)frame.Summary.Path.Length + 2L * entry.Name.Length + 1 <= MaxTopTextCharacters) return;
                string path = EntryPath(frame, entry);
                string name = entry.Name;
                int textNeeded = name.Length + path.Length;
                if (textNeeded > MaxTopTextCharacters) { ReportTopLimit(); return; }
                if (topHeap.Count == MaxTopFiles)
                {
                    FileSummary minimum = topHeap[0];
                    if (bytes < minimum.Bytes) return;
                    if (bytes == minimum.Bytes && ComparePaths(path, minimum.Path) >= 0) return;
                    if (topTextCharacters - minimum.Name.Length - minimum.Path.Length + textNeeded > MaxTopTextCharacters)
                    { ReportTopLimit(); return; }
                    topTextCharacters -= minimum.Name.Length + minimum.Path.Length;
                }
                else if (topTextCharacters + textNeeded > MaxTopTextCharacters) { ReportTopLimit(); return; }
                topTextCharacters += textNeeded;
                FileSummary item = new FileSummary { Name = name, Path = path, Bytes = bytes };
                if (topHeap.Count < MaxTopFiles)
                {
                    topHeap.Add(item);
                    int at = topHeap.Count - 1;
                    while (at > 0)
                    {
                        int parent = (at - 1) / 2;
                        if (CompareImportance(topHeap[parent], item) <= 0) break;
                        topHeap[at] = topHeap[parent];
                        at = parent;
                    }
                    topHeap[at] = item;
                }
                else
                {
                    int at = 0;
                    while (true)
                    {
                        int left = at * 2 + 1;
                        if (left >= topHeap.Count) break;
                        int right = left + 1;
                        int smaller = right < topHeap.Count && CompareImportance(topHeap[right], topHeap[left]) < 0 ? right : left;
                        if (CompareImportance(item, topHeap[smaller]) <= 0) break;
                        topHeap[at] = topHeap[smaller];
                        at = smaller;
                    }
                    topHeap[at] = item;
                }
            }

            private void ReportTopLimit()
            {
                if (topLimitReported) return;
                topLimitReported = true;
                AddIssue(RootPath, "A lista de arquivos maiores foi limitada pelo orçamento de memória dos detalhes; o total não foi alterado.", false);
            }

            private static int ComparePaths(string a, string b)
            {
                int comparison = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a, b);
            }

            private static int CompareImportance(FileSummary a, FileSummary b)
            {
                int sizeOrder = a.Bytes.CompareTo(b.Bytes);
                if (sizeOrder != 0) return sizeOrder;
                int pathOrder = ComparePaths(a.Path, b.Path);
                return pathOrder > 0 ? -1 : pathOrder < 0 ? 1 : 0;
            }
        }
    }
}
