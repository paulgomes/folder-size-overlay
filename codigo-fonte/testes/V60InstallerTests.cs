using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PastaGBSetup;

internal static class V60InstallerTests
{
    private static int assertions;

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "pasta-gb-installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ParsingAndBounds(root);
            InstallationAndRollback(Path.Combine(root, "ordinary"));
            CommitFailureAndRecovery(Path.Combine(root, "failure"));
            PreparationFailures(Path.Combine(root, "prepare"));
            OwnershipAndUninstall(Path.Combine(root, "ownership"));
            LinksAndPaths(Path.Combine(root, "paths"));
            Console.WriteLine("PASS: " + assertions + " installer assertions; managed transaction/file I/O on " + Environment.OSVersion.Platform + "; Windows UI, mutex, registry and shortcut integration not executed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Check(bool value, string reason)
    { assertions++; if (!value) throw new Exception("Assertion failed: " + reason); }

    private static void Reject(Action action, string reason)
    {
        bool rejected = false;
        try { action(); }
        catch (IOException) { rejected = true; }
        catch (InvalidDataException) { rejected = true; }
        catch (UnauthorizedAccessException) { rejected = true; }
        Check(rejected, reason);
    }

    private sealed class Payload
    {
        internal readonly byte[] App, Config, Setup;
        internal readonly string SetupPath;
        internal Dictionary<string, byte[]> Overrides = new Dictionary<string, byte[]>();
        internal Payload(string root, string version)
        {
            Directory.CreateDirectory(root);
            App = Encoding.UTF8.GetBytes("executable-test-fixture-" + version);
            Config = Encoding.UTF8.GetBytes("<configuration>" + version + "</configuration>");
            Setup = Encoding.UTF8.GetBytes("setup-test-fixture-" + version);
            SetupPath = Path.Combine(root, "Setup-" + version + ".exe");
            File.WriteAllBytes(SetupPath, Setup);
        }
        internal Stream Open(string name)
        {
            byte[] value;
            if (Overrides.TryGetValue(name, out value)) return value == null ? null : new MemoryStream(value, false);
            if (name == Product.Executable) return new MemoryStream(App, false);
            if (name == Product.Config) return new MemoryStream(Config, false);
            if (name == Product.PayloadManifest) return new MemoryStream(Encoding.ASCII.GetBytes(ManifestText), false);
            throw new InvalidDataException("Unknown fixture resource");
        }
        internal string ManifestText
        { get { return Hash(App) + "  " + Product.Executable + "\n" + Hash(Config) + "  " + Product.Config + "\n"; } }
    }

    private static string Hash(byte[] bytes)
    { using (var stream = new MemoryStream(bytes)) return SafeFiles.Hash(stream, Product.MaximumSetupBytes); }
    private static byte[] ReadApp(InstallEngine engine) { return File.ReadAllBytes(Path.Combine(engine.Destination, Product.Executable)); }
    private static void CopyOwned(string source, string target)
    { Directory.CreateDirectory(target); foreach (string name in Product.OwnedFiles) File.Copy(Path.Combine(source, name), Path.Combine(target, name)); }

    private static void ParsingAndBounds(string root)
    {
        var payload = new Payload(root, "manifest");
        var hashes = InstalledManifest.ParsePayload(payload.ManifestText);
        Check(hashes.Count == 2 && hashes[Product.Executable] == Hash(payload.App), "strict payload manifest round trip");
        Check(InstalledManifest.ParsePayload(payload.ManifestText.Replace("\n", "\r\n")).Count == 2, "CRLF accepted");
        Reject(() => InstalledManifest.ParsePayload(payload.ManifestText + "\n"), "trailing third line rejected");
        Reject(() => InstalledManifest.ParsePayload(payload.ManifestText.Replace(Product.Config, Product.Executable)), "duplicate resource name rejected");
        Reject(() => InstalledManifest.ParsePayload(payload.ManifestText.Replace(Product.Config, "../victim")), "manifest cannot choose destination path");
        Reject(() => InstalledManifest.ParsePayload(payload.ManifestText.Replace("  ", " ")), "ambiguous hash separation rejected");
        Reject(() => InstalledManifest.ParsePayload("Z" + payload.ManifestText.Substring(1)), "non-hex hash rejected");
        Reject(() => InstalledManifest.ParsePayload(new string('x', 2049)), "manifest size bounded");
        Reject(() => InstalledManifest.ParsePayload(payload.ManifestText + "\0"), "control characters rejected");
        Reject(() => InstalledManifest.Parse("Unknown\n1.0\n" + new string('A', 64)), "unknown ownership marker rejected");
        using (var stream = new MemoryStream(new byte[2049])) Reject(() => SafeFiles.ReadText(stream), "bounded stream manifest read");
        using (var stream = new MemoryStream(new byte[10])) Reject(() => SafeFiles.Hash(stream, 9), "bounded hashing");
        using (var stream = new MemoryStream()) Reject(() => SafeFiles.Hash(stream, 9), "zero byte resources rejected");
        string existing = Path.Combine(root, "existing.txt"); File.WriteAllText(existing, "preserve");
        using (var stream = new MemoryStream(new byte[] { 1 })) Reject(() => SafeFiles.CopyNew(stream, existing, 10), "copy never overwrites an existing path");
        Check(File.ReadAllText(existing) == "preserve", "existing bytes preserved");
        Reject(() => new InstallEngine("relative/path"), "relative install base rejected");
    }

    private static void InstallationAndRollback(string root)
    {
        var first = new Payload(root, "first"); var next = new Payload(root, "next");
        var engine = new InstallEngine(Path.Combine(root, "Programs"));
        InstalledManifest installed = engine.Install(first.Open, first.SetupPath);
        Check(installed.Version == Product.Version && ReadApp(engine).SequenceEqual(first.App), "first installation commits verified executable");
        Check(Directory.GetFiles(engine.Destination).Length == 4 && !Directory.Exists(engine.Stage), "only four known installed files and no stage");
        Check(!Directory.Exists(engine.Previous), "fresh install has no invented previous version");
        Check(File.ReadAllBytes(Path.Combine(engine.Destination, Product.Uninstaller)).SequenceEqual(first.Setup), "same installer is copied for per-user uninstall");
        InstallEngine.ReadOwned(engine.Destination, true, true);
        engine.Install(next.Open, next.SetupPath);
        Check(ReadApp(engine).SequenceEqual(next.App), "upgrade commits new executable");
        Check(File.ReadAllBytes(Path.Combine(engine.Previous, Product.Executable)).SequenceEqual(first.App), "upgrade retains previous version");
        engine.Rollback();
        Check(ReadApp(engine).SequenceEqual(first.App), "rollback restores previous executable");
        Check(File.ReadAllBytes(Path.Combine(engine.Previous, Product.Executable)).SequenceEqual(next.App), "rollback itself remains reversible");
        engine.Rollback(); Check(ReadApp(engine).SequenceEqual(next.App), "second rollback swaps complete verified versions");
        Check(!Directory.Exists(engine.Stage), "rollback leaves no stage");
    }

    private static void CommitFailureAndRecovery(string root)
    {
        var first = new Payload(root, "first"); var next = new Payload(root, "next");
        var engine = new InstallEngine(Path.Combine(root, "Programs")); engine.Install(first.Open, first.SetupPath);
        engine.BeforeOperationForTests = operation => { if (operation == "commit") throw new IOException("injected commit failure"); };
        Reject(() => engine.Install(next.Open, next.SetupPath), "commit failure reaches caller");
        Check(ReadApp(engine).SequenceEqual(first.App), "failed commit restores old directory");
        Check(!Directory.Exists(engine.Stage) && !Directory.Exists(engine.Previous), "failed commit leaves one complete original version");
        engine.BeforeOperationForTests = operation => { if (operation == "commit" || operation == "restore") throw new IOException("injected double failure"); };
        Reject(() => engine.Install(next.Open, next.SetupPath), "failed immediate restoration reaches caller");
        Check(!Directory.Exists(engine.Destination) && Directory.Exists(engine.Previous), "previous version survives failed restore");
        engine.BeforeOperationForTests = null;
        engine.Recover(); Check(ReadApp(engine).SequenceEqual(first.App), "next invocation recovers preserved previous version");
        CopyOwned(engine.Destination, engine.Stage); Directory.Move(engine.Destination, engine.Previous);
        engine.Recover();
        Check(ReadApp(engine).SequenceEqual(first.App) && !Directory.Exists(engine.Previous) && !Directory.Exists(engine.Stage), "simulated process crash between renames recovers old version and removes known stage");
        Directory.CreateDirectory(engine.Stage); engine.Recover();
        Check(!Directory.Exists(engine.Stage), "empty stage after pre-marker interruption is safely removed");
        var fresh = new InstallEngine(Path.Combine(root, "Fresh"));
        fresh.BeforeOperationForTests = operation => { if (operation == "commit") throw new IOException("first commit failure"); };
        Reject(() => fresh.Install(first.Open, first.SetupPath), "first commit failure reported");
        Check(!Directory.Exists(fresh.Destination) && !Directory.Exists(fresh.Previous) && !Directory.Exists(fresh.Stage), "failed first install leaves no partial program");
    }

    private static void PreparationFailures(string root)
    {
        var first = new Payload(root, "first"); var next = new Payload(root, "next");
        var engine = new InstallEngine(Path.Combine(root, "Programs")); engine.Install(first.Open, first.SetupPath);
        next.Overrides[Product.Executable] = Encoding.ASCII.GetBytes("tampered payload");
        Reject(() => engine.Install(next.Open, next.SetupPath), "bad payload hash rejected before commit");
        Check(ReadApp(engine).SequenceEqual(first.App) && !Directory.Exists(engine.Previous), "bad payload cannot replace current installation");
        Check(!Directory.Exists(engine.Stage), "bad staged payload cleanup is limited to known files");
        next.Overrides.Clear(); next.Overrides[Product.Config] = new byte[Product.MaximumConfigBytes + 1];
        Reject(() => engine.Install(next.Open, next.SetupPath), "oversized embedded config rejected while copying");
        Check(ReadApp(engine).SequenceEqual(first.App) && !Directory.Exists(engine.Stage), "oversized resource leaves old installation intact");
        next.Overrides.Clear(); next.Overrides[Product.PayloadManifest] = null;
        Reject(() => engine.Install(next.Open, next.SetupPath), "missing manifest fails closed");
        Check(ReadApp(engine).SequenceEqual(first.App), "missing resource preserves previous bytes");
        string marker = File.ReadAllText(Path.Combine(engine.Destination, Product.Marker));
        Directory.CreateDirectory(engine.Stage); File.WriteAllText(Path.Combine(engine.Stage, Product.Marker), marker);
        File.WriteAllText(Path.Combine(engine.Stage, Product.Executable), "interrupted partial write");
        engine.Recover(); Check(!Directory.Exists(engine.Stage), "owned incomplete staged file can be safely discarded");
        Directory.CreateDirectory(engine.Stage); File.WriteAllText(Path.Combine(engine.Stage, "notes.txt"), "user data");
        Reject(() => engine.Recover(), "unrecognized stage is not recursively deleted");
        Check(File.ReadAllText(Path.Combine(engine.Stage, "notes.txt")) == "user data", "foreign staging contents preserved");
    }

    private static void OwnershipAndUninstall(string root)
    {
        var first = new Payload(root, "first"); var next = new Payload(root, "next");
        var engine = new InstallEngine(Path.Combine(root, "Programs"));
        string data = Path.Combine(root, Product.Folder); Directory.CreateDirectory(data);
        string preferences = Path.Combine(data, "preferencias.ini"); File.WriteAllText(preferences, "original preferences");
        engine.Install(first.Open, first.SetupPath); engine.Install(next.Open, next.SetupPath);
        string foreign = Path.Combine(engine.Destination, "my-document.txt"); File.WriteAllText(foreign, "important");
        Reject(() => engine.Install(first.Open, first.SetupPath), "upgrade refuses unexpected files in managed directory");
        Reject(() => engine.Uninstall(), "uninstall preflight refuses unexpected files");
        Check(File.ReadAllText(foreign) == "important" && Directory.Exists(engine.Previous), "uninstall preflight preserves user file and backup");
        File.Delete(foreign);
        string app = Path.Combine(engine.Destination, Product.Executable); File.WriteAllText(app, "modified executable");
        Reject(() => engine.Uninstall(), "changed installed executable is preserved");
        Check(File.ReadAllText(app) == "modified executable" && Directory.Exists(engine.Previous), "tampered file prevents removal of either version");
        File.WriteAllBytes(app, next.App);
        engine.Uninstall();
        Check(!Directory.Exists(engine.Destination) && !Directory.Exists(engine.Previous) && !Directory.Exists(engine.Stage), "uninstall removes owned program and backup directories");
        Check(File.ReadAllText(preferences) == "original preferences", "per-user preferences outside install directory survive uninstall");
        engine.Uninstall(); Check(Directory.Exists(data), "repeat uninstall is harmless to data");
        engine.Install(first.Open, first.SetupPath); File.Delete(Path.Combine(engine.Destination, Product.Executable));
        engine.Uninstall(); Check(!Directory.Exists(engine.Destination), "interrupted or quarantined payload removal resumes using ownership marker");
        Directory.CreateDirectory(engine.Destination); engine.Uninstall();
        Check(!Directory.Exists(engine.Destination), "interruption after marker removal leaves only a removable empty directory");
        Directory.CreateDirectory(engine.Destination); File.WriteAllText(Path.Combine(engine.Destination, "mine.txt"), "mine");
        Reject(() => engine.Uninstall(), "unknown existing installation directory preserved");
        Check(File.ReadAllText(Path.Combine(engine.Destination, "mine.txt")) == "mine", "unknown directory content survives");
    }

    private static void LinksAndPaths(string root)
    {
        var payload = new Payload(root, "links");
        string outside = Path.Combine(root, "Outside"); Directory.CreateDirectory(outside);
        string precious = Path.Combine(outside, "precious.txt"); File.WriteAllText(precious, "keep me");
        string programs = Path.Combine(root, "ProgramsLink"); Directory.CreateSymbolicLink(programs, outside);
        var linkedBase = new InstallEngine(programs);
        Reject(() => linkedBase.Install(payload.Open, payload.SetupPath), "linked Programs ancestor rejected");
        Check(!Directory.Exists(Path.Combine(outside, Product.Folder)) && File.ReadAllText(precious) == "keep me", "installation never traverses linked base");
        var engine = new InstallEngine(Path.Combine(root, "Programs")); Directory.CreateDirectory(engine.Programs);
        Directory.CreateSymbolicLink(engine.Destination, outside);
        Reject(() => engine.Uninstall(), "linked destination rejected before deletion");
        Check(File.ReadAllText(precious) == "keep me", "linked target contents untouched");
        Directory.Delete(engine.Destination); engine.Install(payload.Open, payload.SetupPath);
        string app = Path.Combine(engine.Destination, Product.Executable); File.Delete(app); File.CreateSymbolicLink(app, precious);
        Reject(() => engine.Uninstall(), "linked owned filename is not trusted as a normal file");
        Check(File.ReadAllText(precious) == "keep me", "file link target preserved");
        File.Delete(app); File.WriteAllBytes(app, payload.App);
        Directory.CreateSymbolicLink(engine.Previous, outside);
        Reject(() => engine.Install(payload.Open, payload.SetupPath), "linked previous-version directory rejected");
        Check(ReadApp(engine).SequenceEqual(payload.App), "unsafe backup path cannot disturb current installation");
    }
}
