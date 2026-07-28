using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace Sanctuary.Game.Pathfinding;

// Reads the client's per-world ".gzne" file — the REAL wall/collision boundary geometry (reverse-engineered
// 2026-07-26 by hex-dumping bs_cracked_claw_caverns.gzne.z after ObstacleMap's own .gcnk-derived obstacles
// turned out to have ZERO wall coverage for that dungeon: .gcnk only captures discrete PLACED PROPS
// (trees, buildings, decorative clutter), never the cave/terrain mesh itself - .gzne is the file that does).
//
// Wire format (best-effort, pattern-matched like GcnkParser — not a byte-exact record reader):
//   - Same outer raw-DEFLATE wrapper convention as every other ".z" client asset (".gzne.z", wbits=-15);
//     some files ship pre-decompressed ("magic GZNE" visible directly).
//   - Magic "GZNE"(4) + a small header (version, material/texture references as "<name>.dds\0" strings, a
//     few unexplained int32 fields) that this parser does NOT fully decode — it just finds the LAST
//     "<name>.dds\0" occurrence and starts scanning for wall-strip groups right after it.
//   - A wall-strip group = int32 vertexCount (always even, observed 4-24) followed by vertexCount vertices
//     (3 floats each). Vertices come in FLOOR/CEILING PAIRS: consecutive vertices [2i]/[2i+1] share the same
//     (X, Z) with Y differing by ~100 (a vertical "wall post" at that boundary point, floor to well above
//     any camera-relevant height). Consecutive PAIRS within one group form a connected wall strip - the
//     polyline through the floor vertices IS the real 2D collision boundary. Groups repeat back-to-back
//     until the buffer ends (or, for multi-material worlds with texture names interleaved mid-stream, until
//     the pattern breaks — NOT YET HANDLED: only the strips after the LAST texture name are recovered, so
//     multi-material worlds will under-report walls rather than parse garbage. Live-verified 2026-07-26: the
//     single-material case (bs_cracked_claw_caverns, sg_bandit_hideout) parses cleanly end-to-end with zero
//     rejected bytes; multi-material worlds (e.g. sh_frostfang_cavern) stop partway — partial real data,
//     never corrupted data, since every group is validated (finite floats, plausible world-coordinate range,
//     AND the floor/ceiling pairing itself) before being accepted.
public static class GzneParser
{
    // A connected wall boundary: consecutive points are real, adjacent collision segments. Only the floor
    // vertex of each floor/ceiling pair is kept — the ~100-unit-tall "post" itself isn't needed, we only
    // block movement in the X/Z plane.
    public readonly record struct WallStrip(IReadOnlyList<Vector4> Points);

    public static List<WallStrip> ParseFile(string path) => Parse(File.ReadAllBytes(path));

    public static List<WallStrip> Parse(ReadOnlySpan<byte> raw)
    {
        var result = new List<WallStrip>();

        var buffer = raw;
        Span<byte> outerDecompressed = default;
        if (!MatchesAt(buffer, 0, "GZNE"))
        {
            outerDecompressed = InflateRaw(buffer);
            buffer = outerDecompressed;
        }

        if (buffer.Length < 4 || !MatchesAt(buffer, 0, "GZNE"))
            return result; // not a GZNE chunk (or empty/corrupt) - caller should skip this world

        var lastTexEnd = FindLastTextureEnd(buffer);
        if (lastTexEnd < 0)
            return result;

        var pos = lastTexEnd;
        var fails = 0;
        while (pos + 4 <= buffer.Length && fails < 64)
        {
            var count = BitConverter.ToInt32(buffer.Slice(pos, 4));
            if (count >= 2 && count <= 1000 && count % 2 == 0 && pos + 4 + count * 12 <= buffer.Length)
            {
                if (TryReadGroup(buffer, pos + 4, count, out var points))
                {
                    result.Add(new WallStrip(points));
                    pos += 4 + count * 12;
                    fails = 0;
                    continue;
                }
            }
            pos++;
            fails++;
        }

        return result;
    }

    private static bool TryReadGroup(ReadOnlySpan<byte> buffer, int start, int count, out List<Vector4> points)
    {
        points = new List<Vector4>(count / 2);
        for (var i = 0; i < count; i += 2)
        {
            var oa = start + i * 12;
            var ob = oa + 12;
            var ax = BitConverter.ToSingle(buffer.Slice(oa, 4));
            var ay = BitConverter.ToSingle(buffer.Slice(oa + 4, 4));
            var az = BitConverter.ToSingle(buffer.Slice(oa + 8, 4));
            var bx = BitConverter.ToSingle(buffer.Slice(ob, 4));
            var by = BitConverter.ToSingle(buffer.Slice(ob + 4, 4));
            var bz = BitConverter.ToSingle(buffer.Slice(ob + 8, 4));

            if (!IsPlausibleWorldCoordinate(ax) || !IsPlausibleWorldCoordinate(ay) || !IsPlausibleWorldCoordinate(az) ||
                !IsPlausibleWorldCoordinate(bx) || !IsPlausibleWorldCoordinate(by) || !IsPlausibleWorldCoordinate(bz))
                return false;

            // Floor/ceiling pair check: same (X,Z), Y differs by ~100 - this is what distinguishes real wall
            // data from a coincidental byte-pattern match.
            if (MathF.Abs(ax - bx) > 0.01f || MathF.Abs(az - bz) > 0.01f || MathF.Abs(MathF.Abs(ay - by) - 100f) > 1f)
                return false;

            points.Add(new Vector4(ax, ay, az, 1f));
        }
        return true;
    }

    private static bool IsPlausibleWorldCoordinate(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && MathF.Abs(v) < 20000f;

    // The LAST "<name>.dds\0" occurrence in the buffer - real wall-strip data for the final material starts
    // right after it (see the class comment's multi-material caveat).
    private static int FindLastTextureEnd(ReadOnlySpan<byte> data)
    {
        const string suffix = ".dds";
        var last = -1;
        var i = 0;
        while (i < data.Length - suffix.Length - 1)
        {
            if (MatchesAt(data, i, suffix) && data[i + suffix.Length] == 0)
            {
                last = i + suffix.Length + 1;
                i += suffix.Length + 1;
            }
            else
            {
                i++;
            }
        }
        return last;
    }

    private static bool MatchesAt(ReadOnlySpan<byte> data, int offset, string ascii)
    {
        if (offset < 0 || offset + ascii.Length > data.Length)
            return false;
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
}
