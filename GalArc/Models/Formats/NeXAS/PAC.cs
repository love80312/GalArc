using CommunityToolkit.Mvvm.ComponentModel;
using GalArc.Infrastructure.Logging;
using GalArc.Infrastructure.Progress;
using GalArc.Models.Formats.Commons;
using GalArc.Models.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalArc.Models.Formats.NeXAS;

/// <summary>
/// NeXAS PAC archive reader/writer.
/// Supports the Huffman-index PAC format, the plain-index PAC format,
/// the older name-complemented format, and Zstandard method 7 archives.
/// </summary>
internal class PAC : ArcFormat, IUnpackConfigurable, IPackConfigurable
{
    public override string Name => "PAC";
    public override string Description => "NeXAS Archive";
    public override bool CanWrite => true;

    private NeXASPACUnpackOptions _unpackOptions;
    public ArcOptions UnpackOptions => _unpackOptions ??= new NeXASPACUnpackOptions();

    private NeXASPACPackOptions _packOptions;
    public ArcOptions PackOptions => _packOptions ??= new NeXASPACPackOptions();

    internal enum Method
    {
        None = 0,
        Lzss = 1,
        Huffman = 2,
        Zlib = 3,
        Zlib1 = 4,
        Zlib2 = 5,
        Zstd = 7
    }

    public static Array MethodValues => Enum.GetValues<Method>();

    private const string Magic = "PAC";
    private const int HeaderSize = 12;
    private const int IndexEntrySize = 76;
    private static readonly string[] NoCompressionExts = ["png", "fnt", "ogg"];

    public override void Unpack(string filePath, string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        using FileStream stream = File.OpenRead(filePath);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        if (stream.Length < 8 || Encoding.ASCII.GetString(ReadExactly(reader, 3)) != Magic)
        {
            throw new InvalidArchiveException();
        }

        Directory.CreateDirectory(folderPath);
        NeXASPACUnpackOptions unpackOptions = _unpackOptions ??= new NeXASPACUnpackOptions();
        Encoding selectedEncoding = unpackOptions.Encoding ?? NeXASEncodings.Default;
        Encoding scriptEncoding = unpackOptions.ScriptEncoding ?? NeXASEncodings.Default;

        // Byte 3 is reserved. Modern PACs store the entry count at byte 4.
        stream.Position = 4;
        uint countValue = reader.ReadUInt32();
        if (countValue == 0 || countValue >= 0x10000 || countValue > int.MaxValue)
        {
            UnpackLegacy(reader, folderPath, selectedEncoding, scriptEncoding);
            return;
        }

        int fileCount = (int)countValue;
        stream.Position = 8;
        int methodValue = reader.ReadInt32();
        Method method = (Method)methodValue;
        Logger.Info("NeXAS PAC: {0} entries, compression method {1}.", fileCount, methodValue);

        List<PackedEntry> entries = ReadIndex(reader, fileCount, selectedEncoding);
        WriteEntryOrder(folderPath, entries);
        ProgressManager.SetMax(entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            PackedEntry entry = entries[i];
            stream.Position = entry.Offset;
            byte[] packedData = ReadExactly(reader, checked((int)entry.Size));
            byte[] data = DecodeEntryData(packedData, entry, method);

            string outputPath = GetOutputPath(folderPath, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, data);
            TryExtractScript(folderPath, i, entry.Name, data, scriptEncoding);

            ProgressManager.Progress();
        }

        Logger.Info("NeXAS PAC extraction completed: {0} files.", entries.Count);
    }

    private List<PackedEntry> ReadIndex(BinaryReader reader, int fileCount, Encoding encoding)
    {
        Exception compressedIndexError = null;
        try
        {
            List<PackedEntry> entries = ReadHuffmanIndex(reader, fileCount, encoding);
            Logger.ShowVersion("pac", 2);
            return entries;
        }
        catch (Exception ex) when (IsIndexException(ex))
        {
            compressedIndexError = ex;
            Logger.Debug("NeXAS PAC: Huffman index candidate rejected: {0}", ex.Message);
        }

        try
        {
            List<PackedEntry> entries = ReadPlainIndex(reader, fileCount, encoding);
            Logger.ShowVersion("pac", 1);
            return entries;
        }
        catch (Exception ex) when (IsIndexException(ex))
        {
            Logger.Debug("NeXAS PAC: plain index candidate rejected: {0}", ex.Message);
            throw new InvalidArchiveException("NeXAS PAC index is invalid.", compressedIndexError ?? ex);
        }
    }

    private static bool IsIndexException(Exception ex)
    {
        return ex is InvalidArchiveException
            or InvalidDataException
            or EndOfStreamException
            or IOException
            or ArgumentException
            or OverflowException
            or IndexOutOfRangeException;
    }

    private static List<PackedEntry> ReadHuffmanIndex(BinaryReader reader, int fileCount, Encoding encoding)
    {
        Stream stream = reader.BaseStream;
        if (stream.Length < HeaderSize + 4)
        {
            throw new InvalidArchiveException();
        }

        stream.Position = stream.Length - 4;
        uint packedLengthValue = reader.ReadUInt32();
        if (packedLengthValue == 0 || packedLengthValue > int.MaxValue)
        {
            throw new InvalidArchiveException();
        }

        int packedLength = (int)packedLengthValue;
        long indexStart = stream.Length - 4L - packedLength;
        if (indexStart < HeaderSize)
        {
            throw new InvalidArchiveException();
        }

        stream.Position = indexStart;
        byte[] packedIndex = ReadExactly(reader, packedLength);
        for (int i = 0; i < packedIndex.Length; i++)
        {
            packedIndex[i] ^= 0xFF;
        }

        int unpackedLength = checked(fileCount * IndexEntrySize);
        byte[] index = DecodeHuffmanIndex(packedIndex, unpackedLength);
        return ParseIndex(index, fileCount, encoding, HeaderSize, indexStart);
    }

    private static List<PackedEntry> ReadPlainIndex(BinaryReader reader, int fileCount, Encoding encoding)
    {
        Stream stream = reader.BaseStream;
        int indexLength = checked(fileCount * IndexEntrySize);
        long dataStart = HeaderSize + (long)indexLength;
        if (dataStart > stream.Length)
        {
            throw new InvalidArchiveException();
        }

        stream.Position = HeaderSize;
        byte[] index = ReadExactly(reader, indexLength);
        return ParseIndex(index, fileCount, encoding, dataStart, stream.Length);
    }

    private static List<PackedEntry> ParseIndex(
        byte[] index,
        int fileCount,
        Encoding encoding,
        long dataStart,
        long dataEnd)
    {
        int expectedLength = checked(fileCount * IndexEntrySize);
        if (index.Length != expectedLength || dataStart < HeaderSize || dataEnd < dataStart)
        {
            throw new InvalidArchiveException();
        }

        List<PackedEntry> entries = new(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            int position = i * IndexEntrySize;
            int nameLength = 0;
            while (nameLength < 64 && index[position + nameLength] != 0)
            {
                nameLength++;
            }

            if (nameLength == 0)
            {
                throw new InvalidArchiveException("PAC contains an empty entry name.");
            }

            byte[] nameBytes = new byte[nameLength];
            Buffer.BlockCopy(index, position, nameBytes, 0, nameLength);
            string name = DecodeEntryName(nameBytes, encoding, i);
            string relativeName = MakeSafeRelativeName(name, nameBytes, i);

            uint offset = ReadUInt32(index, position + 64);
            uint unpackedSize = ReadUInt32(index, position + 68);
            uint packedSize = ReadUInt32(index, position + 72);

            if (unpackedSize > int.MaxValue || packedSize > int.MaxValue)
            {
                throw new InvalidArchiveException("PAC entry is too large for this application.");
            }

            ulong end = (ulong)offset + packedSize;
            if (offset < dataStart || offset > dataEnd || end > (ulong)dataEnd)
            {
                throw new InvalidArchiveException("PAC entry points outside the data section.");
            }

            entries.Add(new PackedEntry
            {
                Name = name,
                Path = relativeName,
                Offset = offset,
                UnpackedSize = unpackedSize,
                Size = packedSize
            });
        }

        return entries;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return (uint)(data[offset]
            | data[offset + 1] << 8
            | data[offset + 2] << 16
            | data[offset + 3] << 24);
    }

    private static string DecodeEntryName(byte[] nameBytes, Encoding preferredEncoding, int entryIndex)
    {
        List<Encoding> candidates = [preferredEncoding];
        foreach (Encoding candidate in NeXASEncodings.Supported)
        {
            if (!candidates.Exists(item => item.CodePage == candidate.CodePage))
            {
                candidates.Add(candidate);
            }
        }

        foreach (Encoding candidate in candidates)
        {
            try
            {
                Encoding strict = Encoding.GetEncoding(
                    candidate.CodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                string value = strict.GetString(nameBytes);
                if (!string.IsNullOrWhiteSpace(value) && !ContainsControlCharacters(value))
                {
                    return value;
                }
            }
            catch (DecoderFallbackException)
            {
                // Try the next known encoding. Some localized games use a different code page.
            }
        }

        return CreateFallbackName(nameBytes, entryIndex);
    }

    private static bool ContainsControlCharacters(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }
        return false;
    }

    private static string CreateFallbackName(byte[] rawName, int entryIndex)
    {
        string ascii = Encoding.ASCII.GetString(rawName);
        string extension = string.Empty;
        string[] knownExtensions = [".bin2", ".bin", ".grp", ".dat", ".ogg", ".png", ".fnt"];
        foreach (string candidate in knownExtensions)
        {
            if (ascii.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                extension = candidate;
                break;
            }
        }

        string prefix = ascii.StartsWith("script", StringComparison.OrdinalIgnoreCase)
            ? "script"
            : "entry";
        return $"{prefix}_{entryIndex:D4}{extension}";
    }

    private static string MakeSafeRelativeName(string decodedName, byte[] rawName, int entryIndex)
    {
        string normalized = decodedName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw new InvalidArchiveException("PAC contains an absolute file path.");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            normalized = CreateFallbackName(rawName, entryIndex);
            segments = [normalized];
        }

        List<string> safeSegments = new(segments.Length);
        const string invalidCharacters = "<>:\"|?*";
        foreach (string segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new InvalidArchiveException("PAC contains a path traversal entry.");
            }

            StringBuilder safe = new(segment.Length);
            foreach (char character in segment)
            {
                safe.Append(char.IsControl(character) || invalidCharacters.Contains(character) ? '_' : character);
            }

            string safeSegment = safe.ToString().TrimEnd(' ', '.');
            if (safeSegment.Length == 0)
            {
                safeSegment = $"entry_{entryIndex:D4}";
            }
            safeSegments.Add(safeSegment);
        }

        return Path.Combine(safeSegments.ToArray());
    }

    private static string GetOutputPath(string rootFolder, string relativeName)
    {
        string root = Path.GetFullPath(rootFolder);
        string fullPath = Path.GetFullPath(Path.Combine(root, relativeName));
        string rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidArchiveException("PAC entry escapes the output directory.");
        }

        return fullPath;
    }

    private byte[] DecodeEntryData(byte[] packedData, PackedEntry entry, Method method)
    {
        if (entry.UnpackedSize == entry.Size)
        {
            return packedData;
        }

        if (method == Method.None || !Enum.IsDefined(typeof(Method), method))
        {
            throw new InvalidArchiveException($"Unsupported PAC compression method: {(int)method}.");
        }

        Logger.Debug("Decompressing {0}: {1} -> {2} bytes using {3}.",
            entry.Name, entry.Size, entry.UnpackedSize, method);

        byte[] data = method switch
        {
            Method.Lzss => LzssHelper.Decompress(packedData),
            Method.Huffman => HuffmanHelper.Decode(packedData, checked((int)entry.UnpackedSize)),
            Method.Zlib or Method.Zlib1 or Method.Zlib2 => ZlibHelper.Decompress(packedData),
            Method.Zstd => Zstd.Decompress(packedData),
            _ => throw new InvalidArchiveException($"Unsupported PAC compression method: {(int)method}.")
        };

        if (data.Length != entry.UnpackedSize)
        {
            throw new InvalidArchiveException(
                $"Decompressed size mismatch for '{entry.Name}': expected {entry.UnpackedSize}, got {data.Length}.");
        }

        return data;
    }

    private static void TryExtractScript(
        string folderPath,
        int entryIndex,
        string entryName,
        byte[] data,
        Encoding textEncoding)
    {
        string extension = Path.GetExtension(entryName);
        if (!extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bin2", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string textRoot = Path.Combine(folderPath, "_text");
        string textPath = Path.Combine(textRoot, $"{entryIndex:D4}.txt");
        try
        {
            if (NeXASScriptReader.TryExtract(data, textPath, textEncoding, out int segmentCount, out string reason))
            {
                Logger.Info("Plain text exported: {0} ({1} segments).", textPath, segmentCount);
            }
            else
            {
                Logger.Info("Text export skipped: {0}. {1}", entryName, reason);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Logger.Error("Failed to export script text for {0}: {1}", entryName, ex.Message);
            Logger.Debug(ex.ToString());
        }
    }

    private static void UnpackLegacy(BinaryReader reader, string folderPath, Encoding encoding, Encoding scriptEncoding)
    {
        Stream stream = reader.BaseStream;
        stream.Position = 3;
        uint baseOffset = reader.ReadUInt32();
        if (baseOffset < 7 || baseOffset > stream.Length)
        {
            throw new InvalidArchiveException("Invalid legacy PAC data offset.");
        }

        List<Entry> entries = [];
        while (stream.Position < baseOffset)
        {
            List<byte> nameBytes = [];
            bool foundTerminator = false;
            while (stream.Position < baseOffset)
            {
                byte value = reader.ReadByte();
                if (value == 0)
                {
                    foundTerminator = true;
                    break;
                }
                nameBytes.Add((byte)~value);
            }

            if (!foundTerminator || nameBytes.Count == 0 || baseOffset - stream.Position < 8)
            {
                throw new InvalidArchiveException("Invalid legacy PAC index entry.");
            }

            byte[] rawName = nameBytes.ToArray();
            string name = DecodeEntryName(rawName, encoding, entries.Count);
            string relativeName = MakeSafeRelativeName(name, rawName, entries.Count);
            uint relativeOffset = reader.ReadUInt32();
            uint size = reader.ReadUInt32();
            ulong absoluteOffset = (ulong)baseOffset + relativeOffset;
            if (absoluteOffset > (ulong)stream.Length || absoluteOffset + size > (ulong)stream.Length)
            {
                throw new InvalidArchiveException("Legacy PAC entry points outside the archive.");
            }

            entries.Add(new Entry
            {
                Name = name,
                Path = relativeName,
                Offset = checked((uint)absoluteOffset),
                Size = size
            });
        }

        if (entries.Count == 0)
        {
            throw new InvalidArchiveException("Legacy PAC contains no entries.");
        }

        WriteEntryOrder(folderPath, entries);
        ProgressManager.SetMax(entries.Count);
        int entryIndex = 0;
        foreach (Entry entry in entries)
        {
            stream.Position = entry.Offset;
            byte[] data = ReadExactly(reader, checked((int)entry.Size));

            // The legacy variant stores the first three data bytes complemented.
            for (int i = 0; i < Math.Min(3, data.Length); i++)
            {
                data[i] = (byte)~data[i];
            }

            string outputPath = GetOutputPath(folderPath, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, data);
            TryExtractScript(folderPath, entryIndex, entry.Name, data, scriptEncoding);
            entryIndex++;
            ProgressManager.Progress();
        }

        Logger.ShowVersion("pac", "legacy");
        Logger.Info("Legacy NeXAS PAC extraction completed: {0} files.", entries.Count);
    }

    private static void WriteEntryOrder(string folderPath, IEnumerable<Entry> entries)
    {
        string textDirectory = Path.Combine(folderPath, "_text");
        Directory.CreateDirectory(textDirectory);
        string orderPath = Path.Combine(textDirectory, "_entries.lst");
        using StreamWriter writer = new(orderPath, false, new UTF8Encoding(false));
        int index = 0;
        foreach (Entry entry in entries)
        {
            writer.Write(index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            writer.Write('\t');
            writer.WriteLine(Path.GetFileName(entry.Path));
            index++;
        }
    }

    private static string[] GetOrderedFiles(string folderPath, out Dictionary<string, int> entryIndices)
    {
        string[] files = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
        string orderPath = Path.Combine(folderPath, "_text", "_entries.lst");
        entryIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(orderPath))
        {
            return files;
        }

        List<string> ordered = new(files.Length);
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(orderPath, new UTF8Encoding(false, true)))
        {
            int separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            if (!int.TryParse(
                line.AsSpan(0, separator),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int index))
            {
                continue;
            }

            string name = line[(separator + 1)..];
            entryIndices[name] = index;
            foreach (string file in files)
            {
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)
                    && used.Add(file))
                {
                    ordered.Add(file);
                    break;
                }
            }
        }

        // Newly added files are appended after original PAC entries and have no translation sidecar.
        int nextIndex = entryIndices.Count;
        foreach (string file in files)
        {
            if (used.Add(file))
            {
                ordered.Add(file);
                entryIndices[Path.GetFileName(file)] = nextIndex++;
            }
        }

        return ordered.ToArray();
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        if (count < 0)
        {
            throw new InvalidArchiveException();
        }

        byte[] data = reader.ReadBytes(count);
        if (data.Length != count)
        {
            throw new InvalidArchiveException("Unexpected end of PAC data.");
        }
        return data;
    }

    private sealed class HuffmanNode
    {
        public bool IsLeaf { get; init; }
        public byte Value { get; init; }
        public HuffmanNode Left { get; init; }
        public HuffmanNode Right { get; init; }
    }

    private sealed class HuffmanBitReader
    {
        private readonly byte[] _data;
        private int _bitPosition;

        public HuffmanBitReader(byte[] data)
        {
            _data = data;
        }

        public int ReadBit()
        {
            if (_bitPosition >= _data.Length * 8L)
            {
                throw new InvalidArchiveException("Huffman index ended unexpectedly.");
            }

            int bit = (_data[_bitPosition >> 3] >> (7 - (_bitPosition & 7))) & 1;
            _bitPosition++;
            return bit;
        }

        public byte ReadByte()
        {
            int value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 1) | ReadBit();
            }
            return (byte)value;
        }
    }

    private static byte[] DecodeHuffmanIndex(byte[] packedData, int decodedLength)
    {
        if (packedData.Length == 0 || decodedLength <= 0)
        {
            throw new InvalidArchiveException("Empty Huffman index.");
        }

        HuffmanBitReader bitReader = new(packedData);
        int internalNodeCount = 0;
        HuffmanNode root = ReadHuffmanNode(bitReader, ref internalNodeCount, 0);
        byte[] result = new byte[decodedLength];

        for (int i = 0; i < result.Length; i++)
        {
            HuffmanNode node = root;
            while (!node.IsLeaf)
            {
                node = bitReader.ReadBit() == 0 ? node.Left : node.Right;
                if (node == null)
                {
                    throw new InvalidArchiveException("Invalid Huffman tree.");
                }
            }
            result[i] = node.Value;
        }

        return result;
    }

    private static HuffmanNode ReadHuffmanNode(
        HuffmanBitReader reader,
        ref int internalNodeCount,
        int depth)
    {
        if (depth > 255)
        {
            throw new InvalidArchiveException("Huffman tree is too deep.");
        }

        if (reader.ReadBit() == 0)
        {
            return new HuffmanNode
            {
                IsLeaf = true,
                Value = reader.ReadByte()
            };
        }

        internalNodeCount++;
        if (internalNodeCount > 255)
        {
            throw new InvalidArchiveException("Huffman tree contains too many nodes.");
        }

        HuffmanNode left = ReadHuffmanNode(reader, ref internalNodeCount, depth + 1);
        HuffmanNode right = ReadHuffmanNode(reader, ref internalNodeCount, depth + 1);
        return new HuffmanNode
        {
            IsLeaf = false,
            Left = left,
            Right = right
        };
    }

    public override void Pack(string folderPath, string filePath)
    {
        NeXASPACPackOptions options = _packOptions ??= new NeXASPACPackOptions();
        Encoding sourceScriptEncoding = options.SourceScriptEncoding ?? NeXASEncodings.Default;
        Encoding translatedScriptEncoding = options.ScriptEncoding ?? NeXASEncodings.Default;
        string[] files = GetOrderedFiles(folderPath, out Dictionary<string, int> entryIndices);
        if (files.Length == 0 || files.Length >= 0x10000)
        {
            throw new InvalidArchiveException("PAC must contain between 1 and 65535 files.");
        }

        if (options.Version is not (1 or 2))
        {
            throw new InvalidArchiveException("Unsupported NeXAS PAC index version.");
        }

        using FileStream output = File.Create(filePath);
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write((byte)0);
        writer.Write((uint)files.Length);
        writer.Write((int)options.Method);

        int indexLength = checked(IndexEntrySize * files.Length);
        uint offset;
        if (options.Version == 1)
        {
            writer.BaseStream.Position += indexLength;
            offset = checked((uint)writer.BaseStream.Position);
        }
        else
        {
            offset = HeaderSize;
        }

        ProgressManager.SetMax(files.Length);
        List<PackedEntry> entries = new(files.Length);
        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            byte[] data = File.ReadAllBytes(file);

            string extension = Path.GetExtension(fileName);
            if (extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bin2", StringComparison.OrdinalIgnoreCase))
            {
                string textPath = entryIndices.TryGetValue(fileName, out int entryIndex)
                    ? Path.Combine(folderPath, "_text", $"{entryIndex:D4}.txt")
                    : string.Empty;
                if (!string.IsNullOrEmpty(textPath) && File.Exists(textPath))
                {
                    data = NeXASScriptWriter.ApplyTranslation(
                        data,
                        textPath,
                        sourceScriptEncoding,
                        translatedScriptEncoding,
                        out int changedSegments,
                        out int totalSegments);
                    Logger.Info("Translation applied: {0} ({1}/{2} text segments changed).",
                        fileName, changedSegments, totalSegments);
                }
            }

            uint unpackedSize = checked((uint)data.Length);

            if (!fileName.HasAnyOfExtensions(NoCompressionExts))
            {
                data = options.Method switch
                {
                    Method.Lzss => LzssHelper.Compress(data),
                    Method.Huffman => HuffmanHelper.Encode(data),
                    Method.Zlib or Method.Zlib1 or Method.Zlib2 => ZlibHelper.Compress(data),
                    Method.Zstd => Zstd.Compress(data),
                    _ => data
                };
            }

            writer.Write(data);
            uint packedSize = checked((uint)data.Length);
            entries.Add(new PackedEntry
            {
                Name = fileName,
                Offset = offset,
                UnpackedSize = unpackedSize,
                Size = packedSize
            });
            offset = checked(offset + packedSize);
            ProgressManager.Progress();
        }

        using MemoryStream indexStream = new();
        using (BinaryWriter indexWriter = new(indexStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (PackedEntry entry in entries)
            {
                indexWriter.WritePaddedString(entry.Name, 64, '\0', options.Encoding ?? NeXASEncodings.Default);
                indexWriter.Write(entry.Offset);
                indexWriter.Write(entry.UnpackedSize);
                indexWriter.Write(entry.Size);
            }
        }

        byte[] rawIndex = indexStream.ToArray();
        if (options.Version == 2)
        {
            byte[] packedIndex = HuffmanHelper.Encode(rawIndex);
            for (int i = 0; i < packedIndex.Length; i++)
            {
                packedIndex[i] ^= 0xFF;
            }
            writer.BaseStream.Position = writer.BaseStream.Length;
            writer.Write(packedIndex);
            writer.Write(packedIndex.Length);
        }
        else
        {
            writer.BaseStream.Position = HeaderSize;
            writer.Write(rawIndex);
        }

        Logger.Info("NeXAS PAC packed: {0} files, version {1}, method {2}.",
            files.Length, options.Version, options.Method);
    }
}

internal static class NeXASEncodings
{
    public static readonly IReadOnlyList<Encoding> Supported;
    public static readonly Encoding Default;
    public static readonly Encoding Chinese;

    static NeXASEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932);
        Chinese = Encoding.GetEncoding(936);
        Supported = [shiftJis, Chinese, Encoding.GetEncoding(950), Encoding.UTF8];
        Default = shiftJis;
    }
}

internal partial class NeXASPACUnpackOptions : ArcOptions
{
    [ObservableProperty]
    public partial IReadOnlyList<Encoding> Encodings { get; set; } = NeXASEncodings.Supported;

    // Encoding of PAC entry names in the index.
    [ObservableProperty]
    public partial Encoding Encoding { get; set; } = NeXASEncodings.Default;

    // Encoding used to decode script segments into the UTF-8 text files.
    [ObservableProperty]
    public partial Encoding ScriptEncoding { get; set; } = NeXASEncodings.Default;
}

internal partial class NeXASPACPackOptions : ArcOptions
{
    [ObservableProperty]
    public partial IReadOnlyList<Encoding> Encodings { get; set; } = NeXASEncodings.Supported;

    // Encoding of entry names written into the PAC index.
    [ObservableProperty]
    public partial Encoding Encoding { get; set; } = NeXASEncodings.Default;

    // Encoding used to decode the target BIN before comparing it with its TXT file.
    [ObservableProperty]
    public partial Encoding SourceScriptEncoding { get; set; } = NeXASEncodings.Default;

    // Encoding used for changed translation lines written back into BIN files.
    [ObservableProperty]
    public partial Encoding ScriptEncoding { get; set; } = NeXASEncodings.Chinese;

    [ObservableProperty]
    public partial int Version { get; set; } = 2;

    [ObservableProperty]
    public partial IReadOnlyList<int> Versions { get; set; } = [1, 2];

    // The supplied CNScript/JPScript samples use Zstandard method 7.
    [ObservableProperty]
    public partial PAC.Method Method { get; set; } = PAC.Method.Zstd;
}
