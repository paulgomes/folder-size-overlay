using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PastaGBSetup;

internal static class V60InstallerPayloadSmoke
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3) throw new ArgumentException("Usage: smoke <setup.exe> <PastaGBIcones.exe> <PastaGBIcones.exe.config>");
            string setup = Path.GetFullPath(args[0]);
            Assembly assembly = Assembly.LoadFile(setup);
            string[] names = assembly.GetManifestResourceNames();
            string[] expected = { Product.Executable, Product.Config, Product.PayloadManifest };
            if (names.Length != expected.Length || expected.Any(name => !names.Contains(name)))
                throw new InvalidDataException("Unexpected or missing embedded resources.");
            if (assembly.GetName().Version.ToString() != Product.Version) throw new InvalidDataException("Setup assembly version mismatch.");
            if (AssemblyName.GetAssemblyName(Path.GetFullPath(args[1])).Version.ToString() != Product.Version)
                throw new InvalidDataException("Product assembly version mismatch.");
            Dictionary<string, string> manifest;
            using (Stream stream = assembly.GetManifestResourceStream(Product.PayloadManifest))
                manifest = InstalledManifest.ParsePayload(SafeFiles.ReadText(stream));
            for (int i = 0; i < 2; i++)
            {
                string name = expected[i];
                string original = SafeFiles.Hash(Path.GetFullPath(args[i + 1]), Product.MaximumBytes(name));
                using (Stream stream = assembly.GetManifestResourceStream(name))
                    if (SafeFiles.Hash(stream, Product.MaximumBytes(name)) != original || manifest[name] != original)
                        throw new InvalidDataException("Embedded resource and manifest do not match final artifact: " + name);
                if (i == 0)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(name))
                        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("Embedded executable is not a PE image.");
                }
                Console.WriteLine(name + " SHA256=" + original);
            }
            Console.WriteLine("PASS: installer embeds exactly the supplied executable/config and matching manifest; managed resource inspection only, no Windows setup execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
