using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
#if !SETUP_TESTS
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Instalador do Pasta GB Ícones")]
[assembly: AssemblyProduct("Pasta GB Ícones")]
[assembly: AssemblyVersion("6.0.0.0")]
[assembly: AssemblyFileVersion("6.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
#endif

namespace PastaGBSetup
{
    internal static class Product
    {
        internal const string Version = "6.0.0.0";
        internal const string Folder = "PastaGBIcones";
        internal const string Executable = "PastaGBIcones.exe";
        internal const string Config = "PastaGBIcones.exe.config";
        internal const string Uninstaller = "Desinstalar.exe";
        internal const string Marker = "installation.txt";
        internal const string PayloadManifest = "payload-sha256.txt";
        internal const long MaximumExecutableBytes = 16L * 1024 * 1024;
        internal const long MaximumSetupBytes = 64L * 1024 * 1024;
        internal const long MaximumConfigBytes = 64 * 1024;
        internal static readonly string[] OwnedFiles = { Executable, Config, Uninstaller, Marker };

        internal static long MaximumBytes(string name)
        {
            if (name == Executable) return MaximumExecutableBytes;
            if (name == Config) return MaximumConfigBytes;
            if (name == Uninstaller) return MaximumSetupBytes;
            if (name == Marker) return 2048;
            throw new InvalidDataException("Nome de arquivo desconhecido.");
        }
    }

    // Fixed filenames and bounded plain text; a local integrity record, not a signature.
    internal sealed class InstalledManifest
    {
        internal string Version, ExecutableHash, ConfigHash, SetupHash;

        internal static Dictionary<string, string> ParsePayload(string text)
        {
            string[] lines = Lines(text);
            if (lines.Length != 2) throw new InvalidDataException("O manifesto precisa conter exatamente os dois arquivos do programa.");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                if (line.Length < 67 || line.Substring(64, 2) != "  ") throw new InvalidDataException("Manifesto SHA256 inválido.");
                string hash = line.Substring(0, 64).ToUpperInvariant(), name = line.Substring(66);
                if (!ValidHash(hash) || (name != Product.Executable && name != Product.Config) || hashes.ContainsKey(name))
                    throw new InvalidDataException("Arquivo ou hash inválido no manifesto.");
                hashes.Add(name, hash);
            }
            return hashes;
        }

        internal static InstalledManifest Parse(string text)
        {
            string[] lines = Lines(text);
            System.Version version;
            if (lines.Length != 5 || lines[0] != "PastaGBIcones-Installed-1" ||
                !System.Version.TryParse(lines[1], out version) || version.Major < 0 || version.Minor < 0 ||
                !ValidHash(lines[2]) || !ValidHash(lines[3]) || !ValidHash(lines[4]))
                throw new InvalidDataException("Esta pasta não contém um registro de instalação reconhecido.");
            return new InstalledManifest { Version = version.ToString(), ExecutableHash = lines[2].ToUpperInvariant(),
                ConfigHash = lines[3].ToUpperInvariant(), SetupHash = lines[4].ToUpperInvariant() };
        }

        internal string Serialize()
        {
            string value = "PastaGBIcones-Installed-1\n" + Version + "\n" + ExecutableHash + "\n" + ConfigHash + "\n" + SetupHash + "\n";
            Parse(value);
            return value;
        }

        internal string HashFor(string name)
        {
            if (name == Product.Executable) return ExecutableHash;
            if (name == Product.Config) return ConfigHash;
            if (name == Product.Uninstaller) return SetupHash;
            throw new InvalidDataException("Arquivo não pertence ao programa.");
        }

        private static string[] Lines(string text)
        {
            if (String.IsNullOrEmpty(text) || text.Length > 2048) throw new InvalidDataException("Manifesto vazio ou grande demais.");
            foreach (char value in text)
                if ((value < 32 && value != '\r' && value != '\n') || value > 126) throw new InvalidDataException("Texto inválido no manifesto.");
            text = text.Replace("\r\n", "\n");
            if (text.EndsWith("\n", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);
            return text.Split('\n');
        }

        private static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char value in hash)
                if (!((value >= '0' && value <= '9') || (value >= 'A' && value <= 'F') || (value >= 'a' && value <= 'f'))) return false;
            return true;
        }
    }

    internal static class SafeFiles
    {
        internal static string FullPath(string path)
        {
            if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) throw new IOException("É necessário um caminho absoluto.");
            string value = Path.GetFullPath(path), root = Path.GetPathRoot(value);
            while (value.Length > root.Length && (value[value.Length - 1] == Path.DirectorySeparatorChar || value[value.Length - 1] == Path.AltDirectorySeparatorChar))
                value = value.Substring(0, value.Length - 1);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT &&
                (value.Length < 3 || !Char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\'))
                throw new IOException("O instalador precisa de uma pasta local do usuário.");
            return value;
        }

        internal static bool SamePath(string first, string second)
        { return String.Equals(FullPath(first), FullPath(second), StringComparison.OrdinalIgnoreCase); }

        // Inspect every existing component; do not traverse junctions or symbolic links.
        // This is defensive validation, not a privilege boundary against the same user.
        internal static void CheckPath(string path, bool mustExist)
        {
            string full = FullPath(path), root = Path.GetPathRoot(full), current = root;
            string[] components = full.Substring(root.Length).Split(Path.DirectorySeparatorChar);
            CheckComponent(root, true);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i].Length == 0) continue;
                current = Path.Combine(current, components[i]);
                CheckComponent(current, mustExist);
            }
        }

        private static void CheckComponent(string path, bool mustExist)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A operação foi interrompida porque o caminho contém uma ligação para outra pasta ou arquivo: " + path);
            }
            catch (FileNotFoundException) { if (mustExist) throw; }
            catch (DirectoryNotFoundException) { if (mustExist) throw; }
        }

        internal static void CheckRegularFile(string path)
        {
            CheckPath(path, true);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Era esperado um arquivo comum: " + path);
        }

        internal static string Hash(string path, long maximumBytes)
        {
            CheckRegularFile(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Hash(stream, maximumBytes);
        }

        internal static string Hash(Stream stream, long maximumBytes)
        {
            if (stream == null) throw new InvalidDataException("Recurso do instalador ausente.");
            byte[] buffer = new byte[32768]; long total = 0;
            using (SHA256 hash = SHA256.Create())
            {
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    total += count;
                    if (total > maximumBytes) throw new InvalidDataException("Arquivo excede o limite do instalador.");
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                if (total == 0) throw new InvalidDataException("Arquivo vazio no instalador.");
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", String.Empty);
            }
        }

        internal static string ReadText(string path)
        {
            CheckRegularFile(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return ReadText(stream);
        }

        internal static string ReadText(Stream stream)
        {
            if (stream == null) throw new InvalidDataException("Manifesto ausente.");
            using (var memory = new MemoryStream())
            {
                byte[] buffer = new byte[512]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (memory.Length + count > 2048) throw new InvalidDataException("Manifesto grande demais.");
                    memory.Write(buffer, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(memory.ToArray());
            }
        }

        internal static void CopyNew(Stream source, string destination, long maximumBytes)
        {
            if (source == null) throw new InvalidDataException("Recurso do instalador ausente.");
            CheckPath(destination, false);
            using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[32768]; long total = 0; int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    total += count;
                    if (total > maximumBytes) throw new InvalidDataException("Arquivo excede o limite do instalador.");
                    target.Write(buffer, 0, count);
                }
                if (total == 0) throw new InvalidDataException("Arquivo vazio no instalador.");
                target.Flush(true);
            }
        }

        internal static void WriteNewText(string destination, string text)
        {
            using (var source = new MemoryStream(new UTF8Encoding(false, true).GetBytes(text))) CopyNew(source, destination, 2048);
        }
    }

    // All mutations are on fixed child names below one per-user Programs directory.
    // Directory renames are on the same volume. A previous version survives a failed commit.
    internal sealed class InstallEngine
    {
        internal readonly string Programs, Destination, Previous, Stage;
#if SETUP_TESTS
        internal Action<string> BeforeOperationForTests = null;
#endif
        internal InstallEngine(string programs)
        {
            Programs = SafeFiles.FullPath(programs);
            Destination = Path.Combine(Programs, Product.Folder);
            Previous = Path.Combine(Programs, ".PastaGBIcones.previous");
            Stage = Path.Combine(Programs, ".PastaGBIcones.stage");
        }

        internal InstalledManifest Install(Func<string, Stream> resource, string setupPath)
        {
            Recover();
            Dictionary<string, string> hashes;
            using (Stream stream = resource(Product.PayloadManifest)) hashes = InstalledManifest.ParsePayload(SafeFiles.ReadText(stream));
            var manifest = new InstalledManifest { Version = Product.Version, ExecutableHash = hashes[Product.Executable],
                ConfigHash = hashes[Product.Config], SetupHash = SafeFiles.Hash(setupPath, Product.MaximumSetupBytes) };
            CreateStage(manifest);
            try
            {
                using (Stream stream = resource(Product.Executable)) SafeFiles.CopyNew(stream, Path.Combine(Stage, Product.Executable), Product.MaximumExecutableBytes);
                using (Stream stream = resource(Product.Config)) SafeFiles.CopyNew(stream, Path.Combine(Stage, Product.Config), Product.MaximumConfigBytes);
                SafeFiles.CheckRegularFile(setupPath);
                using (var stream = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    SafeFiles.CopyNew(stream, Path.Combine(Stage, Product.Uninstaller), Product.MaximumSetupBytes);
                CommitStage();
                return manifest;
            }
            catch { TryRemoveStage(); throw; }
        }

        internal InstalledManifest Rollback()
        {
            Recover();
            InstalledManifest manifest = ReadOwned(Previous, true, true);
            ReadOwned(Destination, true, true);
            CreateStage(manifest);
            try
            {
                foreach (string name in Product.OwnedFiles)
                {
                    if (name == Product.Marker) continue;
                    string source = Path.Combine(Previous, name);
                    SafeFiles.CheckRegularFile(source);
                    using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                        SafeFiles.CopyNew(stream, Path.Combine(Stage, name), Product.MaximumBytes(name));
                }
                CommitStage();
                return manifest;
            }
            catch { TryRemoveStage(); throw; }
        }

        internal void Uninstall()
        {
            PrepareBase();
            RejectOccupiedFile(Destination); RejectOccupiedFile(Previous); RejectOccupiedFile(Stage);
            ValidateForRemoval(Previous, true); ValidateForRemoval(Stage, false); ValidateForRemoval(Destination, true);
            // Remove backup first: an interrupted uninstall cannot resurrect it later.
            if (Directory.Exists(Previous)) RemoveOwned(Previous, false, true);
            if (Directory.Exists(Stage)) RemoveOwned(Stage, false, false);
            if (Directory.Exists(Destination)) RemoveOwned(Destination, false, true);
            RejectOccupiedFile(Destination); RejectOccupiedFile(Previous); RejectOccupiedFile(Stage);
        }

        internal void Recover()
        {
            PrepareBase();
            RejectOccupiedFile(Destination); RejectOccupiedFile(Previous); RejectOccupiedFile(Stage);
            foreach (string directory in new[] { Destination, Previous, Stage })
                if (Directory.Exists(directory) && Empty(directory)) Directory.Delete(directory, false);
            if (!Directory.Exists(Destination) && Directory.Exists(Previous))
            {
                ReadOwned(Previous, true, true);
                Move(Previous, Destination, "recover");
            }
            if (Directory.Exists(Stage)) RemoveOwned(Stage, false, false);
        }

        private void PrepareBase()
        {
            SafeFiles.CheckPath(Programs, false);
            Directory.CreateDirectory(Programs);
            SafeFiles.CheckPath(Programs, true);
        }

        private static void RejectOccupiedFile(string path)
        {
            SafeFiles.CheckPath(path, false);
            if (File.Exists(path)) throw new IOException("Há um arquivo no local reservado à pasta de instalação: " + path);
        }

        private void CreateStage(InstalledManifest manifest)
        {
            if (Directory.Exists(Destination)) ReadOwned(Destination, true, true);
            RejectOccupiedFile(Stage);
            if (Directory.Exists(Stage)) throw new IOException("A pasta de preparação já está em uso.");
            Directory.CreateDirectory(Stage);
            SafeFiles.CheckPath(Stage, true);
            // Ownership record is durable before any payload is written, so incomplete
            // staged files can be safely cleaned on the next invocation.
            SafeFiles.WriteNewText(Path.Combine(Stage, Product.Marker), manifest.Serialize());
        }

        private void CommitStage()
        {
            ReadOwned(Stage, true, true);
            if (Directory.Exists(Destination)) ReadOwned(Destination, true, true);
            if (Directory.Exists(Previous)) RemoveOwned(Previous, false, true);
            bool movedPrevious = false;
            try
            {
                if (Directory.Exists(Destination)) { Move(Destination, Previous, "backup"); movedPrevious = true; }
                Move(Stage, Destination, "commit");
            }
            catch (Exception failure)
            {
                if (movedPrevious && !Directory.Exists(Destination))
                {
                    try { ReadOwned(Previous, true, true); Move(Previous, Destination, "restore"); }
                    catch (Exception recovery)
                    {
                        throw new IOException("A troca foi interrompida. A versão anterior permanece em " + Previous +
                            ". Abra este instalador novamente para recuperá-la. " + recovery.Message, failure);
                    }
                }
                throw;
            }
        }

        private void Move(string source, string destination, string operation)
        {
            SafeFiles.CheckPath(source, true); SafeFiles.CheckPath(destination, false);
#if SETUP_TESTS
            if (BeforeOperationForTests != null) BeforeOperationForTests(operation);
#endif
            Directory.Move(source, destination);
        }

        private void TryRemoveStage()
        { try { if (Directory.Exists(Stage)) RemoveOwned(Stage, false, false); } catch { /* Preserve evidence for recovery; do not broaden deletion. */ } }

        internal static InstalledManifest ReadOwned(string directory, bool requireComplete, bool verifyHashes)
        {
            SafeFiles.CheckPath(directory, true);
            if (!Directory.Exists(directory)) throw new IOException("A pasta de instalação não foi encontrada: " + directory);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(file);
                bool known = false;
                foreach (string allowed in Product.OwnedFiles) if (String.Equals(name, allowed, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                if (!known || !present.Add(name)) throw new IOException("A pasta contém arquivos que o instalador não gerencia. Mova-os para outro local antes de continuar: " + file);
                SafeFiles.CheckRegularFile(file);
            }
            if (!present.Contains(Product.Marker)) throw new IOException("Pasta existente sem identificação do Pasta GB; nenhum arquivo foi removido: " + directory);
            InstalledManifest manifest = InstalledManifest.Parse(SafeFiles.ReadText(Path.Combine(directory, Product.Marker)));
            if (requireComplete && present.Count != Product.OwnedFiles.Length) throw new IOException("A instalação está incompleta: " + directory);
            if (verifyHashes)
            {
                foreach (string name in Product.OwnedFiles)
                    if (name != Product.Marker && present.Contains(name) &&
                        SafeFiles.Hash(Path.Combine(directory, name), Product.MaximumBytes(name)) != manifest.HashFor(name))
                        throw new IOException("Um arquivo instalado foi alterado. A operação preservou o arquivo: " + Path.Combine(directory, name));
            }
            return manifest;
        }

        private static void RemoveOwned(string directory, bool requireComplete, bool verifyHashes)
        {
            SafeFiles.CheckPath(directory, true);
            if (Empty(directory)) { Directory.Delete(directory, false); return; }
            ReadOwned(directory, requireComplete, verifyHashes);
            // No recursive deletion. The ownership marker is deleted last, permitting
            // safe completion after interruption between individual file deletions.
            foreach (string name in Product.OwnedFiles)
            {
                string file = Path.Combine(directory, name);
                if (!File.Exists(file)) continue;
                SafeFiles.CheckRegularFile(file);
                File.Delete(file);
            }
            SafeFiles.CheckPath(directory, true);
            Directory.Delete(directory, false);
        }

        private static void ValidateForRemoval(string directory, bool verifyHashes)
        {
            if (!Directory.Exists(directory)) return;
            SafeFiles.CheckPath(directory, true);
            if (!Empty(directory)) ReadOwned(directory, false, verifyHashes);
        }

        private static bool Empty(string directory)
        { foreach (string ignored in Directory.EnumerateFileSystemEntries(directory)) return false; return true; }
    }

#if !SETUP_TESTS
    internal sealed class OperationMutex : IDisposable
    {
        private readonly Mutex mutex;
        private bool acquired;
        internal OperationMutex(string name, string failure)
        {
            mutex = new Mutex(false, name);
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { mutex.Dispose(); throw new IOException(failure); }
        }
        public void Dispose()
        { if (acquired) { acquired = false; mutex.ReleaseMutex(); } mutex.Dispose(); }
    }

    internal static class WindowsIntegration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PastaGBIcones";

        internal static string ProgramsPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrEmpty(local)) throw new IOException("A pasta local do usuário não está disponível.");
            return Path.Combine(SafeFiles.FullPath(local), "Programs");
        }

        internal static bool HasFramework48()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                return key != null && Convert.ToInt32(key.GetValue("Release", 0), CultureInfo.InvariantCulture) >= 528040;
        }

        internal static void Register(string directory, InstalledManifest manifest, bool shortcut, List<string> warnings)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    string previous = key.GetValue("InstallLocation") as string;
                    if (!String.IsNullOrEmpty(previous) && !SafeFiles.SamePath(previous, directory))
                        throw new IOException("Já existe outro local registrado para o Pasta GB. O registro anterior foi preservado.");
                    key.SetValue("DisplayName", "Pasta GB Ícones");
                    key.SetValue("DisplayVersion", manifest.Version);
                    key.SetValue("InstallLocation", directory);
                    key.SetValue("DisplayIcon", Path.Combine(directory, Product.Executable));
                    key.SetValue("UninstallString", Quote(Path.Combine(directory, Product.Uninstaller)) + " --uninstall");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception error) { warnings.Add("O registro em Aplicativos instalados não foi concluído: " + error.Message); }
            if (shortcut)
            {
                try { SaveShortcut(directory); }
                catch (Exception error) { warnings.Add("O atalho no menu Iniciar não foi criado: " + error.Message); }
            }
            // Never enable, disable or redirect an existing startup preference silently.
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string current = key == null ? null : key.GetValue(Product.Folder) as string;
                    if (!String.IsNullOrEmpty(current) && !String.Equals(current, Quote(Path.Combine(directory, Product.Executable)), StringComparison.OrdinalIgnoreCase))
                        warnings.Add("A inicialização com o Windows continua configurada para outra cópia. Para usar esta instalação, abra o novo Pasta GB e ative 'Iniciar com o Windows'.");
                }
            }
            catch (Exception error) { warnings.Add("Não foi possível consultar a preferência de inicialização: " + error.Message); }
        }

        internal static void Unregister(string directory, List<string> warnings)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key != null && String.Equals(key.GetValue(Product.Folder) as string,
                        Quote(Path.Combine(directory, Product.Executable)), StringComparison.OrdinalIgnoreCase)) key.DeleteValue(Product.Folder, false);
                bool ours;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    string location = key == null ? null : key.GetValue("InstallLocation") as string;
                    ours = !String.IsNullOrEmpty(location) && SafeFiles.SamePath(location, directory);
                }
                if (ours) Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            }
            catch (Exception error) { warnings.Add("Um registro do programa não pôde ser removido: " + error.Message); }
            try
            {
                string shortcut = ShortcutPath();
                if (File.Exists(shortcut))
                {
                    SafeFiles.CheckRegularFile(shortcut);
                    if (ShortcutPointsTo(shortcut, Path.Combine(directory, Product.Executable))) File.Delete(shortcut);
                }
            }
            catch (Exception error) { warnings.Add("O atalho não pôde ser removido: " + error.Message); }
        }

        private static string ShortcutPath()
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (String.IsNullOrEmpty(programs)) throw new IOException("O menu Iniciar deste usuário não está disponível.");
            return Path.Combine(SafeFiles.FullPath(programs), "Pasta GB Ícones.lnk");
        }

        private static void SaveShortcut(string directory)
        {
            string file = ShortcutPath(), target = Path.Combine(directory, Product.Executable);
            SafeFiles.CheckPath(file, false);
            if (File.Exists(file) && !ShortcutPointsTo(file, target)) throw new IOException("Há um atalho com esse nome apontando para outro programa; ele foi preservado.");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { file });
                Set(link, "TargetPath", target); Set(link, "WorkingDirectory", directory);
                Set(link, "Arguments", String.Empty); Set(link, "Description", "Tamanhos junto às pastas");
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, new object[0]);
            }
            finally { Release(link); Release(shell); }
        }

        private static bool ShortcutPointsTo(string file, string target)
        {
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { file });
                string path = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                string arguments = link.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null) as string;
                return !String.IsNullOrEmpty(path) && String.IsNullOrEmpty(arguments) && SafeFiles.SamePath(path, target);
            }
            finally { Release(link); Release(shell); }
        }

        private static void Set(object target, string name, object value)
        { target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value }); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        internal static string Quote(string path)
        { if (path.IndexOf('"') >= 0) throw new IOException("Caminho inválido."); return "\"" + path + "\""; }
    }

    internal sealed class SetupForm : Form
    {
        private readonly InstallEngine engine;
        private readonly string setupPath;
        private readonly CheckBox shortcut = new CheckBox(), open = new CheckBox();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button install = new Button(), rollback = new Button(), uninstall = new Button(), close = new Button();
        private bool busy;

        internal SetupForm(InstallEngine engine, string setupPath, bool uninstallMode)
        {
            this.engine = engine; this.setupPath = setupPath;
            Text = "Pasta GB Ícones — instalação"; StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(660, 430); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            var title = new Label { Text = "Pasta GB Ícones 6.0", Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) };
            var description = new Label { Text = "Instalação para este usuário. Suas preferências e os tamanhos salvos são preservados.\n\nLocal: " + engine.Destination,
                Location = new Point(26, 70), Size = new Size(606, 90) };
            shortcut.Text = "Criar atalho no menu Iniciar"; shortcut.Checked = true; shortcut.SetBounds(26, 165, 600, 26);
            open.Text = "Abrir o Pasta GB depois de instalar"; open.Checked = true; open.SetBounds(26, 195, 600, 26);
            progress.SetBounds(26, 239, 606, 12); progress.Style = ProgressBarStyle.Marquee; progress.Visible = false;
            status.SetBounds(26, 265, 606, 94); status.Text = uninstallMode ? "Desinstalar remove o programa e a versão anterior. Suas preferências e o cache são mantidos." : "Se o Pasta GB estiver aberto, escolha 'Encerrar' no ícone perto do relógio antes de continuar.";
            install.Text = "Instalar / atualizar"; install.SetBounds(26, 376, 158, 34);
            rollback.Text = "Voltar à anterior"; rollback.SetBounds(192, 376, 148, 34);
            uninstall.Text = "Desinstalar"; uninstall.SetBounds(348, 376, 128, 34);
            close.Text = "Fechar"; close.SetBounds(504, 376, 128, 34);
            Controls.AddRange(new Control[] { title, description, shortcut, open, progress, status, install, rollback, uninstall, close });
            install.Click += delegate { Run("install"); }; rollback.Click += delegate { Run("rollback"); };
            uninstall.Click += delegate
            {
                if (MessageBox.Show(this, "Remover o Pasta GB Ícones deste usuário?\n\nPreferências e cache permanecerão guardados para uma futura instalação.", "Desinstalar Pasta GB", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Run("uninstall");
            };
            close.Click += delegate { Close(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            AcceptButton = uninstallMode ? uninstall : install; CancelButton = close;
        }

        private void Run(string operation)
        {
            if (busy) return;
            bool wantShortcut = shortcut.Checked, wantOpen = open.Checked && operation == "install";
            Busy(true); status.Text = "Preparando a operação…";
            var worker = new Thread(delegate()
            {
                var warnings = new List<string>(); string message; Exception failure = null;
                try
                {
                    using (var setupMutex = new OperationMutex("Local\\PastaGB.Setup.1", "Outro instalador está aberto. Aguarde sua conclusão."))
                    using (var appMutex = new OperationMutex("Local\\PastaGB.Icones.2", "O Pasta GB está em execução. Escolha 'Encerrar' no ícone perto do relógio e tente novamente."))
                    {
                        if (operation == "uninstall")
                        {
                            engine.Uninstall(); WindowsIntegration.Unregister(engine.Destination, warnings);
                            message = "Programa desinstalado. Preferências e cache foram preservados.";
                        }
                        else
                        {
                            InstalledManifest manifest = operation == "rollback" ? engine.Rollback() :
                                engine.Install(name => Assembly.GetExecutingAssembly().GetManifestResourceStream(name), setupPath);
                            WindowsIntegration.Register(engine.Destination, manifest, wantShortcut, warnings);
                            message = operation == "rollback" ? "Versão anterior restaurada: " + manifest.Version + "." : "Pasta GB instalado: " + manifest.Version + ".";
                        }
                    }
                }
                catch (Exception error) { failure = error; message = "A operação não foi concluída."; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        Busy(false); status.Text = message + (warnings.Count == 0 ? String.Empty : " Há observações abaixo.");
                        if (failure != null) MessageBox.Show(this, failure.Message, "Pasta GB — operação interrompida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else
                        {
                            if (warnings.Count != 0) MessageBox.Show(this, message + "\n\n" + String.Join("\n\n", warnings.ToArray()), "Pasta GB — observações", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            if (wantOpen)
                            {
                                try { Process.Start(new ProcessStartInfo(Path.Combine(engine.Destination, Product.Executable)) { UseShellExecute = true }); }
                                catch (Exception error) { MessageBox.Show(this, "O programa foi instalado, mas não abriu: " + error.Message, "Pasta GB"); }
                            }
                        }
                    }));
                }
                catch (InvalidOperationException) { }
            });
            worker.IsBackground = false; worker.Name = "Pasta GB installer"; worker.SetApartmentState(ApartmentState.STA);
            try { worker.Start(); }
            catch (Exception error) { Busy(false); status.Text = "A operação não começou."; MessageBox.Show(this, error.Message, "Pasta GB"); }
        }

        private void Busy(bool value)
        {
            busy = value; progress.Visible = value;
            install.Enabled = rollback.Enabled = uninstall.Enabled = close.Enabled = shortcut.Enabled = open.Enabled = !value;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new IOException("Este instalador é para Windows.");
                if (args.Length > 1 || (args.Length == 1 && args[0] != "--uninstall")) throw new IOException("Argumento desconhecido. Abra o instalador sem parâmetros.");
                if (!WindowsIntegration.HasFramework48()) throw new IOException("Instale o .NET Framework 4.8 da Microsoft antes de continuar.");
                var engine = new InstallEngine(WindowsIntegration.ProgramsPath());
                string self = SafeFiles.FullPath(Assembly.GetExecutingAssembly().Location);
                string directory = Path.GetDirectoryName(self);
                if (SafeFiles.SamePath(directory, engine.Destination) || SafeFiles.SamePath(directory, engine.Previous) || SafeFiles.SamePath(directory, engine.Stage))
                {
                    // The executable must leave the installation before it can remove
                    // or replace itself. No shell command or process termination is used.
                    string temporary = Path.Combine(Path.GetTempPath(), "PastaGBSetup-" + Guid.NewGuid().ToString("N"));
                    SafeFiles.CheckPath(temporary, false); Directory.CreateDirectory(temporary); SafeFiles.CheckPath(temporary, true);
                    string target = Path.Combine(temporary, "PastaGB-Instalar.exe");
                    string expected = SafeFiles.Hash(self, Product.MaximumSetupBytes);
                    using (var source = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read)) SafeFiles.CopyNew(source, target, Product.MaximumSetupBytes);
                    if (SafeFiles.Hash(target, Product.MaximumSetupBytes) != expected) throw new IOException("A cópia temporária do instalador não foi verificada.");
                    Process.Start(new ProcessStartInfo(target, args.Length == 1 ? "--uninstall" : String.Empty) { UseShellExecute = false, WorkingDirectory = temporary });
                    return;
                }
                using (var form = new SetupForm(engine, self, args.Length == 1)) Application.Run(form);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Instalador do Pasta GB", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
#endif
}
