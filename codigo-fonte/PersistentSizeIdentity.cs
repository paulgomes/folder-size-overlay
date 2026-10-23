using System;
using System.Globalization;
using PastaGB;

namespace PastaGBIcons
{
    // This token is evidence of object identity, never of current folder size.
    // It is accepted only from a complete scan which revalidated its root.
    internal sealed class PersistentIdentityToken
    {
        internal readonly string Path, Identity, VolumeIdentity, PhysicalPath;

        private PersistentIdentityToken(string path, string identity, string volume, string physical)
        { Path = path; Identity = identity; VolumeIdentity = volume; PhysicalPath = physical; }

        internal static bool TryCreate(string path, string identity, string volumeIdentity,
            string physicalPath, bool validated, out PersistentIdentityToken token)
        {
            token = null;
            string normalized, physical;
            if (!validated || !PersistentCachePaths.TryNormalize(path, out normalized) ||
                !PersistentCachePaths.TryNormalize(physicalPath, out physical) ||
                String.IsNullOrEmpty(volumeIdentity) || String.IsNullOrEmpty(identity) || identity.Length > 256)
                return false;
            const string prefix = @"\\?\Volume{";
            if (volumeIdentity.Length != prefix.Length + 38 ||
                !volumeIdentity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !volumeIdentity.EndsWith(@"}\", StringComparison.Ordinal)) return false;
            Guid volume;
            if (!Guid.TryParseExact(volumeIdentity.Substring(prefix.Length, 36), "D", out volume) || volume == Guid.Empty)
                return false;
            string canonicalVolume = (prefix + volume.ToString("D") + @"}\").ToUpperInvariant();
            if (!identity.StartsWith(volumeIdentity + "|", StringComparison.OrdinalIgnoreCase)) return false;
            string suffix = identity.Substring(volumeIdentity.Length + 1);
            string[] parts = suffix.Split('|');
            bool validRoot = parts.Length == 2 && parts[0] == "VOLUME_ROOT" && Hex(parts[1], 16);
            bool validFile = parts.Length == 3 &&
                ((Hex(parts[0], 16) && Hex(parts[1], 32)) || (Hex(parts[0], 8) && Hex(parts[1], 16))) &&
                !OnlyZero(parts[1]) && Hex(parts[2], 16);
            if (!validRoot && !validFile) return false;
            if (validRoot && physical.Length != 3) return false;
            token = new PersistentIdentityToken(normalized, canonicalVolume + "|" + suffix.ToUpperInvariant(),
                canonicalVolume, physical);
            return true;
        }

        internal bool SameObject(PersistentIdentityToken other)
        {
            return other != null && String.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Identity, other.Identity, StringComparison.Ordinal) &&
                String.Equals(VolumeIdentity, other.VolumeIdentity, StringComparison.Ordinal) &&
                String.Equals(PhysicalPath, other.PhysicalPath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool OnlyZero(string value)
        { for (int i = 0; i < value.Length; i++) if (value[i] != '0') return false; return true; }

        private static bool Hex(string value, int length)
        {
            if (value.Length != length) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }

    internal static class PersistentCachePaths
    {
        internal const int MaximumPathCharacters = 2048;

        // Deliberately narrower than the scanner. Ambiguous/device/remote paths
        // miss the optional cache and continue through the normal reader.
        internal static bool TryNormalize(string path, out string normalized)
        {
            normalized = null;
            if (String.IsNullOrEmpty(path) || path.Length < 3 || path.Length > MaximumPathCharacters ||
                !Letter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return false;
            string value = path.Replace('/', '\\');
            while (value.Length > 3 && value[value.Length - 1] == '\\') value = value.Substring(0, value.Length - 1);
            int component = 3;
            for (int i = 3; i <= value.Length; i++)
            {
                if (i == value.Length || value[i] == '\\')
                {
                    int length = i - component;
                    if (length == 0 && value.Length != 3) return false;
                    if (length > 0 && (value[i - 1] == '.' || value[i - 1] == ' ')) return false;
                    if (length > 0)
                    {
                        string part = value.Substring(component, length);
                        int dot = part.IndexOf('.');
                        string basename = (dot < 0 ? part : part.Substring(0, dot)).ToUpperInvariant();
                        if (basename == "CON" || basename == "PRN" || basename == "AUX" || basename == "NUL" ||
                            (basename.Length == 4 && (basename.StartsWith("COM", StringComparison.Ordinal) ||
                                basename.StartsWith("LPT", StringComparison.Ordinal)) && basename[3] >= '1' && basename[3] <= '9'))
                            return false;
                    }
                    component = i + 1;
                    continue;
                }
                char c = value[i];
                if (c < 32 || c == ':' || c == '*' || c == '?' || c == '<' || c == '>' || c == '|' || c == '"') return false;
                if (Char.IsHighSurrogate(c))
                { if (i + 1 >= value.Length || !Char.IsLowSurrogate(value[++i])) return false; }
                else if (Char.IsLowSurrogate(c)) return false;
            }
            normalized = Char.ToUpperInvariant(value[0]) + value.Substring(1);
            return true;
        }

        private static bool Letter(char value)
        { return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z'); }
    }

    internal interface IPersistentDirectoryIdentityProvider
    {
        PersistentIdentityToken Resolve(string path, bool followDirectoryLinks, bool crossVolumeLinks);
    }

    internal sealed class PersistentWindowsIdentityProvider : IPersistentDirectoryIdentityProvider
    {
        public PersistentIdentityToken Resolve(string path, bool followDirectoryLinks, bool crossVolumeLinks)
        {
            // Never let the portable path-only resolver count as stable identity.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            var resolver = new WindowsDirectoryResolver();
            ResolvedDirectory directory = resolver.Resolve(path, new ScanOptions
            { FollowDirectoryLinks = followDirectoryLinks, CrossVolumeLinks = crossVolumeLinks });
            if (directory == null || directory.HasLink || directory.Dependencies.Count != 0) return null;
            PersistentIdentityToken token;
            return PersistentIdentityToken.TryCreate(path, directory.Identity, directory.VolumeIdentity,
                directory.PhysicalPath, true, out token) ? token : null;
        }
    }
}
