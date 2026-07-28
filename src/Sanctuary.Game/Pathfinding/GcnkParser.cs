using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Sanctuary.Game.Pathfinding;

// Reads the client's per-tile ".gcnk" world-chunk files: real per-tile structure (lights, "areas", groups,
// and RuntimeObject placements - trees, buildings, decorative clutter, AND structural cave-wall/boulder/
// platform pieces) plus a flat render mesh (index/vertex buffer - just a generic ground-plane placeholder,
// not real terrain shape; not parsed here, we only need the placements).
//
// This is a byte-exact port of the real, community-documented format from the ForgeLightToolkit project
// (github.com/EDITzDev/ForgeLightToolkit, a Unity importer built specifically for Free Realms/Clone Wars
// Adventures) - found 2026-07-26 after an EARLIER version of this parser (fragile ".adr\0" substring
// pattern-matching, no real structure awareness) turned out to silently MISS every structural cave piece:
// those use the ".agr" extension, not ".adr", so the old scanner never found them at all - it only ever
// found decorative dressing (flytraps, bones, water, crystals), explaining why mob pathfinding still
// clipped through cave walls even after wiring in real placement-based obstacle avoidance. Validated
// byte-exact against bs_cracked_claw_caverns's real 4 tile files (zero remaining/unparsed bytes after a
// full parse) before porting to C#.
//
// Wire format:
//   - Outer: optional raw-DEFLATE wrapper (wbits=-15, the ".gcnk.z" convention shared by every other client
//     ".z" asset), OR already at the "GCNK" magic (plain ".gcnk", pre-decompressed).
//   - magic "GCNK"(4) + version(4, observed 5-6) + chunkUncompressedLen(4) + chunkCompressedLen(4) +
//     chunkCompressedLen bytes of a STANDARD zlib stream (WITH header) -> the "chunk" payload:
//       int32 tileCount, then per tile: Coords(2xint32), Position(Vector4), Unknown7(int32, if >0 read 4
//       more int32s), Unknown12(float), ecoDataCount(int32)+that many int32s, runtimeObjectCount(int32)+
//       that many RuntimeObject records (see below), rawLightCount+records, rawAreaCount+records,
//       rawGroupCount+records, Unknown13(int32), 4 trailing bytes.
//     Then (not parsed here - not needed for placements): exportRenderBatchCount+records, heightMapBpp(+
//     optional heightmap data if version>4), indexBufferCount+ushorts, vertexBufferCount+vertex records.
//   - RuntimeObject: Unknown(int32), FileName(cstr - the real ".adr"/".agr" model reference), Unknown3(cstr),
//     Position(Vector4), Rotation(Vector4), Scale(float), [if version>=6: MaterialName(cstr), TintAlias(cstr),
//     if TintAlias non-empty: Vector4], 4 skip bytes, [if version>4: ObjectId(int32) else unsupported],
//     Unknown11(int32), [if version>2: unknownCount(int32)+that many (int32 size, size*4 bytes) blobs].
//   - RawLight: Name(cstr), ColorName(cstr), Type(byte), Position(Vector4), Range(float), Intensity(float),
//     Color(4 bytes).
//   - RawArea: Name(cstr), int32, Name2(cstr), Vector4, Vector4, int32, Vector3(12 bytes).
//   - RawGroup: Name(cstr), Vector4, Vector4, int32.
public static class GcnkParser
{
    public readonly record struct Placement(string ModelName, Vector4 Position, Vector4 Rotation, float Scale);

    public static List<Placement> ParseFile(string path) => Parse(File.ReadAllBytes(path));

    public static List<Placement> Parse(ReadOnlySpan<byte> raw)
    {
        var result = new List<Placement>();

        var buffer = raw;
        Span<byte> outerDecompressed = default;
        if (!MatchesMagic(buffer))
        {
            outerDecompressed = InflateRaw(buffer);
            buffer = outerDecompressed;
        }

        if (buffer.Length < 16 || !MatchesMagic(buffer))
            return result; // not a GCNK chunk (or empty/corrupt) - caller should skip this tile

        var version = BitConverter.ToInt32(buffer.Slice(4, 4));
        var chunkUncompressedLen = BitConverter.ToInt32(buffer.Slice(8, 4));
        var chunkCompressedLen = BitConverter.ToInt32(buffer.Slice(12, 4));
        if (chunkCompressedLen <= 0 || 16 + chunkCompressedLen > buffer.Length || chunkUncompressedLen <= 0)
            return result;

        byte[] chunk;
        try
        {
            chunk = InflateZlib(buffer.Slice(16, chunkCompressedLen), chunkUncompressedLen);
        }
        catch
        {
            return result; // corrupt/unexpected compressed data - skip this tile rather than throw
        }

        try
        {
            ParseChunk(chunk, version, result);
        }
        catch
        {
            // A version/field we don't handle correctly would desync the reader and start producing
            // garbage records - safer to return whatever we parsed before the failure than to risk
            // polluting the obstacle map with misread positions.
        }

        return result;
    }

    private static void ParseChunk(byte[] chunk, int version, List<Placement> result)
    {
        var r = new SpanReader(chunk);
        var tileCount = r.ReadInt32();

        for (var t = 0; t < tileCount; t++)
        {
            r.ReadInt32(); r.ReadInt32(); // Coords
            r.ReadVector4(); // Position

            var unknown7 = r.ReadInt32();
            if (unknown7 > 0)
            {
                r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
            }

            r.ReadSingle(); // Unknown12

            var ecoCount = r.ReadInt32();
            for (var i = 0; i < ecoCount; i++)
                r.ReadInt32();

            var runtimeObjectCount = r.ReadInt32();
            for (var i = 0; i < runtimeObjectCount; i++)
                ParseRuntimeObject(ref r, version, result);

            var lightCount = r.ReadInt32();
            for (var i = 0; i < lightCount; i++)
                SkipRawLight(ref r);

            var areaCount = r.ReadInt32();
            for (var i = 0; i < areaCount; i++)
                SkipRawArea(ref r);

            var groupCount = r.ReadInt32();
            for (var i = 0; i < groupCount; i++)
                SkipRawGroup(ref r);

            r.ReadInt32(); // Unknown13
            r.Skip(4);
        }

        // Render mesh (export batches / heightmap / index+vertex buffers) follows - not needed for
        // placement-based obstacles, and we've already got what we came for.
    }

    private static void ParseRuntimeObject(ref SpanReader r, int version, List<Placement> result)
    {
        r.ReadInt32(); // Unknown
        var fileName = r.ReadCString();
        r.ReadCString(); // Unknown3
        var position = r.ReadVector4();
        var rotation = r.ReadVector4();
        var scale = r.ReadSingle();

        if (version >= 6)
        {
            r.ReadCString(); // MaterialName
            var tintAlias = r.ReadCString();
            if (tintAlias.Length > 0)
                r.ReadVector4();
        }

        r.Skip(4);

        if (version > 4)
            r.ReadInt32(); // ObjectId
        else
            throw new NotSupportedException("GcnkParser: version <= 4 RuntimeObject layout not supported");

        r.ReadInt32(); // Unknown11

        if (version > 2)
        {
            var unknownCount = r.ReadInt32();
            for (var i = 0; i < unknownCount; i++)
            {
                var size = r.ReadInt32();
                r.Skip(size * 4);
            }
        }

        if (fileName.Length > 0 && IsPlausibleWorldCoordinate(position.X) && IsPlausibleWorldCoordinate(position.Y) && IsPlausibleWorldCoordinate(position.Z))
            result.Add(new Placement(fileName, position, rotation, scale));
    }

    private static void SkipRawLight(ref SpanReader r)
    {
        r.ReadCString(); r.ReadCString();
        r.Skip(1); // Type
        r.ReadVector4();
        r.ReadSingle(); r.ReadSingle(); // Range, Intensity
        r.Skip(4); // Color32
    }

    private static void SkipRawArea(ref SpanReader r)
    {
        r.ReadCString();
        r.ReadInt32();
        r.ReadCString();
        r.ReadVector4();
        r.ReadVector4();
        r.ReadInt32();
        r.Skip(12); // Vector3
    }

    private static void SkipRawGroup(ref SpanReader r)
    {
        r.ReadCString();
        r.ReadVector4();
        r.ReadVector4();
        r.ReadInt32();
    }

    private static bool MatchesMagic(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'G' && data[1] == (byte)'C' && data[2] == (byte)'N' && data[3] == (byte)'K';

    private static bool IsPlausibleWorldCoordinate(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && MathF.Abs(v) < 20000f;

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

    // Minimal little-endian binary reader over a byte[] (avoids a BinaryReader+MemoryStream allocation
    // pair, and ReadCString needs raw byte access anyway).
    private ref struct SpanReader
    {
        private readonly byte[] _data;
        private int _pos;

        public SpanReader(byte[] data)
        {
            _data = data;
            _pos = 0;
        }

        public void Skip(int count) => _pos += count;

        public int ReadInt32()
        {
            var v = BitConverter.ToInt32(_data, _pos);
            _pos += 4;
            return v;
        }

        public float ReadSingle()
        {
            var v = BitConverter.ToSingle(_data, _pos);
            _pos += 4;
            return v;
        }

        public Vector4 ReadVector4()
        {
            var v = new Vector4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
            return v;
        }

        public string ReadCString()
        {
            var start = _pos;
            var end = Array.IndexOf(_data, (byte)0, start);
            if (end < 0)
                throw new InvalidDataException("GcnkParser: unterminated string");
            var s = Encoding.UTF8.GetString(_data, start, end - start);
            _pos = end + 1;
            return s;
        }
    }
}
