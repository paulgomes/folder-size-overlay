// Metadata-only, fail-closed directory resolution. Windows production uses
// volume + file IDs; the portable path-only implementation keeps the legacy
// no-link harness available, and never claims to support native Windows links.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PastaGB
{
    internal sealed class ResolvedDirectory
    {
        internal string SourcePath;
        internal string PhysicalPath;
        internal string Identity;
        internal string VolumeIdentity;
        internal bool HasLink;
        internal readonly List<string> Dependencies = new List<string>();
    }

    internal interface IDirectoryResolver
    {
        ResolvedDirectory Resolve(string path, ScanOptions options);
        bool IsSame(ResolvedDirectory directory, ScanOptions options);
    }

    internal sealed class DirectoryResolutionException : IOException
    {
        internal readonly bool SkippedLink;
        internal DirectoryResolutionException(string message, bool skipped) : base(message)
        { SkippedLink = skipped; }
    }

    internal static class DirectoryResolvers
    {
        internal static IDirectoryResolver Create()
        {
            return Path.DirectorySeparatorChar == '\\' ?
                (IDirectoryResolver)new WindowsDirectoryResolver() : new PortableDirectoryResolver();
        }

        internal static StringComparer PathComparer
        {
            get { return Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal; }
        }

        internal static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        internal static bool IsWithin(string child, string parent)
        {
            if (String.IsNullOrEmpty(child) || String.IsNullOrEmpty(parent)) return false;
            string left = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string right = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (PathComparer.Equals(left, right)) return true;
            return left.StartsWith(right + Path.DirectorySeparatorChar,
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    internal sealed class PortableDirectoryResolver : IDirectoryResolver
    {
        public ResolvedDirectory Resolve(string path, ScanOptions options)
        {
            string full = DirectoryResolvers.Normalize(path);
            DirectoryInfo info = new DirectoryInfo(full);
            info.Refresh();
            if ((int)info.Attributes == -1 || !info.Exists)
                throw new IOException("A pasta deixou de estar disponível.");
            // No platform-dependent link traversal in this fallback, including
            // a requested path whose ancestor is itself a symbolic link.
            for (DirectoryInfo current = info; current != null; current = current.Parent)
            {
                current.Refresh();
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new DirectoryResolutionException("A resolução segura de links locais requer o Windows.", true);
            }
            return new ResolvedDirectory
            {
                SourcePath = full, PhysicalPath = full,
                Identity = full, VolumeIdentity = Path.GetPathRoot(full)
            };
        }

        public bool IsSame(ResolvedDirectory directory, ScanOptions options)
        {
            try { return DirectoryResolvers.PathComparer.Equals(Resolve(directory.SourcePath, options).Identity, directory.Identity); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal sealed class WindowsDirectoryResolver : IDirectoryResolver
    {
        private const uint DirectoryAttribute = 0x10;
        private const uint ReparseAttribute = 0x400;
        private const uint OfflineAttribute = 0x1000;
        private const uint RecallOnOpenAttribute = 0x40000;
        private const uint RecallOnAccessAttribute = 0x400000;
        internal const uint SymbolicLinkTag = 0xA000000C;
        internal const uint MountPointTag = 0xA0000003;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;
        private const uint ReadAttributes = 0x80;
        private const uint OpenExisting = 3;
        private const uint ShareAll = 7;
        private const uint GetReparsePoint = 0x000900A8;
        private const int MaxLinkHops = 32;
        private const int MaxPathChars = 32767;

        public ResolvedDirectory Resolve(string path, ScanOptions options)
        {
            string source = DirectoryResolvers.Normalize(path);
            EnsureLocalDosPath(source);
            List<string> dependencies = new List<string>();
            HashSet<string> traversedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hadLink = false;
            string resolved = ResolvePath(source, options, traversedLinks, dependencies, ref hadLink, 0);
            // ResolvePath inspects each prefix using OPEN_REPARSE_POINT before
            // following a recognized tag. Unknown/cloud/network targets fail.
            using (SafeFileHandle handle = OpenMetadata(resolved))
            {
                NativeFileInformation info = ReadInformation(handle);
                if ((info.Attributes & ReparseAttribute) != 0)
                    throw new DirectoryResolutionException("A pasta mudou para um link durante a resolução.", true);
                RejectUnavailableAttributes(info.Attributes);
                string physical = GetFinalPath(handle, 0);
                if (physical.StartsWith(@"\\?\", StringComparison.Ordinal)) physical = physical.Substring(4);
                EnsureLocalDosPath(physical);
                string volumePath = GetFinalPath(handle, 1);
                int end = volumePath.IndexOf("}\\", StringComparison.Ordinal);
                if (!volumePath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
                    throw new IOException("A unidade não forneceu uma identidade de volume verificável.");
                string volume = volumePath.Substring(0, end + 2).ToUpperInvariant();
                string id = ReadFileId(handle, info, volumePath.Length == end + 2);
                ResolvedDirectory result = new ResolvedDirectory
                {
                    SourcePath = source, PhysicalPath = DirectoryResolvers.Normalize(physical),
                    VolumeIdentity = volume,
                    Identity = volume + "|" + id + "|" + info.CreationTime.High.ToString("X8") + info.CreationTime.Low.ToString("X8"),
                    HasLink = hadLink
                };
                foreach (string dependency in dependencies) AddDependency(result.Dependencies, dependency);
                if (hadLink) AddDependency(result.Dependencies, result.PhysicalPath);
                return result;
            }
        }

        public bool IsSame(ResolvedDirectory directory, ScanOptions options)
        {
            try
            {
                ResolvedDirectory now = Resolve(directory.SourcePath, options);
                return String.Equals(now.Identity, directory.Identity, StringComparison.Ordinal) &&
                    String.Equals(now.PhysicalPath, directory.PhysicalPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static string ResolvePath(string requested, ScanOptions options, HashSet<string> traversedLinks,
            List<string> dependencies, ref bool hadLink, int hops)
        {
            if (hops > MaxLinkHops)
                throw new DirectoryResolutionException("O limite de encadeamento de links locais foi atingido.", true);
            string path = NormalizeInternal(requested);
            string root = InternalRoot(path);
            EnsureLocalInternalRoot(root);
            string remainder = path.Substring(root.Length).Trim('\\');
            string[] parts = remainder.Length == 0 ? new string[0] : remainder.Split('\\');
            string current = root;
            // Root and every component are checked. No following an unknown
            // intermediate provider merely to ask the final handle its path.
            for (int index = -1; index < parts.Length; index++)
            {
                if (index >= 0) current = CombineWindows(current, parts[index]);
                using (SafeFileHandle handle = OpenMetadata(current))
                {
                    NativeFileInformation info = ReadInformation(handle);
                    if ((info.Attributes & DirectoryAttribute) == 0)
                        throw new IOException("O caminho deixou de ser um diretório.");
                    RejectUnavailableAttributes(info.Attributes);
                    if ((info.Attributes & ReparseAttribute) == 0) continue;
                    if (!options.FollowDirectoryLinks)
                        throw new DirectoryResolutionException("Links de diretório estão desativados nas preferências.", true);
                    byte[] data = new byte[16384];
                    uint returned;
                    if (!DeviceIoControl(handle, GetReparsePoint, IntPtr.Zero, 0, data, (uint)data.Length, out returned, IntPtr.Zero))
                        throw NativeFailure("Não foi possível identificar o tipo deste link");
                    string target;
                    bool relative;
                    if (!TryReadLinkTarget(data, (int)returned, out target, out relative))
                        throw new DirectoryResolutionException("Ponto de nova análise desconhecido, de nuvem ou não suportado; destino ignorado.", true);
                    if (!traversedLinks.Add(current))
                        throw new DirectoryResolutionException("Ciclo detectado na resolução dos links locais.", true);
                    hadLink = true;
                    string parent = index < 0 ? root : Path.GetDirectoryName(current);
                    string targetPath = NormalizeTarget(target, relative, parent);
                    if (!options.CrossVolumeLinks)
                    {
                        string sourceVolume = VolumeOfHandle(handle);
                        string targetResolved = ResolvePath(targetPath, options, traversedLinks, dependencies, ref hadLink, hops + 1);
                        using (SafeFileHandle targetHandle = OpenMetadata(targetResolved))
                            if (!String.Equals(sourceVolume, VolumeOfHandle(targetHandle), StringComparison.OrdinalIgnoreCase))
                                throw new DirectoryResolutionException("O link aponta para outra unidade e essa opção está desativada.", true);
                        targetPath = targetResolved;
                    }
                    // Track the alias itself, not its entire parent tree. The
                    // watcher can monitor this entry's rename/replacement at its
                    // parent while filtering by name, avoiding a dependency on
                    // every change anywhere under C:\. Final target is added
                    // after resolution. Raw GUID/device paths never escape here.
                    string aliasPath = GetFinalPath(handle, 0);
                    if (aliasPath.StartsWith(@"\\?\", StringComparison.Ordinal)) aliasPath = aliasPath.Substring(4);
                    EnsureLocalDosPath(aliasPath);
                    AddDependency(dependencies, aliasPath);
                    for (int next = index + 1; next < parts.Length; next++) targetPath = CombineWindows(targetPath, parts[next]);
                    return ResolvePath(targetPath, options, traversedLinks, dependencies, ref hadLink, hops + 1);
                }
            }
            return current;
        }

        // Pure parser is also exercised by portable tests: bounds are checked
        // before reading a target, and only two documented local tags pass.
        internal static bool TryReadLinkTarget(byte[] data, int count, out string target, out bool relative)
        {
            target = null; relative = false;
            if (data == null || count < 8 || count > data.Length) return false;
            uint tag = BitConverter.ToUInt32(data, 0);
            int payloadLength = BitConverter.ToUInt16(data, 4);
            if (payloadLength > count - 8) return false;
            int baseOffset;
            if (tag == SymbolicLinkTag)
            {
                if (payloadLength < 12 || count < 20) return false;
                uint flags = BitConverter.ToUInt32(data, 16);
                if ((flags & ~1U) != 0) return false;
                relative = (flags & 1) != 0; baseOffset = 20;
            }
            else if (tag == MountPointTag)
            {
                if (payloadLength < 8 || count < 16) return false;
                baseOffset = 16;
            }
            else return false;
            int offset = BitConverter.ToUInt16(data, 8);
            int length = BitConverter.ToUInt16(data, 10);
            int end = 8 + payloadLength;
            if ((offset & 1) != 0 || (length & 1) != 0 || length == 0 ||
                baseOffset + offset > end || length > end - baseOffset - offset) return false;
            target = Encoding.Unicode.GetString(data, baseOffset + offset, length);
            return target.IndexOf('\0') < 0;
        }

        private static string NormalizeTarget(string target, bool relative, string parent)
        {
            if (relative)
            {
                if (target.StartsWith("\\", StringComparison.Ordinal) || target.IndexOf(':') >= 0)
                    throw new DirectoryResolutionException("O link relativo tem um destino inválido.", true);
                return NormalizeInternal(CombineWindows(parent, target));
            }
            if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = @"\\?\" + target.Substring(4);
            return NormalizeInternal(target);
        }

        private static string NormalizeInternal(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > MaxPathChars || path.IndexOf('\0') >= 0)
                throw new DirectoryResolutionException("O destino do link é inválido ou excede o limite de caminho.", true);
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
            {
                string root = InternalRoot(path);
                // Resolve dot segments without turning a GUID path into a
                // network/device namespace accepted by Path.GetFullPath.
                List<string> parts = new List<string>();
                foreach (string part in path.Substring(root.Length).Split('\\'))
                {
                    if (part.Length == 0 || part == ".") continue;
                    if (part == "..") { if (parts.Count == 0) throw new IOException("Destino fora da raiz do volume."); parts.RemoveAt(parts.Count - 1); }
                    else { if (part.IndexOf(':') >= 0) throw new IOException("Destino de dispositivo não aceito."); parts.Add(part); }
                }
                return root + String.Join("\\", parts.ToArray());
            }
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            EnsureLocalDosPath(path);
            return DirectoryResolvers.Normalize(path);
        }

        private static string InternalRoot(string path)
        {
            if (path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
            {
                int end = path.IndexOf("}\\", StringComparison.Ordinal);
                Guid guid;
                if (end != 47 || !Guid.TryParse(path.Substring(11, 36), out guid))
                    throw new IOException("Identidade de volume inválida no destino do link.");
                return path.Substring(0, end + 2);
            }
            EnsureLocalDosPath(path);
            return path.Substring(0, 3);
        }

        private static bool IsDosPath(string path)
        { return path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\'; }

        private static void EnsureLocalDosPath(string path)
        {
            if (!IsDosPath(path) || path.IndexOf(':', 2) >= 0)
                throw new DirectoryResolutionException("Somente destinos locais com caminho de unidade verificável são aceitos; rede e dispositivos foram ignorados.", true);
            EnsureLocalInternalRoot(path.Substring(0, 3));
        }

        private static void EnsureLocalInternalRoot(string root)
        {
            uint type = GetDriveType(root);
            if (type != 2 && type != 3 && type != 5 && type != 6)
                throw new DirectoryResolutionException("O destino não é uma unidade local disponível.", true);
        }

        private static string CombineWindows(string parent, string child)
        { return parent.TrimEnd('\\') + "\\" + child; }

        private static void AddDependency(List<string> dependencies, string path)
        {
            if (!IsDosPath(path)) return;
            path = DirectoryResolvers.Normalize(path);
            foreach (string existing in dependencies)
                if (String.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
            if (dependencies.Count >= 64)
                throw new DirectoryResolutionException("O limite de destinos acompanhados foi atingido; link ignorado para preservar a validade da soma.", true);
            dependencies.Add(path);
        }

        private static void RejectUnavailableAttributes(uint attributes)
        {
            if ((attributes & DirectoryAttribute) == 0) throw new IOException("O item não é um diretório.");
            if ((attributes & (OfflineAttribute | RecallOnOpenAttribute | RecallOnAccessAttribute)) != 0)
                throw new DirectoryResolutionException("Conteúdo de nuvem ou offline não é percorrido por este leitor.", true);
        }

        private static SafeFileHandle OpenMetadata(string path)
        {
            string native = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
            SafeFileHandle handle = CreateFile(native, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); handle.Dispose();
                throw new IOException("Não foi possível abrir os metadados da pasta. " + new Win32Exception(error).Message);
            }
            return handle;
        }

        private static NativeFileInformation ReadInformation(SafeFileHandle handle)
        {
            NativeFileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) throw NativeFailure("Identidade do diretório indisponível");
            return info;
        }

        private static string ReadFileId(SafeFileHandle handle, NativeFileInformation info, bool isVolumeRoot)
        {
            FileIdInformation id;
            if (GetFileInformationByHandleEx(handle, 18, out id, 24))
            {
                if (id.Low == 0 && id.High == 0)
                {
                    // The volume's root is uniquely identified by the verified
                    // volume GUID even when a filesystem reports a zero root ID.
                    if (isVolumeRoot) return "VOLUME_ROOT";
                    throw new IOException("Identidade de arquivo nula; leitura ignorada.");
                }
                return id.Volume.ToString("X16") + "|" + id.High.ToString("X16") + id.Low.ToString("X16");
            }
            // ReFS's 64-bit ID is explicitly not guaranteed unique. Never use
            // that fallback when its 128-bit query failed (Microsoft file-ID
            // contract). Older local filesystems can use the verified 64-bit ID.
            StringBuilder format = new StringBuilder(64);
            uint serial, componentLength, flags;
            if (!GetVolumeInformationByHandle(handle, null, 0, out serial, out componentLength, out flags,
                format, (uint)format.Capacity))
                throw NativeFailure("Não foi possível validar o sistema de arquivos para a identidade de diretório");
            if (String.Equals(format.ToString(), "ReFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("O volume ReFS não forneceu a identidade de 128 bits necessária para uma soma segura.");
            if (info.FileIndexHigh == 0 && info.FileIndexLow == 0)
            {
                if (isVolumeRoot) return "VOLUME_ROOT";
                throw new IOException("O sistema de arquivos não fornece uma identidade verificável.");
            }
            return info.VolumeSerialNumber.ToString("X8") + "|" + info.FileIndexHigh.ToString("X8") + info.FileIndexLow.ToString("X8");
        }

        private static string VolumeOfHandle(SafeFileHandle handle)
        {
            string path = GetFinalPath(handle, 1);
            int end = path.IndexOf("}\\", StringComparison.Ordinal);
            if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
                throw new IOException("Não foi possível confirmar a unidade do link.");
            return path.Substring(0, end + 2);
        }

        private static string GetFinalPath(SafeFileHandle handle, uint flags)
        {
            StringBuilder buffer = new StringBuilder(512);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, flags);
            if (length == 0) throw NativeFailure("Não foi possível resolver um caminho local acompanhável");
            if (length >= buffer.Capacity)
            {
                if (length > MaxPathChars) throw new IOException("O caminho final excede o limite suportado.");
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, flags);
                if (length == 0 || length >= buffer.Capacity) throw NativeFailure("Não foi possível resolver o caminho final");
            }
            return buffer.ToString();
        }

        private static IOException NativeFailure(string message)
        { return new IOException(message + ". " + new Win32Exception(Marshal.GetLastWin32Error()).Message); }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeTime { internal uint Low; internal uint High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInformation
        {
            internal uint Attributes;
            internal NativeTime CreationTime, LastAccessTime, LastWriteTime;
            internal uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInformation { internal ulong Volume, Low, High; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdInformation info, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationByHandleW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, StringBuilder name, uint nameSize,
            out uint serial, out uint componentLength, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
        private static extern uint GetDriveType(string root);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputLength,
            [Out] byte[] output, uint outputLength, out uint bytesReturned, IntPtr overlapped);
    }
}
