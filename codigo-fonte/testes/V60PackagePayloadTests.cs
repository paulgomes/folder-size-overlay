using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

internal static class V60PackagePayloadTests
{
    private static int count;
    private static void Check(bool value, string message)
    { count++; if (!value) throw new Exception(message); }

    public static int Main()
    {
        try
        {
            string path = Path.GetFullPath("installer/PastaGB-Instalar.exe");
            Check(AssemblyName.GetAssemblyName(path).Version == new Version(6, 0, 0, 0), "setup version must match this release");
            Assembly setup = Assembly.Load(File.ReadAllBytes(path));
            string[] names = setup.GetManifestResourceNames();
            Check(names.Length == 3 && names.Contains("payload-sha256.txt"), "only expected setup payload resources");
            string manifest;
            using (Stream stream = setup.GetManifestResourceStream("payload-sha256.txt"))
            using (var reader = new StreamReader(stream)) manifest = reader.ReadToEnd();
            string[] lines = manifest.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length == 2, "exactly two product payload entries");
            foreach (string name in new[] { "PastaGBIcones.exe", "PastaGBIcones.exe.config" })
            {
                byte[] payload;
                using (Stream stream = setup.GetManifestResourceStream(name))
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); payload = memory.ToArray(); }
                byte[] original = File.ReadAllBytes(Path.Combine("pasta-gb-icones", name));
                Check(payload.SequenceEqual(original), "setup embeds the exact validated " + name);
                string hash;
                using (SHA256 algorithm = SHA256.Create()) hash = BitConverter.ToString(algorithm.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
                Check(lines.Contains(hash + "  " + name), "setup integrity manifest matches " + name);
            }
            Console.WriteLine("PASS: " + count + " setup payload/version/integrity checks; installer not executed on Windows.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
