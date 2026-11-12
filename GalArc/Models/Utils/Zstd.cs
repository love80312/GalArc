using System.IO;
using ZstdSharp;

namespace GalArc.Models.Utils;

/// <summary>
/// Zstandard compression helpers.
/// </summary>
internal static class Zstd
{
    public static byte[] Decompress(byte[] data)
    {
        using MemoryStream input = new(data);
        using MemoryStream output = new();

        // Consume the complete Zstandard frame before disposing the decoder.
        using (DecompressionStream decompressionStream = new(input))
        {
            decompressionStream.CopyTo(output);
        }

        return output.ToArray();
    }

    public static byte[] Compress(byte[] data)
    {
        using MemoryStream input = new(data);
        using MemoryStream output = new();

        // Disposing CompressionStream writes the ZSTD_e_end directive and
        // finishes the frame. ZstdSharp.Port defaults leaveOpen to true,
        // so the output MemoryStream remains available after this scope.
        using (CompressionStream compressionStream = new(output))
        {
            input.CopyTo(compressionStream);
        }

        return output.ToArray();
    }
}
