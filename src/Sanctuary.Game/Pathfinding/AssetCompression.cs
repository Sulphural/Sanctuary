using System;
using System.IO;
using System.IO.Compression;

namespace Sanctuary.Game.Pathfinding;

// The client's ".z" asset convention: raw DEFLATE with no zlib/gzip header (wbits = -15). Shared by
// every ".z" file in the game's asset tree (.gcnk.z, .gzne.z, .cdt.z, .adr.z, .agr.z, ...).
public static class AssetCompression
{
    public static byte[] InflateRaw(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    // Reads a client asset that may or may not be ".z"-compressed, returning its raw bytes either way.
    // Returns null when the file is missing or can't be inflated.
    public static byte[]? ReadMaybeCompressed(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            if (!path.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
                return raw;
            return InflateRaw(raw);
        }
        catch
        {
            return null;
        }
    }
}
