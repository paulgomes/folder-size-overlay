// Windows INTERACTIVE regression harness. References the actual delivered EXE.
// It never certifies raster/text placement from a screenshot automatically.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PastaGBIcons;

internal static class ExplorerRegressionHarness
{
    private static readonly List<object> checks = new List<object>();
    private static string output, executable;
    private static uint appProcess;
    private static string fingerprintA;
    private static readonly Dictionary<string, long> expectedBytes = new Dictionary<string, long>();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || Environment.OSVersion.Platform != PlatformID.Win32NT)
        { Console.Error.WriteLine("Uso em Windows interativo: ExplorerRegressionHarness.exe caminho-do-app pasta-de-saida"); return 2; }
        executable = Path.GetFullPath(args[0]); output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(output, "cenario");
        string error = null;
        dynamic shell = null, browser = null;
        try
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            string a = Path.Combine(fixture, "A");
            string b = Path.Combine(fixture, "B");
            string empty = Path.Combine(fixture, "Vazia");
            AddFolder(a, "Fotos", 2000000); AddFolder(a, "Relatórios", 3500000);
            AddFolder(b, "Fotos", 7000000); AddFolder(b, "Relatórios", 9000000);
            Directory.CreateDirectory(empty);
            string appHash = Hash(executable);
            foreach (Process running in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            {
                using (running)
                {
                    if (!String.Equals(Hash(running.MainModule.FileName), appHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("Outra versão do aplicativo está aberta. Feche-a antes de testar este executável.");
                    // Child scanners have the same filename; prefer an app with
                    // actual overlay windows below after discovering the folder.
                    if (appProcess == 0) appProcess = (uint)running.Id;
                }
            }
            if (appProcess == 0)
            {
                Process process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                if (process != null) { appProcess = (uint)process.Id; process.Dispose(); }
            }
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
            Process.Start(new ProcessStartInfo("explorer.exe", "/n,\"" + a + "\"") { UseShellExecute = true });
            browser = FindBrowser(shell, a);
            IntPtr root = new IntPtr(Convert.ToInt64(browser.HWND, CultureInfo.InvariantCulture));

            Stage(browser, root, a, 1, "01-A-icones", true);
            Stage(browser, root, b, 1, "02-B-mesmos-nomes", true);
            Stage(browser, root, empty, 1, "03-pasta-vazia", false);
            Stage(browser, root, a, 1, "04-retorno-A", true);
            Stage(browser, root, a, 4, "05-detalhes", false);
            Stage(browser, root, a, 3, "06-lista", false);
            Stage(browser, root, a, 5, "07-miniaturas", false);

            dynamic document = browser.Document;
            dynamic selected = document.Folder.ParseName("Fotos");
            document.SelectItem(selected, 1 | 4 | 8 | 16);
            ExplorerSurface selection = AwaitSurface(root, a);
            bool foundSelection = selection.Snapshot.Folders.Any(item => item.Name == "Fotos" && item.IsSelected);
            if (!foundSelection) throw new InvalidOperationException("A seleção nativa não foi refletida pelo snapshot.");
            checks.Add(new { name = "Seleção nativa", passed = true, scope = "Shell/UIA", detail = "Fotos selecionada e identificada sem trocar o caminho." });
            Screenshot(root, "08-selecao.png");
            Release(selected); Release(document);
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            checks.Add(new { name = "Execução", passed = false, scope = "automated", detail = exception.Message });
        }
        finally
        {
            Release(browser); Release(shell);
            var report = new {
                generated_utc = DateTime.UtcNow.ToString("o"),
                app = executable, app_sha256 = File.Exists(executable) ? Hash(executable) : null,
                app_version = File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).FileVersion : null,
                os = Environment.OSVersion.ToString(), process_bits = IntPtr.Size * 8,
                windows_interactive_execution = true, automated_passed = error == null,
                visual_review = "PENDING_MANUAL", fixture = fixture,
                expected_logical_bytes = expectedBytes, checks = checks, error = error,
                limits = "Shell/UIA e propriedades nativas foram exercitados. Screenshots exigem inspeção humana. USB, DPI múltiplo, edição e arraste seguem a matriz manual."
            };
            File.WriteAllText(Path.Combine(output, "resultado-windows.json"), new JavaScriptSerializer().Serialize(report), Encoding.UTF8);
        }
        Console.WriteLine(error == null ? "Verificações automáticas concluídas. Inspeção visual continua pendente." : "FALHA: " + error);
        Console.WriteLine("Evidências: " + output);
        return error == null ? 0 : 1;
    }

    private static void AddFolder(string parent, string name, long bytes)
    {
        string folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        using (var stream = new FileStream(Path.Combine(folder, "conteudo.bin"), FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(bytes);
        expectedBytes[folder] = bytes;
    }

    private static dynamic FindBrowser(dynamic shell, string path)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 15000)
        {
            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic candidate = null;
                try
                {
                    candidate = windows.Item(i);
                    if (SamePath((string)candidate.Document.Folder.Self.Path, path))
                    { Release(windows); return candidate; }
                }
                catch { }
                Release(candidate);
            }
            Release(windows); Pump(200);
        }
        throw new TimeoutException("O Explorador não abriu a janela exclusiva do cenário.");
    }

    private static void Stage(dynamic browser, IntPtr root, string path, int mode, string name, bool requireLabels)
    {
        browser.Navigate2(path);
        ExplorerSurface surface = AwaitSurface(root, path);
        dynamic document = browser.Document;
        document.CurrentViewMode = mode;
        Release(document); Pump(600);
        surface = AwaitSurface(root, path);
        if (surface.ViewMode != (uint)mode)
            throw new InvalidOperationException("O modo solicitado não foi aplicado: " + name);
        HashSet<string> expected = new HashSet<string>(Directory.GetDirectories(path), StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = new HashSet<string>(surface.Snapshot.Folders.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(actual)) throw new InvalidOperationException("Identidade ou quantidade de pastas incorreta: " + name);
        foreach (DesktopFolderIcon folder in surface.Snapshot.Folders)
            if (!SamePath(Path.GetDirectoryName(folder.Path), path) || !folder.ItemBounds.IntersectsWith(surface.Snapshot.HostScreenBounds))
                throw new InvalidOperationException("Pasta associada a outro local ou posição: " + name);
        if (name == "01-A-icones") fingerprintA = surface.Fingerprint;
        if (name == "02-B-mesmos-nomes" && surface.Fingerprint == fingerprintA)
            throw new InvalidOperationException("A troca de pasta reutilizou a identidade anterior.");
        if (name == "04-retorno-A" && surface.Fingerprint != fingerprintA)
            throw new InvalidOperationException("O retorno à pasta A não recuperou sua identidade.");

        List<IntPtr> labels = new List<IntPtr>();
        Stopwatch wait = Stopwatch.StartNew();
        do
        {
            labels = VisibleLabels(root);
            if (expected.Count == 0 ? labels.Count == 0 : labels.Count > 0 || !requireLabels) break;
            Pump(250);
        } while (wait.ElapsedMilliseconds < 12000);
        if (expected.Count == 0 && labels.Count != 0) throw new InvalidOperationException("Etiquetas antigas permaneceram na pasta vazia.");
        if (requireLabels && labels.Count == 0) throw new InvalidOperationException("Nenhuma etiqueta visível no modo de ícones: " + name);
        foreach (IntPtr label in labels) ValidateClickThrough(label);
        var captions = labels.Select(LabelText).ToArray();
        checks.Add(new { name = name, passed = true, scope = "Shell/UIA + HWND",
            folders = actual.ToArray(), fingerprint = surface.Fingerprint, dpi = surface.Snapshot.Dpi,
            view_mode = surface.ViewMode, visible_labels = labels.Count, captions = captions,
            visual_review = "PENDING_MANUAL", screenshot = Screenshot(root, name + ".png") });
        Console.WriteLine("PASS " + name + ": " + actual.Count + " pastas; " + labels.Count + " etiquetas nativas observadas.");
    }

    private static ExplorerSurface AwaitSurface(IntPtr root, string path)
    {
        Stopwatch clock = Stopwatch.StartNew();
        string last = null;
        while (clock.ElapsedMilliseconds < 15000)
        {
            ExplorerCaptureResult result = null;
            Exception failure = null;
            Thread worker = new Thread(delegate() {
                try { result = ExplorerProbe.Capture(); }
                catch (Exception exception) { failure = exception; }
                finally { ExplorerProbe.ReleaseThreadCache(); }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            Stopwatch attempt = Stopwatch.StartNew();
            while (!worker.Join(25))
            {
                Application.DoEvents();
                if (attempt.ElapsedMilliseconds > 10000)
                    throw new TimeoutException("Shell bloqueado no teste. Nenhum worker substituto será iniciado nesta execução.");
            }
            if (failure != null) throw failure;
            if (result != null)
            {
                last = result.Status;
                foreach (ExplorerSurface surface in result.Surfaces)
                    if (surface.RootHandle == root && SamePath(surface.Location, path) && surface.Snapshot.Visible && !surface.MetadataPending)
                        return surface;
            }
            Pump(200);
        }
        throw new TimeoutException("Não foi possível obter snapshot confirmado de " + path + ": " + last);
    }

    private static List<IntPtr> VisibleLabels(IntPtr root)
    {
        List<IntPtr> labels = new List<IntPtr>();
        EnumChildWindows(root, delegate(IntPtr window, IntPtr data) {
            if (!IsWindowVisible(window) || !LabelText(window).StartsWith("Pasta GB: ", StringComparison.Ordinal)) return true;
            uint process;
            GetWindowThreadProcessId(window, out process);
            try
            {
                using (Process owner = Process.GetProcessById((int)process))
                    if (String.Equals(Hash(owner.MainModule.FileName), Hash(executable), StringComparison.Ordinal)) labels.Add(window);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return labels;
    }

    private static void ValidateClickThrough(IntPtr window)
    {
        long exStyle = IntPtr.Size == 8 ? GetWindowLongPtr(window, -20).ToInt64() : GetWindowLong(window, -20);
        long style = IntPtr.Size == 8 ? GetWindowLongPtr(window, -16).ToInt64() : GetWindowLong(window, -16);
        if ((exStyle & 0x00000020) == 0 || (exStyle & 0x08000000) == 0 || (style & 0x08000000) == 0)
            throw new InvalidOperationException("Uma etiqueta pode interceptar a interação nativa.");
        UIntPtr result;
        if (SendMessageTimeout(window, 0x0084, IntPtr.Zero, IntPtr.Zero, 0x22, 500, out result) == IntPtr.Zero ||
            (IntPtr.Size == 8 ? unchecked((long)result.ToUInt64()) : unchecked((int)result.ToUInt32())) != -1)
            throw new InvalidOperationException("A etiqueta não confirmou HTTRANSPARENT.");
    }

    private static string Screenshot(IntPtr root, string name)
    {
        SetForegroundWindow(root); Pump(150);
        if (GetForegroundWindow() != root) return "SKIPPED: janela não está em primeiro plano";
        NativeRect native;
        if (!GetWindowRect(root, out native)) return "SKIPPED: limites indisponíveis";
        Rectangle bounds = Rectangle.Intersect(Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom), SystemInformation.VirtualScreen);
        if (bounds.Width < 1 || bounds.Height < 1) return "SKIPPED: janela fora da tela";
        using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bitmap.Save(Path.Combine(output, name), ImageFormat.Png);
        }
        return name;
    }
    private static string LabelText(IntPtr window)
    { StringBuilder value = new StringBuilder(256); GetWindowText(window, value, value.Capacity); return value.ToString(); }
    private static bool SamePath(string a, string b)
    { return String.Equals(a == null ? null : a.TrimEnd('\\'), b == null ? null : b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
    private static void Pump(int milliseconds)
    { Stopwatch clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); } }
    private static string Hash(string path)
    { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void Release(object value)
    { if (value != null && Marshal.IsComObject(value)) try { Marshal.ReleaseComObject(value); } catch (InvalidComObjectException) { } }
    private delegate bool EnumChild(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr root, EnumChild callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out UIntPtr result);
}
