// Windows-only regression against the ACTUAL delivered executable assembly.
// No scanner sources, reflection overrides or alternate executable seams are
// compiled here. The production assembly starts its own EXE as each child.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using PastaGB;

internal static class ScanWorkerWindowsTests
{
    private static readonly List<object> Checks = new List<object>();
    private static readonly List<object> CpuChecks = new List<object>();
    private static readonly List<object> MemoryChecks = new List<object>();
    private static readonly List<ProcessScanSession> Sessions = new List<ProcessScanSession>();
    private static int completedWorkers;
    private static bool workerExecutionVerified;
    private static string app, output, fixture;

    // Deliberately does not handle --scan-worker. An old implementation that
    // launches the entry harness instead of the delivered EXE fails the test.
    private static int Main(string[] args)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || args.Length != 2)
        {
            Console.Error.WriteLine("NOT RUN: use Run-WorkerRegression.ps1 em Windows com .NET Framework 4.8.");
            return 77;
        }
        app = Path.GetFullPath(args[0]); output = Path.GetFullPath(args[1]);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(output, "PASTAGB-WORKER-TEST-OWNED.txt")))
        {
            Console.Error.WriteLine("O roteiro exige sua pasta temporária exclusiva e identificada."); return 2;
        }
        fixture = Path.Combine(output, "cenario-worker-" + Guid.NewGuid().ToString("N"));
        string failure = null, cleanup = null;
        string loadedAssembly = typeof(ProcessScanSession).Assembly.Location;
        DateTime startedUtc = DateTime.UtcNow;
        try
        {
            Check(SamePath(app, loadedAssembly) && Hash(app) == Hash(loadedAssembly),
                "Assembly entregue", "A API testada vem da cópia verificada do executável selecionado.");
            Check(ProcessScanSession.OwnedProcessCount == 0 && ProcessScanSession.AvailableSlots == 2,
                "Orçamento inicial", ProcessScanSession.DiagnosticSummary);
            Directory.CreateDirectory(fixture);
            TestKnownTree();
            TestCancellation();
            TestReuseAfterCancellation();
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Checks.Add(new { name = "Execução do roteiro", passed = false, detail = exception.Message });
        }
        finally
        {
            foreach (ProcessScanSession session in Sessions)
                try { session.Cancel(); session.Dispose(); } catch { }
            try { AwaitRelease(10000); }
            catch (Exception exception)
            {
                if (failure == null) failure = exception.ToString();
                Checks.Add(new { name = "Saída final dos leitores", passed = false, detail = exception.Message });
            }
            // Never delete while a child still owns the fixture. The unique
            // directory is retained as evidence if exit cannot be confirmed.
            if (ProcessScanSession.OwnedProcessCount == 0)
                try { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
                catch (Exception exception) { cleanup = exception.Message; }
            else cleanup = "Cenário preservado: existe leitor com saída ainda não confirmada.";

            var report = new
            {
                started_utc = startedUtc.ToString("o"), finished_utc = DateTime.UtcNow.ToString("o"),
                delivered_executable = app, delivered_sha256 = Hash(app),
                referenced_assembly = loadedAssembly, referenced_sha256 = Hash(loadedAssembly),
                file_version = FileVersionInfo.GetVersionInfo(app).FileVersion,
                assembly_version = typeof(ProcessScanSession).Assembly.GetName().Version.ToString(),
                windows_version = Environment.OSVersion.ToString(), process_bits = IntPtr.Size * 8,
                clr = Environment.Version.ToString(), windows_native_execution = true,
                windows_ui_executed = false, automated_passed = failure == null,
                terminal_sessions = completedWorkers, worker_execution_verified = workerExecutionVerified,
                checks = Checks, cpu_configuration_checks = CpuChecks,
                memory_configuration_checks = MemoryChecks,
                cpu_enforcement_benchmark_executed = false,
                memory_pressure_test_executed = false,
                final_reserved_slots = ProcessScanSession.OwnedProcessCount,
                final_quarantined_slots = ProcessScanSession.QuarantinedProcessCount,
                supervisor = ProcessScanSession.DiagnosticSummary, fixture = fixture,
                cleanup_warning = cleanup, error = failure,
                scope = "Executável final: metadados Win32, subprocessos, protocolo, configuração nativa de CPU e memória ou fallback explícito, cancelamento e saída. Não valida aparência, DPI, USB, ACL, junções, corrupção de driver, taxa real de CPU sob carga ou pressão de memória."
            };
            WriteNewText(Path.Combine(output, "resultado-worker-windows.json"), new JavaScriptSerializer().Serialize(report));
        }
        Console.WriteLine(failure == null ? "PASS: leitor do executável entregue validado nos cenários nativos registrados." : "FAIL: " + failure);
        Console.WriteLine("Evidências: " + output);
        Console.WriteLine("WINDOWS_UI_EXECUTED=false");
        return failure == null ? 0 : 1;
    }

    private static void TestKnownTree()
    {
        string root = Path.Combine(fixture, "Pasta ç [literal] $(sem-comando)");
        string child = Path.Combine(root, "Subpasta");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(Path.Combine(root, "Vazia"));
        CreateFile(Path.Combine(root, "direto.bin"), 12345);
        CreateFile(Path.Combine(root, "zero.bin"), 0);
        CreateFile(Path.Combine(child, "conteudo.bin"), 54321);
        ProcessScanSession session = Start(root + Path.DirectorySeparatorChar, 3000, 15);
        ScanResult result = Finish(session, 45000);
        Check(result.FatalError == null && !result.Cancelled && !result.IsPartial,
            "Leitura nativa concluída", session.Diagnostic);
        Check(result.TotalBytes == 66666 && result.FileCount == 3 && result.FolderCount == 2,
            "Totais conhecidos", "Esperado: 66.666 bytes, 3 arquivos e 2 subpastas; obtido: " + result.TotalBytes + ", " + result.FileCount + ", " + result.FolderCount + ".");
        Check(SamePath(result.RootPath, root), "Identidade da raiz", result.RootPath);
        Check(result.DependencyPaths != null && result.DependencyPaths.Any(x => SamePath(x, root)) &&
            result.DependencyPaths.All(Path.IsPathRooted), "Dependências preservadas", String.Join(" | ", result.DependencyPaths ?? new List<string>()));
        Check(result.Subtotals != null && result.Subtotals.Any(x => SamePath(x.Path, child) && x.Bytes == 54321),
            "Subtotal concluído", "Subpasta deve retornar 54.321 bytes pelo protocolo do executável final.");
        workerExecutionVerified = true;
        Check(session.GetProgress().TotalBytes == result.TotalBytes, "Progresso final consistente", "O progresso publicado corresponde ao resultado terminal.");
        RecordCpu(session, 15, "Equilibrado");
        session.Cancel();
        Check(!session.Result.Cancelled, "Cancelamento após conclusão", "Não altera um resultado já concluído.");
        AwaitRelease(10000);
        Check(!session.TerminationPending, "Saída real confirmada", ProcessScanSession.DiagnosticSummary);
    }

    private static void TestCancellation()
    {
        string root = Path.Combine(fixture, "Cancelar");
        Directory.CreateDirectory(root);
        // Minimum supported enumeration budget keeps this small fixture active
        // long enough to cancel AFTER real metadata progress has been observed.
        for (int index = 0; index < 1024; index++)
            CreateFile(Path.Combine(root, index.ToString("D4") + ".bin"), 1);
        ProcessScanSession session = Start(root, 100, 10);
        Stopwatch wait = Stopwatch.StartNew();
        while (!session.Completed && session.GetProgress().FileCount < 20 && wait.ElapsedMilliseconds < 20000) Thread.Sleep(15);
        Check(!session.Completed && session.GetProgress().FileCount >= 20,
            "Leitor ativo antes do cancelamento", session.Diagnostic);
        RecordCpu(session, 10, "Econômico");
        Stopwatch call = Stopwatch.StartNew();
        session.Cancel();
        long cancelCallMilliseconds = call.ElapsedMilliseconds;
        Check(cancelCallMilliseconds < 500, "Cancelamento sem espera de processo no chamador", "Chamada retornou em " + cancelCallMilliseconds + " ms.");
        ScanResult result = Finish(session, 15000);
        Check(result.Cancelled && result.IsPartial, "Cancelamento terminal explícito", session.Diagnostic);
        Check(result.TotalBytes < 1024 && result.FileCount < 1024,
            "Resultado interrompido identificado", "O leitor devolveu contagem parcial da operação cancelada.");
        AwaitRelease(10000);
        Check(!session.TerminationPending && ProcessScanSession.QuarantinedProcessCount == 0,
            "Orçamento liberado após saída", ProcessScanSession.DiagnosticSummary);
    }

    private static void TestReuseAfterCancellation()
    {
        string root = Path.Combine(fixture, "Vazia-apos-cancelamento");
        Directory.CreateDirectory(root);
        ProcessScanSession session = Start(root, 3000, 10);
        ScanResult result = Finish(session, 45000);
        Check(result.FatalError == null && !result.IsPartial && result.TotalBytes == 0 && result.FileCount == 0,
            "Nova leitura após cancelamento", "Um novo processo calcula corretamente uma pasta vazia.");
        AwaitRelease(10000);
        Check(ProcessScanSession.OwnedProcessCount == 0 && ProcessScanSession.AvailableSlots == 2,
            "Ausência de slots vazados", ProcessScanSession.DiagnosticSummary);
    }

    private static ProcessScanSession Start(string path, int entriesPerSecond, int cpuPercent)
    {
        ProcessScanSession session = ProcessScanSession.Start(path,
            new ScanOptions { FollowDirectoryLinks = true, CrossVolumeLinks = true, MaxEntriesPerSecond = entriesPerSecond },
            30, cpuPercent);
        Sessions.Add(session);
        return session;
    }

    private static ScanResult Finish(ProcessScanSession session, int milliseconds)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!session.Completed && wait.ElapsedMilliseconds < milliseconds) Thread.Sleep(15);
        Check(session.Completed && session.Result != null, "Resultado terminal publicado", session.Diagnostic);
        completedWorkers++;
        return session.Result;
    }

    private static void AwaitRelease(int milliseconds)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (ProcessScanSession.OwnedProcessCount != 0 && wait.ElapsedMilliseconds < milliseconds) Thread.Sleep(20);
        if (ProcessScanSession.OwnedProcessCount != 0)
            throw new TimeoutException("Saída dos leitores não confirmada; nenhum substituto adicional será iniciado. " + ProcessScanSession.DiagnosticSummary);
    }

    private static void RecordCpu(ProcessScanSession session, int requestedPercent, string profile)
    {
        bool applied = session.CpuLimitApplied;
        string detail = session.Diagnostic;
        Check(applied ? detail.Contains(requestedPercent + "%") : detail.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0,
            "Configuração de CPU ou fallback explícito: " + profile, detail);
        CpuChecks.Add(new { profile = profile, requested_percent_per_reader = requestedPercent,
            native_configuration_confirmed = applied, status = applied ? "CONFIRMED" : "FALLBACK",
            diagnostic = detail, enforcement_benchmark_executed = false });
        bool memoryApplied = session.MemoryLimitApplied;
        Check(memoryApplied ? detail.Contains("Limite nativo de memória confirmado: 384 MiB") :
            detail.Contains("Limite nativo de memória não confirmado") && detail.Contains("guarda reativa"),
            "Configuração de memória ou fallback explícito: " + profile, detail);
        MemoryChecks.Add(new { profile = profile, requested_committed_bytes_per_reader = 402653184L,
            native_configuration_confirmed = memoryApplied, status = memoryApplied ? "CONFIRMED" : "FALLBACK",
            diagnostic = detail, memory_pressure_test_executed = false });
    }

    private static bool SamePath(string a, string b)
    {
        return String.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
    private static void CreateFile(string path, long bytes)
    {
        using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) stream.SetLength(bytes);
    }
    private static string Hash(string path)
    {
        using (SHA256 algorithm = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
    }
    private static void WriteNewText(string path, string text)
    {
        using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
    }
    private static void Check(bool condition, string name, string detail)
    {
        Checks.Add(new { name = name, passed = condition, detail = detail });
        if (!condition) throw new InvalidOperationException(name + ": " + detail);
        Console.WriteLine("PASS " + name);
    }
}
