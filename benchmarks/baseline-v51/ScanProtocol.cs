// Private, bounded IPC for the scanner child. No object deserialization and no
// executable, command line or file contents are accepted by this protocol.
// Compatible with C# 5 / .NET Framework 4.8.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PastaGB
{
    internal enum ScanFrameKind { Request = 1, Cancel = 2, Progress = 3, Result = 4, Fault = 5 }

    internal sealed class ScanFrame
    {
        internal Guid RequestId;
        internal ScanFrameKind Kind;
        internal byte[] Payload;
    }

    internal sealed class ScanRequest
    {
        internal string Path;
        internal ScanOptions Options;
    }

    internal static class ScanProtocol
    {
        internal const int Version = 2;
        internal const int HeaderBytes = 32;
        internal const int MaxFrameBytes = 16 * 1024 * 1024;
        internal const int MaxStringBytes = 128 * 1024;
        private const int Magic = 0x35424750; // PGB5, little endian
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static ScanOptions CopyOptions(ScanOptions source)
        {
            source = source ?? new ScanOptions();
            return new ScanOptions
            {
                FollowDirectoryLinks = source.FollowDirectoryLinks,
                CrossVolumeLinks = source.CrossVolumeLinks,
                MaxDepth = Clamp(source.MaxDepth, 1, 512),
                MaxDirectories = Clamp(source.MaxDirectories, 1, 100000),
                MaxEntries = Clamp(source.MaxEntries, 1, 2000000),
                MaxSubtotals = Clamp(source.MaxSubtotals, 1, 4096),
                MaxEntriesPerSecond = Clamp(source.MaxEntriesPerSecond, 100, 50000)
            };
        }

        private static int Clamp(int n, int low, int high) { return Math.Max(low, Math.Min(high, n)); }

        internal static void WriteFrame(Stream stream, ScanFrameKind kind, Guid requestId, byte[] payload)
        {
            if (stream == null || payload == null) throw new ArgumentNullException();
            if (requestId == Guid.Empty || !KnownKind((int)kind) || payload.Length > MaxFrameBytes)
                throw new InvalidDataException("Envelope inválido do leitor.");
            byte[] header;
            using (MemoryStream memory = new MemoryStream(HeaderBytes))
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((int)kind);
                writer.Write(requestId.ToByteArray());
                writer.Write(payload.Length);
                header = memory.ToArray();
            }
            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        // Null means a clean EOF between frames. A truncated header/body is an
        // error, never an empty or successful folder result.
        internal static ScanFrame ReadFrame(Stream stream)
        {
            int first = stream.ReadByte();
            if (first < 0) return null;
            byte[] header = new byte[HeaderBytes];
            header[0] = (byte)first;
            ReadExact(stream, header, 1, header.Length - 1);
            using (BinaryReader reader = new BinaryReader(new MemoryStream(header, false)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                    throw new InvalidDataException("Versão de comunicação incompatível.");
                int kind = reader.ReadInt32();
                Guid id = new Guid(reader.ReadBytes(16));
                int length = reader.ReadInt32();
                if (!KnownKind(kind) || id == Guid.Empty || length < 0 || length > MaxFrameBytes)
                    throw new InvalidDataException("Envelope de comunicação fora dos limites.");
                byte[] payload = new byte[length];
                ReadExact(stream, payload, 0, length);
                return new ScanFrame { Kind = (ScanFrameKind)kind, RequestId = id, Payload = payload };
            }
        }

        private static bool KnownKind(int kind) { return kind >= 1 && kind <= 5; }
        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) throw new EndOfStreamException("Mensagem incompleta do leitor.");
                offset += n;
                count -= n;
            }
        }

        internal static byte[] Request(string path, ScanOptions options)
        {
            return Payload(delegate(BinaryWriter writer)
            {
                WriteString(writer, path);
                writer.Write(options.FollowDirectoryLinks);
                writer.Write(options.CrossVolumeLinks);
                writer.Write(options.MaxDepth);
                writer.Write(options.MaxDirectories);
                writer.Write(options.MaxEntries);
                writer.Write(options.MaxSubtotals);
                writer.Write(options.MaxEntriesPerSecond);
            });
        }

        internal static ScanRequest ReadRequest(byte[] payload)
        {
            using (BinaryReader reader = Reader(payload))
            {
                string path = ReadString(reader, false);
                ScanOptions options = new ScanOptions
                {
                    FollowDirectoryLinks = ReadBoolean(reader),
                    CrossVolumeLinks = ReadBoolean(reader),
                    MaxDepth = ReadBoundedInt(reader, 1, 512),
                    MaxDirectories = ReadBoundedInt(reader, 1, 100000),
                    MaxEntries = ReadBoundedInt(reader, 1, 2000000),
                    MaxSubtotals = ReadBoundedInt(reader, 1, 4096),
                    MaxEntriesPerSecond = ReadBoundedInt(reader, 100, 50000)
                };
                End(reader);
                if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathRooted(path))
                    throw new InvalidDataException("Pasta inválida no pedido de leitura.");
                return new ScanRequest { Path = path, Options = options };
            }
        }

        internal static byte[] Progress(long sequence, ScanProgress progress)
        {
            return Payload(delegate(BinaryWriter writer)
            {
                writer.Write(sequence);
                WriteString(writer, progress.RootPath);
                WriteString(writer, progress.CurrentPath);
                writer.Write(progress.TotalBytes);
                writer.Write(progress.FileCount);
                writer.Write(progress.FolderCount);
                writer.Write(progress.ErrorCount);
                writer.Write(progress.SkippedLinks);
                writer.Write(progress.StartedUtc.Ticks);
                writer.Write(progress.CancelRequested);
            });
        }

        internal static ScanProgress ReadProgress(byte[] payload, out long sequence)
        {
            using (BinaryReader reader = Reader(payload))
            {
                sequence = ReadCount(reader);
                if (sequence == 0) throw new InvalidDataException("Sequência inválida do leitor.");
                ScanProgress progress = new ScanProgress
                {
                    RootPath = ReadString(reader, false),
                    CurrentPath = ReadString(reader, false),
                    TotalBytes = ReadCount(reader),
                    FileCount = ReadCount(reader),
                    FolderCount = ReadCount(reader),
                    ErrorCount = ReadCount(reader),
                    SkippedLinks = ReadCount(reader),
                    StartedUtc = ReadUtc(reader),
                    CancelRequested = ReadBoolean(reader)
                };
                End(reader);
                return progress;
            }
        }

        internal static byte[] Result(ScanResult result)
        {
            // Aggregate sizes and dependency paths are always transferred in
            // full. Optional detail lists have independent byte budgets so a
            // tree of long names cannot discard an otherwise valid total.
            bool omitted = false;
            int omittedIssues;
            byte[] children = FolderList(result.Children, 4097, 1024 * 1024, ref omitted);
            byte[] subtotals = FolderList(result.Subtotals, 4096, 4 * 1024 * 1024, ref omitted);
            byte[] files = FileList(result.TopFiles, ref omitted);
            byte[] issues = IssueList(result.Issues, out omittedIssues, ref omitted);
            return Payload(delegate(BinaryWriter writer)
            {
                WriteString(writer, result.RootPath);
                writer.Write(result.TotalBytes);
                writer.Write(result.FileCount);
                writer.Write(result.FolderCount);
                writer.Write(result.ErrorCount);
                writer.Write(result.SkippedLinks);
                writer.Write(SaturatingAdd(result.OmittedIssueCount, omittedIssues));
                writer.Write(result.StartedUtc.Ticks);
                writer.Write(result.EndedUtc.Ticks);
                writer.Write(result.Cancelled);
                WriteString(writer, ShortMessage(result.FatalError));
                int count = result.DependencyPaths == null ? 0 : result.DependencyPaths.Count;
                if (count > 64) throw new InvalidDataException("Dependências acima do limite do leitor.");
                writer.Write(count);
                if (result.DependencyPaths != null)
                    foreach (string dependency in result.DependencyPaths) WriteString(writer, dependency);
                writer.Write(children);
                writer.Write(files);
                writer.Write(issues);
                writer.Write(subtotals);
                writer.Write(omitted);
            });
        }

        internal static ScanResult ReadResult(byte[] payload, out bool detailsOmitted)
        {
            using (BinaryReader reader = Reader(payload))
            {
                ScanResult result = new ScanResult
                {
                    RootPath = ReadString(reader, false),
                    TotalBytes = ReadCount(reader),
                    FileCount = ReadCount(reader),
                    FolderCount = ReadCount(reader),
                    ErrorCount = ReadCount(reader),
                    SkippedLinks = ReadCount(reader),
                    OmittedIssueCount = ReadCount(reader),
                    StartedUtc = ReadUtc(reader),
                    EndedUtc = ReadUtc(reader),
                    Cancelled = ReadBoolean(reader),
                    FatalError = ReadString(reader, true),
                    DependencyPaths = new List<string>()
                };
                int count = ReadBoundedInt(reader, 0, 64);
                for (int i = 0; i < count; i++) result.DependencyPaths.Add(ReadString(reader, false));
                result.Children = ReadFolderList(reader, 4097);
                result.TopFiles = new List<FileSummary>();
                count = ReadBoundedInt(reader, 0, 100);
                for (int i = 0; i < count; i++)
                    result.TopFiles.Add(new FileSummary
                    {
                        Name = ReadString(reader, false), Path = ReadString(reader, false), Bytes = ReadCount(reader)
                    });
                result.Issues = new List<ScanIssue>();
                count = ReadBoundedInt(reader, 0, 200);
                for (int i = 0; i < count; i++)
                    result.Issues.Add(new ScanIssue
                    {
                        Path = ReadString(reader, false), Message = ReadString(reader, false), IsSkippedLink = ReadBoolean(reader)
                    });
                result.Subtotals = ReadFolderList(reader, 4096);
                detailsOmitted = ReadBoolean(reader);
                End(reader);
                return result;
            }
        }

        private static byte[] FolderList(List<FolderSummary> list, int countLimit, int byteLimit, ref bool omitted)
        {
            int dropped;
            byte[] value = DetailList(list, countLimit, byteLimit, delegate(BinaryWriter writer, FolderSummary item)
            {
                WriteString(writer, item.Name);
                WriteString(writer, item.Path);
                writer.Write(item.Bytes);
                writer.Write(item.FileCount);
                writer.Write(item.FolderCount);
                writer.Write(item.ErrorCount);
                writer.Write(item.SkippedLinks);
                writer.Write(item.IsDirectFiles);
            }, out dropped);
            omitted |= dropped != 0;
            return value;
        }

        private static List<FolderSummary> ReadFolderList(BinaryReader reader, int limit)
        {
            int count = ReadBoundedInt(reader, 0, limit);
            List<FolderSummary> list = new List<FolderSummary>(count);
            for (int i = 0; i < count; i++)
                list.Add(new FolderSummary
                {
                    Name = ReadString(reader, false),
                    Path = ReadString(reader, false),
                    Bytes = ReadCount(reader),
                    FileCount = ReadCount(reader),
                    FolderCount = ReadCount(reader),
                    ErrorCount = ReadCount(reader),
                    SkippedLinks = ReadCount(reader),
                    IsDirectFiles = ReadBoolean(reader)
                });
            return list;
        }

        private static byte[] FileList(List<FileSummary> list, ref bool omitted)
        {
            int dropped;
            byte[] value = DetailList(list, 100, 512 * 1024, delegate(BinaryWriter writer, FileSummary item)
            {
                WriteString(writer, item.Name); WriteString(writer, item.Path); writer.Write(item.Bytes);
            }, out dropped);
            omitted |= dropped != 0;
            return value;
        }

        private static byte[] IssueList(List<ScanIssue> list, out int dropped, ref bool omitted)
        {
            byte[] value = DetailList(list, 200, 512 * 1024, delegate(BinaryWriter writer, ScanIssue item)
            {
                WriteString(writer, item.Path); WriteString(writer, ShortMessage(item.Message) ?? String.Empty); writer.Write(item.IsSkippedLink);
            }, out dropped);
            omitted |= dropped != 0;
            return value;
        }

        private static byte[] DetailList<T>(List<T> list, int countLimit, int byteLimit, Action<BinaryWriter, T> writeItem, out int dropped)
        {
            int written = 0;
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                writer.Write(0); // Replaced after the byte/count budget is known.
                if (list != null)
                    foreach (T item in list)
                    {
                        if (written == countLimit) break;
                        byte[] bytes = Payload(delegate(BinaryWriter element) { writeItem(element, item); });
                        if (memory.Length + bytes.Length > byteLimit) break;
                        writer.Write(bytes);
                        written++;
                    }
                memory.Position = 0;
                writer.Write(written);
                dropped = list == null ? 0 : list.Count - written;
                return memory.ToArray();
            }
        }

        internal static byte[] Fault(string message)
        {
            return Payload(delegate(BinaryWriter writer) { WriteString(writer, ShortMessage(message) ?? "Falha no leitor."); });
        }

        internal static string ReadFault(byte[] payload)
        {
            using (BinaryReader reader = Reader(payload))
            {
                string message = ReadString(reader, false);
                End(reader);
                return ShortMessage(message);
            }
        }

        internal static string ShortMessage(string value)
        {
            if (value == null || value.Length <= 2048) return value;
            // Do not cut a UTF-16 surrogate pair before strict UTF-8 encoding.
            int count = Char.IsHighSurrogate(value[2047]) ? 2047 : 2048;
            return value.Substring(0, count);
        }

        private static long SaturatingAdd(long value, int count)
        {
            return value > Int64.MaxValue - count ? Int64.MaxValue : value + count;
        }

        private static byte[] Payload(Action<BinaryWriter> write)
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                write(writer);
                if (memory.Length > MaxFrameBytes) throw new InvalidDataException("Mensagem do leitor excedeu o limite.");
                return memory.ToArray();
            }
        }

        private static BinaryReader Reader(byte[] payload)
        {
            if (payload == null || payload.Length > MaxFrameBytes) throw new InvalidDataException("Mensagem inválida do leitor.");
            return new BinaryReader(new MemoryStream(payload, false));
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (value == null) { writer.Write(-1); return; }
            int count = Utf8.GetByteCount(value);
            if (count > MaxStringBytes) throw new InvalidDataException("Texto do leitor excedeu o limite.");
            byte[] bytes = Utf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader, bool nullable)
        {
            int count = reader.ReadInt32();
            if (count == -1 && nullable) return null;
            if (count < 0 || count > MaxStringBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Comprimento de texto inválido.");
            return Utf8.GetString(reader.ReadBytes(count));
        }

        private static bool ReadBoolean(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new InvalidDataException("Indicador inválido do leitor.");
            return value != 0;
        }

        private static int ReadBoundedInt(BinaryReader reader, int minimum, int maximum)
        {
            int value = reader.ReadInt32();
            if (value < minimum || value > maximum) throw new InvalidDataException("Contagem fora dos limites do leitor.");
            return value;
        }

        private static long ReadCount(BinaryReader reader)
        {
            long value = reader.ReadInt64();
            if (value < 0) throw new InvalidDataException("Contagem negativa do leitor.");
            return value;
        }

        private static DateTime ReadUtc(BinaryReader reader)
        {
            long ticks = reader.ReadInt64();
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Data inválida do leitor.");
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        private static void End(BinaryReader reader)
        {
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Dados inesperados após mensagem do leitor.");
        }
    }
}
