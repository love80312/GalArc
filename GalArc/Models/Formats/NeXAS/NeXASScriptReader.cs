using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GalArc.Models.Formats.NeXAS;

/// <summary>
/// Reads the NUL-separated text area used by NeXAS compiled script files.
/// The source binary is never modified. The exporter writes head.bin,
/// script.txt and tail.bin in a sibling folder.
/// </summary>
internal static class NeXASScriptReader
{
    private sealed class ScriptSegment
    {
        public int Index { get; init; }
        public int RawLength { get; init; }
        public int FollowingNulls { get; init; }
        public bool IsNotText { get; init; }
        public string Text { get; init; }
    }

    /// <summary>
    /// Attempts to parse and export a compiled NeXAS script.
    /// Returns false when the input does not match the script layout.
    /// </summary>
    public static bool TryExtract(
        byte[] data,
        string outputDirectory,
        Encoding textEncoding,
        out int segmentCount)
    {
        segmentCount = 0;
        if (data == null || data.Length < 16 || textEncoding == null)
        {
            return false;
        }

        // NeXAS script header: first UInt64 is n; text area starts at (n + 1) * 8.
        ulong headerValue = ReadUInt64LittleEndian(data, 0);
        if (headerValue >= (ulong)data.Length / 8)
        {
            return false;
        }

        ulong textOffsetValue = (headerValue + 1) * 8;
        if (textOffsetValue >= (ulong)data.Length || textOffsetValue > int.MaxValue)
        {
            return false;
        }

        int textOffset = (int)textOffsetValue;
        int firstTextOffset = textOffset;
        while (firstTextOffset < data.Length && data[firstTextOffset] == 0)
        {
            firstTextOffset++;
        }

        if (firstTextOffset >= data.Length
            || data[firstTextOffset] < 0x20
            || data[firstTextOffset] == 0xFF)
        {
            return false;
        }

        List<ScriptSegment> segments = [];
        int position = firstTextOffset;
        while (position < data.Length)
        {
            byte firstByte = data[position];
            // The compiled script's footer begins with a control byte or 0xFF.
            if (firstByte < 0x20 || firstByte == 0xFF)
            {
                break;
            }

            int terminator = Array.IndexOf(data, (byte)0, position);
            if (terminator < 0)
            {
                // A text segment without a terminating NUL is not a valid script segment.
                return false;
            }

            int rawLength = terminator - position;
            if (rawLength <= 0)
            {
                break;
            }

            string text = textEncoding.GetString(data, position, rawLength);
            bool isNotText = IsAsciiDigit(firstByte) || IsAsciiUpper(firstByte)
                || text.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

            position = terminator;
            int followingNulls = 0;
            while (position < data.Length && data[position] == 0)
            {
                position++;
                followingNulls++;
            }

            segments.Add(new ScriptSegment
            {
                Index = segments.Count,
                RawLength = rawLength,
                FollowingNulls = followingNulls,
                IsNotText = isNotText,
                Text = text
            });
        }

        if (segments.Count == 0)
        {
            return false;
        }

        string textFilePath = Path.Combine(outputDirectory, "script.txt");
        string headFilePath = Path.Combine(outputDirectory, "head.bin");
        string tailFilePath = Path.Combine(outputDirectory, "tail.bin");

        StringBuilder textOutput = new();
        textOutput.Append("ZBSPAC-TRANSLATION ENCODING ")
            .Append(GetZbspacEncodingName(textEncoding))
            .Append(" COUNT ")
            .Append(segments.Count.ToString("D5", CultureInfo.InvariantCulture))
            .Append("\r\n\r\n");

        foreach (ScriptSegment segment in segments)
        {
            textOutput.Append("SEG ")
                .Append(segment.Index.ToString(CultureInfo.InvariantCulture))
                .Append(" NULL ")
                .Append(segment.FollowingNulls.ToString(CultureInfo.InvariantCulture));

            if (segment.IsNotText)
            {
                textOutput.Append(" NOT-TEXT");
            }

            textOutput.Append("\r\n")
                .Append(segment.Text).Append("\r\n")
                .Append('-', segment.RawLength).Append("\r\n")
                .Append(segment.Text).Append("\r\n\r\n");
        }

        // head.bin contains all bytes before the first text segment;
        // tail.bin preserves the binary footer after the last text segment.
        byte[] head = new byte[firstTextOffset];
        Buffer.BlockCopy(data, 0, head, 0, head.Length);

        int tailLength = data.Length - position;
        byte[] tail = new byte[tailLength];
        if (tailLength > 0)
        {
            Buffer.BlockCopy(data, position, tail, 0, tailLength);
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(headFilePath, head);
        File.WriteAllText(textFilePath, textOutput.ToString(), Encoding.Unicode);
        File.WriteAllBytes(tailFilePath, tail);

        segmentCount = segments.Count;
        return true;
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

    private static string GetZbspacEncodingName(Encoding encoding)
    {
        return encoding.CodePage switch
        {
            932 => "japanese",
            936 or 950 => "chinese",
            949 => "korean",
            _ => encoding.WebName
        };
    }
}
