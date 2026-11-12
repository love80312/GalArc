using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalArc.Models.Formats.NeXAS;

/// <summary>
/// One NUL-terminated text segment from a compiled NeXAS script.
/// </summary>
internal sealed class NeXASScriptSegment
{
    public int Index { get; init; }
    public byte[] OriginalBytes { get; init; }
    public int FollowingNulls { get; init; }
    public bool IsNotText { get; init; }
    public string Text { get; init; }
}

/// <summary>
/// Parsed layout of a compiled NeXAS script.
/// The header and footer are retained byte-for-byte from the source BIN.
/// </summary>
internal sealed class NeXASScriptDocument
{
    public int FirstTextOffset { get; init; }
    public int TailOffset { get; init; }
    public IReadOnlyList<NeXASScriptSegment> Segments { get; init; }
}

/// <summary>
/// Extracts the NUL-separated text area from supported NeXAS compiled scripts.
/// TXT files are plain UTF-8, one segment per line, stored in the PAC output's _text folder.
/// </summary>
internal static class NeXASScriptReader
{
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

        // NeXAS compiled script header: the first UInt64 is n;
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
                // A text segment without a terminating NUL is not safe to rewrite.
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
            FirstTextOffset = firstTextOffset,
            TailOffset = tailOffset,
            Segments = segments
        };
        reason = $"Parsed {segments.Count} text segments.";
        return true;
    }

    private static bool IsFooterByte(byte value)
    {
        // The original NeXAS script layout terminates its text section
        // when a segment would begin with a control byte or 0xFF.
        return value < 0x20 || value == 0xFF;
    }

    private static ulong ReadUInt64LittleEndian(byte[] data, int offset)
    {
        return (ulong)data[offset]
            | ((ulong)data[offset + 1] << 8)
            | ((ulong)data[offset + 2] << 16)
            | ((ulong)data[offset + 3] << 24)
            | ((ulong)data[offset + 4] << 32)
            | ((ulong)data[offset + 5] << 40)
            | ((ulong)data[offset + 6] << 48)
            | ((ulong)data[offset + 7] << 56);
    }

    private static bool IsAsciiDigit(byte value) => value >= (byte)'0' && value <= (byte)'9';

    private static bool IsAsciiUpper(byte value) => value >= (byte)'A' && value <= (byte)'Z';
}
