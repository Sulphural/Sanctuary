using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Sanctuary.Game.Pathfinding;

// Reads the client's per-tile ".gcnk" object-placement files (reverse-engineered 2026-07-24, no prior
// documentation existed for this format). Real, non-empty tiles list every placed prop/tree/building in
// that tile as a record: a small binary header, a null-terminated ".adr" model reference, then position
// (and rotation/scale, which we don't currently decode - not needed for a rough obstacle footprint).
//
// Wire format:
//   - Some files are wrapped in an OUTER raw-DEFLATE layer (no zlib header, wbits=-15) - the ".gcnk.z"
//     variant, matching every other client ".z" asset in this project. Others ship pre-decompressed
//     (plain ".gcnk", no wrapper) - detected by checking for the "GCNK" magic directly.
//   - Inner container: magic "GCNK"(4) + version(4, observed 6) + uncompressedSize(4) + compressedSize(4),
//     followed by compressedSize bytes of a STANDARD zlib stream (WITH header, unlike the outer layer)
//     that inflates to uncompressedSize bytes.
//   - Inner payload: a sequence of records. Empty/placeholder tiles use a fixed 64-byte record shape with
//     no name string and a -1000.0f sentinel float; populated tiles have variable-length records - we
//     don't parse the exact header/rotation/scale layout (unconfirmed), we just scan for the recognizable
//     "<name>.adr\0" pattern and read the 3 floats immediately following the name's null-padding as the
//     placement's world position. This is a best-effort scan, not a byte-exact record reader.
public static class GcnkParser
{
    public readonly record struct Placement(string ModelName, Vector4 Position);

    public static List<Placement> ParseFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        return Parse(raw);
    }

    public static List<Placement> Parse(ReadOnlySpan<byte> raw)
    {
        var result = new List<Placement>();

        var buffer = raw;
        Span<byte> outerDecompressed = default;
        if (buffer.Length < 4 || buffer[0] != (byte)'G' || buffer[1] != (byte)'C' || buffer[2] != (byte)'N' || buffer[3] != (byte)'K')
        {
            // Not already at the GCNK magic - assume the outer raw-DEFLATE wrapper (".gcnk.z" convention).
            outerDecompressed = InflateRaw(buffer);
            buffer = outerDecompressed;
        }

        if (buffer.Length < 16 || buffer[0] != (byte)'G' || buffer[1] != (byte)'C' || buffer[2] != (byte)'N' || buffer[3] != (byte)'K')
            return result; // not a GCNK chunk (or empty/corrupt) - caller should skip this tile

        var uncompressedSize = BitConverter.ToInt32(buffer[8..12]);
        var compressedSize = BitConverter.ToInt32(buffer[12..16]);
        if (compressedSize <= 0 || 16 + compressedSize > buffer.Length || uncompressedSize <= 0)
            return result;

        var inner = InflateZlib(buffer.Slice(16, compressedSize), uncompressedSize);

        ScanRecords(inner, result);
        return result;
    }

    // Scans for "<name>.adr\0" occurrences and reads the position floats that follow the name's null
    // padding. Not a record-boundary-aware parser - just pattern matching, so it can't distinguish real
    // objects from coincidental byte sequences, but ".adr\0" inside a run of printable identifier
    // characters is distinctive enough in practice.
    private static void ScanRecords(ReadOnlySpan<byte> data, List<Placement> result)
    {
        const string suffix = ".adr";
        var i = 0;
        while (i < data.Length - suffix.Length - 1)
        {
            if (!MatchesAt(data, i, suffix) || data[i + suffix.Length] != 0)
            {
                i++;
                continue;
            }

            // Walk backward from the ".adr" match to find the start of the identifier run (the model name).
            var nameEnd = i + suffix.Length;
            var nameStart = i;
            while (nameStart > 0 && IsNameChar(data[nameStart - 1]))
                nameStart--;

            if (nameEnd - nameStart < 3)
            {
                i = nameEnd + 1;
                continue;
            }

            var name = Encoding.ASCII.GetString(data[nameStart..nameEnd]);

            // Skip the string's null terminator(s)/alignment padding, then read 3 floats as X/Y/Z.
            var cursor = nameEnd + 1; // +1 for the confirmed null terminator
            while (cursor < data.Length && data[cursor] == 0)
                cursor++;

            if (cursor + 12 <= data.Length)
            {
                var x = BitConverter.ToSingle(data[cursor..(cursor + 4)]);
                var y = BitConverter.ToSingle(data[(cursor + 4)..(cursor + 8)]);
                var z = BitConverter.ToSingle(data[(cursor + 8)..(cursor + 12)]);

                // Sanity bound - reject obviously-wrong reads (mis-detected boundary landing on
                // unrelated binary data) rather than polluting the obstacle map with garbage.
                if (IsPlausibleWorldCoordinate(x) && IsPlausibleWorldCoordinate(y) && IsPlausibleWorldCoordinate(z))
                    result.Add(new Placement(name, new Vector4(x, y, z, 1f)));
            }

            i = nameEnd + 1;
        }
    }

    private static bool IsPlausibleWorldCoordinate(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && MathF.Abs(v) < 20000f;

    private static bool IsNameChar(byte b) =>
        (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z') ||
        (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'_';

    private static bool MatchesAt(ReadOnlySpan<byte> data, int offset, string ascii)
    {
        for (var k = 0; k < ascii.Length; k++)
        {
            if (data[offset + k] != (byte)ascii[k])
                return false;
        }
        return true;
    }

    private static byte[] InflateRaw(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] InflateZlib(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedSize);
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
