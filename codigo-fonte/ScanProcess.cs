// Supervised metadata reader. The UI/coordinator never performs pipe I/O and
// never waits for a child process. A slot is retained until its owned process
// has actually exited AND its bounded I/O threads have returned.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PastaGB
{
    public sealed class ProcessScanSession : IDisposable
    {
        private const int MaximumWorkers = 2;
        private const long MaximumWorkingSet = WorkerJob.ProcessMemoryLimitBytes;
        private static readonly object RegistryGate = new object();
        private static readonly List<ProcessScanSession> Registry = new List<ProcessScanSession>();
        private static readonly Timer Supervisor = new Timer(Supervise, null, 250, 250);
        private static int supervisorRunning, startedCount, failedCount, timeoutCount, killedCount, launchRejectedCount;
        private static int cpuLimitedCount, cpuFallbackCount, memoryLimitedCount, memoryFallbackCount;

        private readonly object gate = new object();
        private readonly Guid requestId = Guid.NewGuid();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DateTime startedUtc = DateTime.UtcNow;
        private readonly string requestedPath;
        private readonly ScanOptions options;
        private readonly int noProgressMilliseconds;
        private readonly int cpuPercent;
        private int startupMilliseconds = 15000, heartbeatMilliseconds = 15000, cancelGraceMilliseconds = 3000;
        private readonly ManualResetEvent writerWake = new ManualResetEvent(false);
        private Process process;
        private WorkerJob job;
        private bool reserved, processStarted, launchFinished, readerFinished, writerFinished = true, exitConfirmed;
        private bool cancelRequested, completed, resultReceived, released;
        private long lastHeartbeat, lastProgress, lastSequence, stopAt = -1, exitAt = -1, lastKillAt = -1, lastMemoryCheck;
        private int killAttempts;
        private ScanProgress progress;
        private ScanResult result;
        private string diagnostic = "Aguardando início do leitor.";
        private string resourceDiagnostic = "Limites nativos de CPU e memória ainda não aplicados.";
        private bool cpuLimitApplied, memoryLimitApplied;
        private bool timedOut;
#if SCAN_PROCESS_TESTS
        private string testExecutable, testArguments;
        private bool testIgnoreKill;
#endif

        private ProcessScanSession(string path, ScanOptions requestedOptions, int timeoutSeconds, int cpuLimitPercent)
        {
            requestedPath = path ?? String.Empty;
            options = ScanProtocol.CopyOptions(requestedOptions);
            noProgressMilliseconds = Math.Max(15, Math.Min(600, timeoutSeconds)) * 1000;
            cpuPercent = WorkerJob.ClampCpuPercent(cpuLimitPercent);
            progress = new ScanProgress { RootPath = requestedPath, CurrentPath = requestedPath, StartedUtc = startedUtc };
        }

        public static ProcessScanSession Start(string path, ScanOptions options)
        {
            return Start(path, options, 90);
        }

        public static ProcessScanSession Start(string path, ScanOptions options, int noProgressTimeoutSeconds)
        {
            return Start(path, options, noProgressTimeoutSeconds, 15);
        }

        public static ProcessScanSession Start(string path, ScanOptions options, int noProgressTimeoutSeconds, int cpuPercent)
        {
            ProcessScanSession session = null;
            try
            {
                if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Selecione uma pasta para analisar.");
                session = new ProcessScanSession(NormalizePath(path), options, noProgressTimeoutSeconds, cpuPercent);
                // Validate the complete bounded request before reserving a slot.
                ScanProtocol.Request(session.requestedPath, session.options);
            }
            catch (Exception ex)
            {
                if (session != null) session.writerWake.Dispose();
                session = new ProcessScanSession(path, options, noProgressTimeoutSeconds, cpuPercent);
                session.Fail("Não foi possível preparar a leitura: " + ex.Message, false);
                session.writerWake.Dispose();
                session.released = true;
                return session;
            }
            session.Begin();
            return session;
        }

        public bool Completed { get { lock (gate) { return completed; } } }
        public ScanResult Result { get { lock (gate) { return result; } } }
        public bool TerminationPending { get { lock (gate) { return completed && reserved && !released; } } }
        public string Diagnostic { get { lock (gate) { return diagnostic + " " + resourceDiagnostic; } } }
        public bool CpuLimitApplied { get { lock (gate) { return cpuLimitApplied; } } }
        public bool MemoryLimitApplied { get { lock (gate) { return memoryLimitApplied; } } }
        public static int OwnedProcessCount { get { lock (RegistryGate) { return Registry.Count; } } }
        public static int AvailableSlots { get { return MaximumWorkers - OwnedProcessCount; } }
        // Includes a reserved launch that has not returned: the budget protects
        // those bounded threads too. A normal short exit/drain is not quarantine.
        public static int QuarantinedProcessCount
        {
            get
            {
                ProcessScanSession[] sessions;
                lock (RegistryGate) { sessions = Registry.ToArray(); }
                int count = 0;
                foreach (ProcessScanSession session in sessions)
                    lock (session.gate)
                    {
                        if (!session.released && !session.exitConfirmed && session.completed && session.stopAt >= 0 &&
                            session.clock.ElapsedMilliseconds - session.stopAt >= session.cancelGraceMilliseconds + 1000) count++;
                    }
                return count;
            }
        }
        public static string DiagnosticSummary
        {
            get
            {
                return "Leitores reservados: " + OwnedProcessCount + "/" + MaximumWorkers +
                    "; iniciados: " + VolatileRead(ref startedCount) +
                    "; falhas: " + VolatileRead(ref failedCount) +
                    "; limites de tempo: " + VolatileRead(ref timeoutCount) +
                    "; encerramentos solicitados ao SO: " + VolatileRead(ref killedCount) +
                    "; pedidos adiados por limite: " + VolatileRead(ref launchRejectedCount) +
                    "; quarentena atual: " + QuarantinedProcessCount +
                    "; limites de CPU confirmados: " + VolatileRead(ref cpuLimitedCount) +
                    "; inícios sem limite nativo de CPU: " + VolatileRead(ref cpuFallbackCount) +
                    "; limites de memória confirmados: " + VolatileRead(ref memoryLimitedCount) +
                    "; inícios sem limite nativo de memória: " + VolatileRead(ref memoryFallbackCount) + ".";
            }
        }

        private static int VolatileRead(ref int value) { return Interlocked.CompareExchange(ref value, 0, 0); }

        public ScanProgress GetProgress()
        {
            lock (gate)
            {
                ScanProgress copy = progress.Copy();
                copy.CancelRequested = cancelRequested;
                return copy;
            }
        }

        // Cooperative cancellation is sent by the bounded writer thread. This
        // method never writes to a pipe, waits for a process, or kills a thread.
        public void Cancel()
        {
            lock (gate)
            {
                if (completed || released) return;
                cancelRequested = true;
                if (stopAt < 0) stopAt = clock.ElapsedMilliseconds;
                diagnostic = "Cancelamento solicitado; aguardando saída do leitor.";
                writerWake.Set();
            }
        }

        public void Dispose() { Cancel(); }

        private void Begin()
        {
            lock (RegistryGate)
            {
                if (Registry.Count < MaximumWorkers)
                {
                    Registry.Add(this);
                    reserved = true;
                }
            }
            if (!reserved)
            {
                Interlocked.Increment(ref launchRejectedCount);
                Fail("Os leitores anteriores ainda estão encerrando. A leitura poderá ser tentada novamente.", false);
                writerWake.Dispose();
                released = true;
                return;
            }
            try
            {
                Thread reader = new Thread(LaunchAndRead) { IsBackground = true, Name = "PastaGB.ScanPipe" };
                reader.Start();
            }
            catch (Exception ex)
            {
                Fail("Não foi possível iniciar o leitor: " + ex.Message, false);
                lock (gate) { launchFinished = true; readerFinished = true; }
            }
        }

        private ProcessStartInfo StartInfo()
        {
            // The executable comes only from this running application. The
            // user-selected folder and all options travel through stdin.
            string executable = typeof(ScanWorker).Assembly.Location;
            string arguments = "--scan-worker";
#if SCAN_PROCESS_TESTS
            if (testExecutable != null) { executable = testExecutable; arguments = testArguments; }
#endif
            return new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
        }

        private void LaunchAndRead()
        {
            Stream output = null;
            try
            {
                Process owned = new Process { StartInfo = StartInfo() };
                lock (gate) { process = owned; }
                if (!owned.Start()) throw new IOException("O processo auxiliar não iniciou.");
                lock (gate)
                {
                    processStarted = true;
                    lastHeartbeat = lastProgress = clock.ElapsedMilliseconds;
                    diagnostic = "Leitor iniciado; aguardando a primeira medição.";
                }
                Interlocked.Increment(ref startedCount);
                try { owned.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                string limitDescription;
                job = WorkerJob.TryAttach(owned, cpuPercent, out limitDescription);
                lock (gate)
                {
                    cpuLimitApplied = job != null && job.CpuLimitApplied;
                    memoryLimitApplied = job != null && job.MemoryLimitApplied;
                    resourceDiagnostic = limitDescription;
                }
                if (cpuLimitApplied) Interlocked.Increment(ref cpuLimitedCount);
                else Interlocked.Increment(ref cpuFallbackCount);
                if (memoryLimitApplied) Interlocked.Increment(ref memoryLimitedCount);
                else Interlocked.Increment(ref memoryFallbackCount);
                output = owned.StandardOutput.BaseStream;
                Stream input = owned.StandardInput.BaseStream;
                lock (gate) { writerFinished = false; }
                try
                {
                    Thread writer = new Thread(delegate() { WriteCommands(input); })
                    {
                        IsBackground = true, Name = "PastaGB.ScanCommands"
                    };
                    writer.Start();
                }
                catch
                {
                    lock (gate) { writerFinished = true; }
                    try { input.Dispose(); } catch { }
                    throw;
                }
                while (true)
                {
                    ScanFrame frame = ScanProtocol.ReadFrame(output);
                    if (frame == null)
                    {
                        Fail("O processo de leitura encerrou antes de entregar o resultado.", false);
                        return;
                    }
                    if (frame.RequestId != requestId) throw new InvalidDataException("Resposta pertence a outro pedido.");
                    if (frame.Kind == ScanFrameKind.Progress)
                    {
                        long sequence;
                        ScanProgress update = ScanProtocol.ReadProgress(frame.Payload, out sequence);
                        CheckRoot(update.RootPath);
                        lock (gate)
                        {
                            if (sequence <= lastSequence) throw new InvalidDataException("Resposta repetida ou fora de ordem.");
                            lastSequence = sequence;
                            long now = clock.ElapsedMilliseconds;
                            lastHeartbeat = now;
                            if (MadeProgress(progress, update)) lastProgress = now;
                            if (!completed) { progress = update; diagnostic = "Leitura em andamento."; }
                        }
                    }
                    else if (frame.Kind == ScanFrameKind.Result)
                    {
                        bool detailsOmitted;
                        ScanResult finished = ScanProtocol.ReadResult(frame.Payload, out detailsOmitted);
                        CheckRoot(finished.RootPath);
                        ValidateResultPaths(finished);
                        Complete(finished, detailsOmitted);
                        return;
                    }
                    else if (frame.Kind == ScanFrameKind.Fault)
                    {
                        Fail("Falha no leitor: " + ScanProtocol.ReadFault(frame.Payload), false);
                        return;
                    }
                    else throw new InvalidDataException("Tipo de resposta inesperado.");
                }
            }
            catch (Exception ex)
            {
                Fail("A comunicação com o leitor foi interrompida: " + ScanProtocol.ShortMessage(ex.Message), false);
            }
            finally
            {
                if (output != null) try { output.Dispose(); } catch { }
                lock (gate) { launchFinished = true; readerFinished = true; }
            }
        }

        private void WriteCommands(Stream input)
        {
            try
            {
                ScanProtocol.WriteFrame(input, ScanFrameKind.Request, requestId, ScanProtocol.Request(requestedPath, options));
                writerWake.WaitOne();
                bool sendCancel;
                lock (gate) { sendCancel = cancelRequested && !resultReceived; }
                if (sendCancel) ScanProtocol.WriteFrame(input, ScanFrameKind.Cancel, requestId, new byte[0]);
            }
            catch (Exception ex)
            {
                Fail("Não foi possível comunicar o pedido ao leitor: " + ScanProtocol.ShortMessage(ex.Message), false);
            }
            finally
            {
                try { input.Dispose(); } catch { }
                lock (gate) { writerFinished = true; }
            }
        }

        private void CheckRoot(string root)
        {
            if (!String.Equals(root, requestedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A resposta não corresponde à pasta solicitada.");
        }

        private static string NormalizePath(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        private void ValidateResultPaths(ScanResult value)
        {
            string prefix = requestedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (FolderSummary summary in value.Subtotals)
            {
                if (!String.Equals(summary.Path, requestedPath, StringComparison.OrdinalIgnoreCase) &&
                    !summary.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Subtotal fora da pasta solicitada.");
                if (!String.Equals(Path.GetFullPath(summary.Path), summary.Path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Caminho de subtotal inválido.");
            }
            foreach (string dependency in value.DependencyPaths)
                if (!Path.IsPathRooted(dependency) || dependency.IndexOf('\0') >= 0 ||
                    !String.Equals(NormalizePath(dependency), dependency, StringComparison.OrdinalIgnoreCase) ||
                    (Path.DirectorySeparatorChar == '\\' && (dependency.Length < 3 || dependency[1] != ':' || dependency[2] != '\\')))
                    throw new InvalidDataException("Dependência de leitura inválida.");
        }

        private static bool MadeProgress(ScanProgress before, ScanProgress after)
        {
            return before.TotalBytes != after.TotalBytes || before.FileCount != after.FileCount ||
                before.FolderCount != after.FolderCount || before.ErrorCount != after.ErrorCount ||
                before.SkippedLinks != after.SkippedLinks ||
                !String.Equals(before.CurrentPath, after.CurrentPath, StringComparison.Ordinal);
        }

        private void Complete(ScanResult finished, bool detailsOmitted)
        {
            lock (gate)
            {
                if (completed) return;
                // A late result after cancellation remains cancelled; the caller
                // must not accidentally publish it for a superseded request.
                if (cancelRequested) finished.Cancelled = true;
                result = finished;
                resultReceived = true;
                progress = ProgressFromResult(finished);
                completed = true;
                if (finished.Cancelled) diagnostic = "Leitura cancelada.";
                else if (!String.IsNullOrEmpty(finished.FatalError))
                {
                    diagnostic = "Leitura indisponível: " + ScanProtocol.ShortMessage(finished.FatalError);
                    Interlocked.Increment(ref failedCount);
                }
                else diagnostic = finished.IsPartial ? "Leitura parcial concluída." : "Leitura concluída.";
                if (detailsOmitted) diagnostic += " Detalhes limitados pelo orçamento de comunicação.";
                if (stopAt < 0) stopAt = clock.ElapsedMilliseconds;
                writerWake.Set();
            }
        }

        private void Fail(string message, bool timeout)
        {
            lock (gate)
            {
                if (completed) return;
                result = FailureResult(progress, ScanProtocol.ShortMessage(message), cancelRequested);
                completed = true;
                timedOut = timeout;
                diagnostic = result.FatalError;
                cancelRequested = true;
                if (stopAt < 0) stopAt = clock.ElapsedMilliseconds;
                writerWake.Set();
            }
            Interlocked.Increment(ref failedCount);
            if (timeout) Interlocked.Increment(ref timeoutCount);
        }

        internal static ScanResult FailureResult(ScanProgress value, string message, bool cancelled)
        {
            return new ScanResult
            {
                RootPath = value.RootPath,
                TotalBytes = value.TotalBytes,
                FileCount = value.FileCount,
                FolderCount = value.FolderCount,
                ErrorCount = value.ErrorCount,
                SkippedLinks = value.SkippedLinks,
                StartedUtc = value.StartedUtc,
                EndedUtc = DateTime.UtcNow,
                Cancelled = cancelled,
                FatalError = message,
                Children = new List<FolderSummary>(),
                TopFiles = new List<FileSummary>(),
                Issues = new List<ScanIssue>(),
                Subtotals = new List<FolderSummary>(),
                DependencyPaths = new List<string>()
            };
        }

        internal static ScanProgress ProgressFromResult(ScanResult value)
        {
            return new ScanProgress
            {
                RootPath = value.RootPath, CurrentPath = value.RootPath,
                TotalBytes = value.TotalBytes, FileCount = value.FileCount, FolderCount = value.FolderCount,
                ErrorCount = value.ErrorCount, SkippedLinks = value.SkippedLinks,
                StartedUtc = value.StartedUtc, CancelRequested = value.Cancelled
            };
        }

        private static void Supervise(object unused)
        {
            if (Interlocked.Exchange(ref supervisorRunning, 1) != 0) return;
            try
            {
                ProcessScanSession[] sessions;
                lock (RegistryGate) { sessions = Registry.ToArray(); }
                foreach (ProcessScanSession session in sessions)
                {
                    try { session.Tick(); }
                    catch (Exception ex) { session.Fail("Falha na supervisão do leitor: " + ex.Message, false); }
                }
            }
            finally { Interlocked.Exchange(ref supervisorRunning, 0); }
            GC.KeepAlive(Supervisor);
        }

        private void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            int inactivityLimit = noProgressMilliseconds;
#if SCAN_PROCESS_TESTS
            if (testNoProgressMilliseconds > 0) inactivityLimit = testNoProgressMilliseconds;
#endif
            Process owned;
            bool started, endedLaunch, readDone, writeDone, done, stopping;
            long heartbeat, advanced, stop, sequence;
            lock (gate)
            {
                if (released) return;
                owned = process; started = processStarted; endedLaunch = launchFinished;
                readDone = readerFinished; writeDone = writerFinished;
                done = completed; stopping = cancelRequested;
                heartbeat = lastHeartbeat; advanced = lastProgress; stop = stopAt; sequence = lastSequence;
            }

            bool exited = false;
            if (started && owned != null)
            {
                try { exited = owned.HasExited; }
                catch (InvalidOperationException) { /* Retain the slot if exit cannot be confirmed. */ }
                if (exited)
                {
                    lock (gate)
                    {
                        exitConfirmed = true;
                        if (exitAt < 0) exitAt = now;
                        writerWake.Set();
                    }
                    // A process can exit while its valid final frame is still
                    // being parsed. Give the bounded reader time to drain it.
                    if (!done && (readDone || now - exitAt > 5000))
                        Fail("O processo de leitura encerrou sem um resultado válido.", false);
                }
            }

            if (!done && !exited)
            {
                if (stopping && stop >= 0 && now - stop >= cancelGraceMilliseconds)
                    Fail("Leitura cancelada antes da conclusão.", false);
                else if (!stopping)
                {
                    if ((!started || sequence == 0) && now >= startupMilliseconds)
                        Fail("O leitor não iniciou a medição dentro do limite de tempo.", true);
                    else if (started && sequence != 0 && now - heartbeat >= heartbeatMilliseconds)
                        Fail("O leitor deixou de responder. A leitura poderá ser repetida.", true);
                    else if (started && now - advanced >= inactivityLimit)
                        Fail("A leitura ficou sem avanço além do limite de tempo. A pasta ou unidade pode estar indisponível.", true);
                    else if (started && now - lastMemoryCheck >= 1000)
                    {
                        lastMemoryCheck = now;
                        try
                        {
                            owned.Refresh();
                            if (owned.WorkingSet64 > MaximumWorkingSet)
                                Fail("O leitor atingiu o limite de memória. A leitura foi interrompida.", false);
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                }
            }

            lock (gate) { stop = stopAt; }
            if (started && !exited && stop >= 0 && now - stop >= cancelGraceMilliseconds &&
                killAttempts < 3 && (lastKillAt < 0 || now - lastKillAt >= 1000))
            {
                lastKillAt = now;
                killAttempts++;
                try
                {
#if SCAN_PROCESS_TESTS
                    if (testIgnoreKill) throw new IOException("Teste: encerramento ainda não confirmado.");
#endif
                    // 'owned' is the exact Process instance created by this
                    // session. Never enumerate/kill by name, PID or user input.
                    owned.Kill();
                    Interlocked.Increment(ref killedCount);
                    lock (gate) { diagnostic = (timedOut ? "Limite de tempo; " : "") + "encerramento solicitado ao SO; aguardando confirmação."; }
                }
                catch (Exception ex)
                {
                    lock (gate) { diagnostic = "Leitor retido no orçamento até confirmar saída: " + ScanProtocol.ShortMessage(ex.Message); }
                }
            }

            lock (gate)
            {
                readDone = readerFinished; writeDone = writerFinished; endedLaunch = launchFinished;
                exited = exitConfirmed; started = processStarted; done = completed;
            }
            // A failed Process.Start owns no running process. Otherwise even a
            // failed Kill must keep this session in the two-worker budget.
            if (endedLaunch && readDone && writeDone && (exited || !started))
            {
                if (!done) Fail("O leitor encerrou sem um resultado válido.", false);
                Release();
            }
        }

        private void Release()
        {
            Process owned;
            lock (gate)
            {
                if (released) return;
                released = true;
                owned = process;
                if (String.IsNullOrEmpty(diagnostic)) diagnostic = "Leitor encerrado.";
                else diagnostic += " Saída confirmada; recursos liberados.";
                writerWake.Dispose();
            }
            try
            {
                if (job != null) job.Dispose();
                if (owned != null) owned.Dispose();
            }
            catch (Exception ex)
            {
                lock (gate) { diagnostic += " Falha ao liberar objeto já encerrado: " + ScanProtocol.ShortMessage(ex.Message); }
            }
            finally { lock (RegistryGate) { Registry.Remove(this); } }
        }

#if SCAN_PROCESS_TESTS
        // Compiled only into the platform-independent harness. The production
        // assembly has no alternate-executable or kill-suppression interface.
        internal static ProcessScanSession StartForTest(string path, ScanOptions options, string executable,
            string arguments, int noProgressMs, int startupMs, int heartbeatMs, int cancelGraceMs, bool ignoreKill)
        {
            ProcessScanSession session = new ProcessScanSession(NormalizePath(path), options, 90, 15)
            {
                testExecutable = executable, testArguments = arguments, testIgnoreKill = ignoreKill,
                startupMilliseconds = startupMs, heartbeatMilliseconds = heartbeatMs,
                cancelGraceMilliseconds = cancelGraceMs
            };
            // readonly assignment is not available outside the constructor; the
            // test-only field supplies a shortened monotonic deadline instead.
            session.testNoProgressMilliseconds = noProgressMs;
            session.Begin();
            return session;
        }
        private int testNoProgressMilliseconds;
        internal bool HasExitedForTest { get { lock (gate) { return exitConfirmed; } } }
#endif
    }

    public static class ScanWorker
    {
        // Call before the single-instance mutex, WinForms initialization and
        // Shell discovery. This private mode only reads one bounded request.
        public static bool TryRun(string[] args)
        {
            if (args == null || args.Length == 0 || !String.Equals(args[0], "--scan-worker", StringComparison.Ordinal)) return false;
            if (args.Length != 1) { Environment.ExitCode = 64; return true; }
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                try { SetErrorMode(0x0001 | 0x0002 | 0x8000); } catch { }
            try { Run(Console.OpenStandardInput(), Console.OpenStandardOutput()); }
            catch { Environment.ExitCode = 70; }
            return true;
        }

        private static void Run(Stream input, Stream output)
        {
            Guid id = Guid.Empty;
            try
            {
                ScanFrame first = ScanProtocol.ReadFrame(input);
                if (first == null) return;
                id = first.RequestId;
                if (first.Kind != ScanFrameKind.Request) throw new InvalidDataException("Pedido inicial inválido.");
                ScanRequest request = ScanProtocol.ReadRequest(first.Payload);
                ScanSession scanner = Scanner.Start(request.Path, request.Options);
                object gate = new object();
                bool cancelled = false;
                string commandFailure = null;
                Thread commands = new Thread(delegate()
                {
                    try
                    {
                        ScanFrame command = ScanProtocol.ReadFrame(input);
                        if (command != null && (command.Kind != ScanFrameKind.Cancel || command.RequestId != id || command.Payload.Length != 0))
                            throw new InvalidDataException("Comando inválido para o leitor.");
                        lock (gate)
                        {
                            cancelled = true;
                            if (command == null) commandFailure = "A conexão com o aplicativo foi encerrada.";
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (gate) { cancelled = true; commandFailure = ScanProtocol.ShortMessage(ex.Message); }
                    }
                    scanner.Cancel();
                }) { IsBackground = true, Name = "PastaGB.WorkerCommands" };
                commands.Start();
                Stopwatch clock = Stopwatch.StartNew();
                long cancelAt = -1, sequence = 0;
                while (true)
                {
                    bool stopping;
                    string error;
                    lock (gate) { stopping = cancelled; error = commandFailure; }
                    if (stopping)
                    {
                        scanner.Cancel();
                        if (cancelAt < 0) cancelAt = clock.ElapsedMilliseconds;
                    }
                    if (scanner.Completed)
                    {
                        ScanResult result = scanner.Result;
                        if (stopping) result.Cancelled = true;
                        ScanProtocol.WriteFrame(output, ScanFrameKind.Result, id, ScanProtocol.Result(result));
                        return;
                    }
                    ScanProgress progress = scanner.GetProgress();
                    ScanProtocol.WriteFrame(output, ScanFrameKind.Progress, id, ScanProtocol.Progress(++sequence, progress));
                    if (stopping && clock.ElapsedMilliseconds - cancelAt >= 2500)
                    {
                        ScanResult result = ProcessScanSession.FailureResult(progress,
                            error ?? "Leitura cancelada antes da conclusão.", true);
                        ScanProtocol.WriteFrame(output, ScanFrameKind.Result, id, ScanProtocol.Result(result));
                        return; // ScanSession is background; no UI/mutex was started.
                    }
                    Thread.Sleep(200);
                }
            }
            catch (Exception ex)
            {
                if (id != Guid.Empty)
                    try { ScanProtocol.WriteFrame(output, ScanFrameKind.Fault, id, ScanProtocol.Fault(ex.Message)); } catch { }
                Environment.ExitCode = 70;
            }
        }

        [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    }

    // Kill-on-close contains child lifetime if the parent is terminated. If the
    // current Windows job policy rejects assignment, stdin EOF still requests
    // bounded cooperative shutdown; failures never broaden process ownership.
    internal sealed class WorkerJob : IDisposable
    {
        internal const long ProcessMemoryLimitBytes = 384L * 1024 * 1024;
        private const string MemoryFallback = " Limite nativo de memória não confirmado; guarda reativa de memória de 384 MiB ativa.";
        private IntPtr handle;
        internal readonly bool CpuLimitApplied, MemoryLimitApplied;
        private WorkerJob(IntPtr value, bool cpuApplied, bool memoryApplied)
        {
            handle = value; CpuLimitApplied = cpuApplied; MemoryLimitApplied = memoryApplied;
        }
        internal static int ClampCpuPercent(int percent) { return Math.Max(1, Math.Min(100, percent)); }

        // Documented ABI: DWORD flags + DWORD CpuRate union, no pointers. Rate
        // uses percentage*100 (15% = 1500); ENABLE|HARD_CAP is 0x1|0x4.
        // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information
        [StructLayout(LayoutKind.Sequential)] internal struct CpuRateLimit
        {
            internal uint ControlFlags;
            internal uint CpuRate;
        }
        internal static CpuRateLimit CreateCpuLimit(int percent)
        {
            return new CpuRateLimit { ControlFlags = 0x00000005, CpuRate = (uint)(ClampCpuPercent(percent) * 100) };
        }

        // PROCESS_MEMORY limits committed virtual memory per reader. It is
        // distinct from the parent's reactive WorkingSet check and never caps
        // the application's UI process or the computer's total memory.
        // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information
        // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information
        internal static ExtendedLimit CreateExtendedLimit()
        {
            ExtendedLimit info = new ExtendedLimit();
            info.Basic.LimitFlags = 0x00002000 | 0x00000100; // KILL_ON_JOB_CLOSE | PROCESS_MEMORY
            info.ProcessMemoryLimit = new UIntPtr((ulong)ProcessMemoryLimitBytes);
            return info;
        }

        internal static bool MemoryLimitMatches(ExtendedLimit actual, uint returned)
        {
            return returned >= Marshal.SizeOf(typeof(ExtendedLimit)) &&
                (actual.Basic.LimitFlags & 0x00000100) != 0 &&
                actual.ProcessMemoryLimit.ToUInt64() == (ulong)ProcessMemoryLimitBytes;
        }

        internal static WorkerJob TryAttach(Process process, int cpuPercent, out string diagnostic)
        {
            diagnostic = "Controle nativo de CPU indisponível neste sistema; prioridade reduzida." + MemoryFallback;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            IntPtr value = IntPtr.Zero;
            try
            {
                value = CreateJobObject(IntPtr.Zero, null);
                if (value == IntPtr.Zero)
                {
                    diagnostic = "Job do leitor não disponível (erro " + Marshal.GetLastWin32Error() + "); prioridade reduzida, sem limite nativo de CPU." + MemoryFallback;
                    return null;
                }
                ExtendedLimit info = CreateExtendedLimit();
                uint extendedBytes = (uint)Marshal.SizeOf(typeof(ExtendedLimit));
                if (!SetInformationJobObject(value, 9, ref info, extendedBytes))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseHandle(value);
                    diagnostic = "Proteção do job recusada (erro " + error + "); prioridade reduzida, sem limite nativo de CPU." + MemoryFallback;
                    return null;
                }
                CpuRateLimit cpu = CreateCpuLimit(cpuPercent);
                uint cpuBytes = (uint)Marshal.SizeOf(typeof(CpuRateLimit));
                bool configured = SetInformationJobObjectCpu(value, 15, ref cpu, cpuBytes);
                int cpuError = configured ? 0 : Marshal.GetLastWin32Error();
                if (!AssignProcessToJobObject(value, process.Handle))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseHandle(value);
                    diagnostic = "Associação ao job recusada (erro " + error + "); prioridade reduzida, sem limite nativo de CPU." + MemoryFallback;
                    return null;
                }
                ExtendedLimit actualMemory = new ExtendedLimit();
                uint memoryReturned;
                bool memoryConfirmed = QueryInformationJobObjectExtended(value, 9, ref actualMemory, extendedBytes, out memoryReturned) &&
                    MemoryLimitMatches(actualMemory, memoryReturned);
                CpuRateLimit actual = new CpuRateLimit();
                uint returned;
                bool confirmed = configured && QueryInformationJobObjectCpu(value, 15, ref actual, cpuBytes, out returned) &&
                    returned >= cpuBytes && (actual.ControlFlags & 0x00000005) == 0x00000005 && actual.CpuRate == cpu.CpuRate;
                diagnostic = confirmed ? "Limite nativo de CPU confirmado: " + ClampCpuPercent(cpuPercent) + "% por leitor." :
                    (configured ? "Windows não confirmou a configuração de CPU; prioridade reduzida." :
                    "Limite nativo de CPU não disponível (erro " + cpuError + "); prioridade reduzida.");
                diagnostic += memoryConfirmed ? " Limite nativo de memória confirmado: 384 MiB de memória comprometida por leitor." : MemoryFallback;
                return new WorkerJob(value, confirmed, memoryConfirmed);
            }
            catch (Exception ex)
            {
                if (value != IntPtr.Zero) CloseHandle(value);
                diagnostic = "Limite nativo de CPU não aplicado: " + ScanProtocol.ShortMessage(ex.Message) + "; prioridade reduzida." + MemoryFallback;
                return null;
            }
        }
        public void Dispose()
        {
            IntPtr value = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (value != IntPtr.Zero) CloseHandle(value);
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimit
        {
            internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
        {
            internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimit
        {
            internal BasicLimit Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimit info, uint length);
        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObjectCpu(IntPtr job, int type, ref CpuRateLimit info, uint length);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObjectCpu(IntPtr job, int type, ref CpuRateLimit info, uint length, out uint returned);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObjectExtended(IntPtr job, int type, ref ExtendedLimit info, uint length, out uint returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr value);
    }
}
