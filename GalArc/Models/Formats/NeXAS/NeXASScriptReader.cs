using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalArc.Models.Formats.NeXAS;

internal enum NeXASScriptFormat
{
    NullSeparatedTextTable,
    BinV2
}

internal sealed class NeXASScriptSegment
{
    public int Index { get; init; }
    public byte[] OriginalBytes { get; init; }
    public int FollowingNulls { get; init; }
    public bool IsNotText { get; init; }
    public string Text { get; init; }

    // BINv2 mapping. These values are ignored by the original text-table format.
    public int BlockIndex { get; init; } = -1;
    public int StringIndex { get; init; } = -1;
}

internal sealed class NeXASBinV2Block
{
    public int Offset { get; init; }
    public int EndOffset { get; init; }

    // Everything from the block start through the VM array, excluding the
    // four-byte count of the primary Strings array.
    public byte[] PrefixBytes { get; init; }

    // The two non-primary string arrays, including their counts and terminators.
    public byte[] SuffixBytes { get; init; }

    public IReadOnlyList<NeXASScriptSegment> Segments { get; init; }
}

internal sealed class NeXASScriptDocument
{
    public NeXASScriptFormat Format { get; init; } = NeXASScriptFormat.NullSeparatedTextTable;
    public int FirstTextOffset { get; init; }
    public int TailOffset { get; init; }
    public IReadOnlyList<NeXASScriptSegment> Segments { get; init; }
    public IReadOnlyList<NeXASBinV2Block> BinV2Blocks { get; init; }
}

/// <summary>
/// Reads the two script layouts encountered in NeXAS BIN files:
/// the NUL-separated text table used by zbspac and the BINv2 structure
/// (version byte + VM array + three counted CString arrays).
/// </summary>
internal static class NeXASScriptReader
{
    private const uint MaxArrayCount = 1_000_000;
    private const int BinV2HeaderSize = 8;
    private const int BinV2MinimumVmCount = 3;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    public static bool TryExtract(
        byte[] data,
        string textFilePath,
        Encoding textEncoding,
        out int segmentCount)
    {
        return TryExtract(data, textFilePath, textEncoding, out segmentCount, out _);
    }

    public static bool TryExtract(
        byte[] data,
        string textFilePath,
        Encoding textEncoding,
        out int segmentCount,
        out string reason)
    {
        segmentCount = 0;
        if (!TryParse(data, textEncoding, out NeXASScriptDocument document, out reason))
        {
            return false;
        }

        StringBuilder output = new();
        foreach (NeXASScriptSegment segment in document.Segments)
        {
            output.Append(NeXASScriptWriter.EscapeText(segment.Text));
            output.Append("\r\n");
        }

        string directory = Path.GetDirectoryName(textFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(textFilePath, output.ToString(), Utf8NoBom);
        segmentCount = document.Segments.Count;
        return true;
    }

    internal static bool TryParse(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document)
    {
        return TryParse(data, textEncoding, out document, out _);
    }

    internal static bool TryParse(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document,
        out string reason)
    {
        document = null;
        reason = null;
        if (data == null || data.Length < 16 || textEncoding == null)
        {
            reason = "BIN is null, too small, or no text encoding was selected.";
            return false;
        }

        // BINv2 stores two UInt32 fields here. Reading them as one UInt64
        // produces misleading values such as 0x000008FC00000000 (2300 << 32).
        if (TryParseBinV2(data, textEncoding, out document, out reason))
        {
            return true;
        }

        // If the file begins with a plausible BINv2 header but its arrays are
        // malformed, do not reinterpret the same bytes as the older text table.
        if (LooksLikeBinV2Header(data))
        {
            return false;
        }

        return TryParseNullSeparatedTable(data, textEncoding, out document, out reason);
    }

    private static bool TryParseBinV2(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document,
        out string reason)
    {
        document = null;
        reason = null;

        // The upstream dispatcher selects the BINv2 engine when the first
        // DWORD is zero. Avoid scanning unrelated legacy BIN payloads.
        if (!LooksLikeBinV2Header(data))
        {
            return false;
        }

        List<NeXASBinV2Block> blocks = [];
        List<NeXASScriptSegment> allSegments = [];
        int position = 0;

        // BINv2 is a structure embedded in the script binary. The reference
        // implementation scans byte-by-byte for structurally valid blocks;
        // after a valid block, continue at its exact end to avoid overlaps.
        while (position <= data.Length - BinV2HeaderSize)
        {
            if (TryReadBinV2Block(data, position, textEncoding,
                allSegments.Count, blocks.Count,
                out NeXASBinV2Block block,
                out List<NeXASScriptSegment> blockSegments))
            {
                blocks.Add(block);
                allSegments.AddRange(blockSegments);
                position = block.EndOffset;
                continue;
            }

            position++;
        }

        if (blocks.Count == 0 || allSegments.Count == 0)
        {
            if (LooksLikeBinV2Header(data))
            {
                reason = "BINv2 header detected, but its VM or CString arrays failed structural validation.";
            }
            return false;
        }

        document = new NeXASScriptDocument
        {
            Format = NeXASScriptFormat.BinV2,
            FirstTextOffset = 0,
            TailOffset = data.Length,
            Segments = allSegments,
            BinV2Blocks = blocks
        };
        reason = $"Parsed BINv2: {blocks.Count} block(s), {allSegments.Count} primary strings.";
        return true;
    }

    private static bool TryReadBinV2Block(
        byte[] data,
        int offset,
        Encoding textEncoding,
        int firstSegmentIndex,
        int blockIndex,
        out NeXASBinV2Block block,
        out List<NeXASScriptSegment> segments)
    {
        block = null;
        segments = null;

        if (offset < 0 || offset > data.Length - BinV2HeaderSize)
        {
            return false;
        }

        uint version = ReadUInt32LittleEndian(data, offset);
        if ((version & 0xFFFFFF00u) != 0)
        {
            return false;
        }

        uint vmCount = ReadUInt32LittleEndian(data, offset + 4);
        if (vmCount < BinV2MinimumVmCount || vmCount > MaxArrayCount)
        {
            return false;
        }

        long primaryCountOffset64 = (long)offset + BinV2HeaderSize + (long)vmCount * 8;
        if (primaryCountOffset64 > data.Length - 4)
        {
            return false;
        }

        int primaryCountOffset = (int)primaryCountOffset64;
        uint primaryCount = ReadUInt32LittleEndian(data, primaryCountOffset);
        if (primaryCount == 0 || primaryCount > MaxArrayCount)
        {
            return false;
        }

        // Match the upstream BINv2 signature check. The UInt64 spans the last
        // DWORD of the VM array and the primary string count.
        int probeOffset = primaryCountOffset - 4;
        ulong probe = ReadUInt64LittleEndian(data, probeOffset);
        if ((probe & 0xFF000000FF000000UL) != 0 || (probe >> 32) == 0)
        {
            return false;
        }

        int firstStringOffset = primaryCountOffset + 4;
        int printableBytes = 0;
        // The original detector was written for CP932 and uses 0xEF as its
        // upper bound. CP936, CP950 and UTF-8 legitimately use higher bytes.
        byte upperBound = textEncoding.CodePage == 932 ? (byte)0xEF : (byte)0xFF;
        for (int i = 0; i < 10 && firstStringOffset + i < data.Length; i++)
        {
            byte value = data[firstStringOffset + i];
            if (value > 0x20 && value < upperBound)
            {
                printableBytes++;
            }
        }
        if (printableBytes < 7)
        {
            return false;
        }

        int vmArrayEnd = primaryCountOffset;
        int cursor = firstStringOffset;
        List<NeXASScriptSegment> parsedSegments = new(checked((int)primaryCount));
        for (int i = 0; i < primaryCount; i++)
        {
            int terminator = Array.IndexOf(data, (byte)0, cursor);
            if (terminator < 0)
            {
                return false;
            }

            int length = terminator - cursor;
            byte[] raw = new byte[length];
            if (length > 0)
            {
                Buffer.BlockCopy(data, cursor, raw, 0, length);
            }

            string text;
            try
            {
                text = textEncoding.GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            parsedSegments.Add(new NeXASScriptSegment
            {
                Index = firstSegmentIndex + i,
                OriginalBytes = raw,
                FollowingNulls = 1,
                IsNotText = false,
                Text = text,
                BlockIndex = blockIndex,
                StringIndex = i
            });

            cursor = terminator + 1;
        }

        int primaryStringsEnd = cursor;
        if (!TrySkipCStringArray(data, ref cursor) || !TrySkipCStringArray(data, ref cursor))
        {
            return false;
        }

        int endOffset = cursor;
        if (endOffset <= offset || endOffset > data.Length)
        {
            return false;
        }

        byte[] prefix = new byte[vmArrayEnd - offset];
        Buffer.BlockCopy(data, offset, prefix, 0, prefix.Length);

        byte[] suffix = new byte[endOffset - primaryStringsEnd];
        Buffer.BlockCopy(data, primaryStringsEnd, suffix, 0, suffix.Length);

        block = new NeXASBinV2Block
        {
            Offset = offset,
            EndOffset = endOffset,
            PrefixBytes = prefix,
            SuffixBytes = suffix,
            Segments = parsedSegments
        };
        segments = parsedSegments;
        return true;
    }

    private static bool TrySkipCStringArray(byte[] data, ref int position)
    {
        if (position < 0 || position > data.Length - 4)
        {
            return false;
        }

        uint count = ReadUInt32LittleEndian(data, position);
        position += 4;
        if (count > MaxArrayCount || count > data.Length - position)
        {
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            int terminator = Array.IndexOf(data, (byte)0, position);
            if (terminator < 0)
            {
                return false;
            }
            position = terminator + 1;
        }

        return true;
    }

    private static bool LooksLikeBinV2Header(byte[] data)
    {
        if (data == null || data.Length < BinV2HeaderSize)
        {
            return false;
        }

        uint version = ReadUInt32LittleEndian(data, 0);
        uint vmCount = ReadUInt32LittleEndian(data, 4);
        if (version != 0 || vmCount < BinV2MinimumVmCount)
        {
            return false;
        }

        long primaryCountOffset = (long)BinV2HeaderSize + (long)vmCount * 8;
        return primaryCountOffset <= data.Length - 4;
    }

    private static bool TryParseNullSeparatedTable(
        byte[] data,
        Encoding textEncoding,
        out NeXASScriptDocument document,
        out string reason)
    {
        document = null;
        reason = null;

        // Original NeXAS text-table header: the first UInt64 is n;
        // the NUL-separated text area begins at (n + 1) * 8.
        ulong headerValue = ReadUInt64LittleEndian(data, 0);
        if (headerValue >= (ulong)data.Length / 8)
        {
            reason = $"Header value {headerValue} points outside the BIN length {data.Length}.";
            return false;
        }

        ulong textOffsetValue = (headerValue + 1) * 8;
        if (textOffsetValue >= (ulong)data.Length || textOffsetValue > int.MaxValue)
        {
            reason = $"Calculated text offset {textOffsetValue} is outside the BIN length {data.Length}.";
            return false;
        }

        int firstTextOffset = (int)textOffsetValue;
        while (firstTextOffset < data.Length && data[firstTextOffset] == 0)
        {
            firstTextOffset++;
        }

        if (firstTextOffset >= data.Length || IsFooterByte(data[firstTextOffset]))
        {
            reason = $"No text segment found at calculated text offset {textOffsetValue}.";
            return false;
        }

        List<NeXASScriptSegment> segments = [];
        int position = firstTextOffset;
        int tailOffset = data.Length;

        while (position < data.Length)
        {
            byte firstByte = data[position];
            if (IsFooterByte(firstByte))
            {
                tailOffset = position;
                break;
            }

            int terminator = Array.IndexOf(data, (byte)0, position);
            if (terminator < 0)
            {
                reason = $"Text segment beginning at BIN offset {position} has no NUL terminator.";
                return false;
            }

            int rawLength = terminator - position;
            if (rawLength <= 0)
            {
                reason = $"Empty text segment found at BIN offset {position}.";
                return false;
            }

            byte[] rawText = new byte[rawLength];
            Buffer.BlockCopy(data, position, rawText, 0, rawLength);
            string text = textEncoding.GetString(rawText);
            bool isNotText = IsAsciiDigit(firstByte)
                || IsAsciiUpper(firstByte)
                || text.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

            position = terminator;
            int followingNulls = 0;
            while (position < data.Length && data[position] == 0)
            {
                position++;
                followingNulls++;
            }

            segments.Add(new NeXASScriptSegment
            {
                Index = segments.Count,
                OriginalBytes = rawText,
                FollowingNulls = followingNulls,
                IsNotText = isNotText,
                Text = text
            });
        }

        if (segments.Count == 0)
        {
            reason = "No NUL-separated text segments were found.";
            return false;
        }

        document = new NeXASScriptDocument
        {
            Format = NeXASScriptFormat.NullSeparatedTextTable,
            FirstTextOffset = firstTextOffset,
            TailOffset = tailOffset,
            Segments = segments,
            BinV2Blocks = Array.Empty<NeXASBinV2Block>()
        };
        reason = $"Parsed {segments.Count} text segments.";
        return true;
    }

    private static bool IsFooterByte(byte value)
    {
        return value < 0x20 || value == 0xFF;
    }

    private static uint ReadUInt32LittleEndian(byte[] data, int offset)
    {
        return (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
    }

    private static ulong ReadUInt64LittleEndian(byte[] data, int offset)
    {
        return (ulong)ReadUInt32LittleEndian(data, offset)
            | ((ulong)ReadUInt32LittleEndian(data, offset + 4) << 32);
    }

    private static bool IsAsciiDigit(byte value) => value >= (byte)'0' && value <= (byte)'9';
    private static bool IsAsciiUpper(byte value) => value >= (byte)'A' && value <= (byte)'Z';
}
