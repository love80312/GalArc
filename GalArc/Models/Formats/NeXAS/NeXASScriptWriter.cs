using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace GalArc.Models.Formats.NeXAS;

/// <summary>
/// Rebuilds supported NeXAS BIN script layouts while retaining all unrelated
/// binary fields from the original target BIN.
/// </summary>
internal static class NeXASScriptWriter
{
    private const char EscapeMarker = '\uE000';
    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    public static byte[] ApplyTranslation(
        byte[] sourceData,
        string textFilePath,
        Encoding sourceTextEncoding,
        Encoding translatedTextEncoding,
        out int changedSegments,
        out int totalSegments)
    {
        changedSegments = 0;
        totalSegments = 0;

        if (!NeXASScriptReader.TryParse(sourceData, sourceTextEncoding,
            out NeXASScriptDocument document, out string parseReason))
        {
            throw new InvalidDataException(
                "The source BIN does not match a supported NeXAS script layout. " + parseReason);
        }

        string[] translatedLines = File.ReadAllLines(textFilePath, Utf8Strict);
        totalSegments = document.Segments.Count;
        if (translatedLines.Length != totalSegments)
        {
            throw new InvalidDataException(
                $"Translation line count mismatch: BIN has {totalSegments} segments, TXT has {translatedLines.Length} lines. " +
                "Do not add or remove lines from the TXT file.");
        }

        Encoding strictEncoding = Encoding.GetEncoding(
            translatedTextEncoding.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

        return document.Format switch
        {
            NeXASScriptFormat.BinV2 => ApplyBinV2Translation(
                sourceData, document, translatedLines, strictEncoding,
                out changedSegments),
            _ => ApplyNullSeparatedTableTranslation(
                sourceData, translatedLines, document, strictEncoding,
                out changedSegments)
        };
    }

    private static byte[] ApplyNullSeparatedTableTranslation(
        byte[] sourceData,
        string[] translatedLines,
        NeXASScriptDocument document,
        Encoding strictEncoding,
        out int changedSegments)
    {
        changedSegments = 0;
        using MemoryStream output = new(sourceData.Length + 4096);
        output.Write(sourceData, 0, document.FirstTextOffset);

        for (int i = 0; i < document.Segments.Count; i++)
        {
            NeXASScriptSegment segment = document.Segments[i];
            byte[] textBytes = GetTranslatedBytes(
                segment, translatedLines[i], strictEncoding, i, ref changedSegments);

            output.Write(textBytes, 0, textBytes.Length);
            for (int n = 0; n < segment.FollowingNulls; n++)
            {
                output.WriteByte(0);
            }
        }

        if (document.TailOffset < 0 || document.TailOffset > sourceData.Length)
        {
            throw new InvalidDataException("The parsed BIN footer offset is invalid.");
        }

        output.Write(sourceData, document.TailOffset, sourceData.Length - document.TailOffset);
        return output.ToArray();
    }

    private static byte[] ApplyBinV2Translation(
        byte[] sourceData,
        NeXASScriptDocument document,
        string[] translatedLines,
        Encoding strictEncoding,
        out int changedSegments)
    {
        changedSegments = 0;
        using MemoryStream output = new(sourceData.Length + 4096);
        int sourcePosition = 0;

        foreach (NeXASBinV2Block block in document.BinV2Blocks)
        {
            if (block.Offset < sourcePosition || block.EndOffset < block.Offset
                || block.EndOffset > sourceData.Length)
            {
                throw new InvalidDataException("BINv2 block offsets overlap or exceed the source BIN.");
            }

            // Preserve bytes between BINv2 structures without modification.
            output.Write(sourceData, sourcePosition, block.Offset - sourcePosition);
            output.Write(block.PrefixBytes, 0, block.PrefixBytes.Length);
            WriteUInt32LittleEndian(output, checked((uint)block.Segments.Count));

            foreach (NeXASScriptSegment segment in block.Segments)
            {
                if (segment.Index < 0 || segment.Index >= translatedLines.Length)
                {
                    throw new InvalidDataException("BINv2 segment index is outside the TXT line range.");
                }

                byte[] stringBytes = GetTranslatedBytes(
                    segment, translatedLines[segment.Index], strictEncoding,
                    segment.Index, ref changedSegments);

                output.Write(stringBytes, 0, stringBytes.Length);
                output.WriteByte(0);
            }

            // The second and third CString arrays are not exported as editable
            // lines; retain their original counts, bytes and terminators exactly.
            output.Write(block.SuffixBytes, 0, block.SuffixBytes.Length);
            sourcePosition = block.EndOffset;
        }

        output.Write(sourceData, sourcePosition, sourceData.Length - sourcePosition);
        return output.ToArray();
    }

    private static byte[] GetTranslatedBytes(
        NeXASScriptSegment segment,
        string translatedLine,
        Encoding strictEncoding,
        int lineIndex,
        ref int changedSegments)
    {
        // In the legacy format this protects file names and labels. BINv2's
        // primary Strings array is intentionally editable as a whole, because
        // its upstream structure identifies it as the script string table.
        if (segment.IsNotText)
        {
            return segment.OriginalBytes;
        }

        string translatedText = UnescapeText(translatedLine);
        if (string.Equals(translatedText, segment.Text, StringComparison.Ordinal))
        {
            return segment.OriginalBytes;
        }

        byte[] textBytes;
        try
        {
            textBytes = strictEncoding.GetBytes(translatedText);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"TXT line {lineIndex + 1} contains characters that cannot be represented by code page {strictEncoding.CodePage}. " +
                "Choose the correct script text encoding before packing.", ex);
        }

        if (Array.IndexOf(textBytes, (byte)0) >= 0)
        {
            throw new InvalidDataException(
                $"TXT line {lineIndex + 1} encodes to a NUL byte, which cannot be stored inside a NeXAS CString.");
        }

        changedSegments++;
        return textBytes;
    }

    private static void WriteUInt32LittleEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    internal static string EscapeText(string text)
    {
        StringBuilder result = new(text.Length);
        foreach (char character in text)
        {
            switch (character)
            {
                case EscapeMarker:
                    result.Append(EscapeMarker).Append('E');
                    break;
                case '\r':
                    result.Append(EscapeMarker).Append('R');
                    break;
                case '\n':
                    result.Append(EscapeMarker).Append('N');
                    break;
                case '\t':
                    result.Append(EscapeMarker).Append('T');
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        result.Append(EscapeMarker)
                            .Append('U')
                            .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        result.Append(character);
                    }
                    break;
            }
        }
        return result.ToString();
    }

    private static string UnescapeText(string text)
    {
        StringBuilder result = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            if (current != EscapeMarker)
            {
                result.Append(current);
                continue;
            }

            if (++i >= text.Length)
            {
                throw new InvalidDataException("TXT ends with an incomplete internal escape marker.");
            }

            char escape = text[i];
            switch (escape)
            {
                case 'E':
                    result.Append(EscapeMarker);
                    break;
                case 'R':
                    result.Append('\r');
                    break;
                case 'N':
                    result.Append('\n');
                    break;
                case 'T':
                    result.Append('\t');
                    break;
                case 'U':
                    if (i + 4 >= text.Length || !ushort.TryParse(
                        text.AsSpan(i + 1, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out ushort value))
                    {
                        throw new InvalidDataException("TXT contains an invalid internal Unicode escape.");
                    }
                    result.Append((char)value);
                    i += 4;
                    break;
                default:
                    throw new InvalidDataException($"TXT contains an unknown internal escape marker: {escape}.");
            }
        }

        return result.ToString();
    }
}
