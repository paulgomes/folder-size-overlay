using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

internal static class PackageMetadataTests
{
    public static int Main()
    {
        try
        {
            string directory = Path.GetFullPath("pasta-gb-icones");
            string executable = Path.Combine(directory, "PastaGBIcones.exe");
            Version expected = new Version(6, 0, 0, 0);
            if (AssemblyName.GetAssemblyName(executable).Version != expected) throw new Exception("Assembly version mismatch.");
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(executable);
            if (info.FileVersion != "6.0.0.0") throw new Exception("File version mismatch: " + info.FileVersion);
            byte[] bytes = File.ReadAllBytes(executable);
            if (bytes.Length < 1024 || bytes[0] != 'M' || bytes[1] != 'Z') throw new Exception("Missing Windows PE executable.");
            XDocument manifest = XDocument.Load(Path.Combine(directory, "app.manifest"));
            XNamespace assembly = "urn:schemas-microsoft-com:asm.v1";
            if ((string)manifest.Root.Element(assembly + "assemblyIdentity").Attribute("version") != "6.0.0.0")
                throw new Exception("Manifest version mismatch.");
            XDocument config = XDocument.Load(Path.Combine(directory, "PastaGBIcones.exe.config"));
            if ((string)config.Root.Element("startup").Element("supportedRuntime").Attribute("sku") != ".NETFramework,Version=v4.8")
                throw new Exception("Runtime target mismatch.");
            if ((string)config.Root.Element("System.Windows.Forms.ApplicationConfigurationSection").Element("add").Attribute("value") != "PerMonitorV2")
                throw new Exception("WinForms DPI opt-in missing.");
            Console.WriteLine("PASS: 6 checks of PE, assembly, file version, manifest, runtime target and WinForms DPI configuration; no Windows execution.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
}
