using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace GalArc.Models.Formats.NeXAS;

/// <summary>
/// Rebuilds a NeXAS BIN by replacing its translatable text segments.
/// The source BIN supplies the header, non-text segments, NUL separators and footer.
/// </summary>
internal static class NeXASScriptWriter
{
    // A private-use marker is used only when a segment contains a real line break
    // or tab. Backslash command strings such as \N[1] remain visually unchanged.
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

        if (!NeXASScriptReader.TryParse(sourceData, sourceTextEncoding, out NeXASScriptDocument document))
        {
            throw new InvalidDataException("The source BIN does not match the supported NeXAS script layout.");
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

        using MemoryStream output = new(sourceData.Length + 4096);
        output.Write(sourceData, 0, document.FirstTextOffset);

        for (int i = 0; i < document.Segments.Count; i++)
        {
            NeXASScriptSegment segment = document.Segments[i];
            byte[] textBytes;

            // File names, labels and other NOT-TEXT entries are always retained
            // from the target BIN even if someone edits their corresponding TXT line.
            if (segment.IsNotText)
            {
                textBytes = segment.OriginalBytes;
            }
            else
            {
                string translatedText = UnescapeText(translatedLines[i]);
                if (string.Equals(translatedText, segment.Text, StringComparison.Ordinal))
                {
                    // Preserve original byte sequences for unchanged text. This avoids
                    // normalizing duplicate or vendor-specific code-page mappings.
                    textBytes = segment.OriginalBytes;
                }
                else
                {
                    try
                    {
                        textBytes = strictEncoding.GetBytes(translatedText);
                    }
                    catch (EncoderFallbackException ex)
                    {
                        throw new InvalidDataException(
                            $"TXT line {i + 1} contains characters that cannot be represented by {translatedTextEncoding.WebName} (code page {translatedTextEncoding.CodePage}). " +
                            "Choose the correct script text encoding before packing.", ex);
                    }

                    if (Array.IndexOf(textBytes, (byte)0) >= 0)
                    {
                        throw new InvalidDataException($"TXT line {i + 1} encodes to a NUL byte, which cannot be stored inside a NeXAS text segment.");
                    }

                    changedSegments++;
                }
            }

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
