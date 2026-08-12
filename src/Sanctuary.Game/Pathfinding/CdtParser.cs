using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Sanctuary.Game.Pathfinding;

// Reads the client's ".cdt" COLLISION MESH files ("CDTA"), the real per-model collision geometry the
// client itself uses - reverse-engineered 2026-08-06.
//
// This exists to replace ObstacleMap.FootprintRadius's name-matched radius GUESSES ("if the name contains
// 'tree' assume 4 units"). Measuring real models against those guesses showed they were wrong in both
// directions and by large factors - sg_bixie_funnel_01 is a ~1-unit prop that was being treated as 4, and
// the modular cave shells are ~100 units across while being treated as 12.
//
// CRITICALLY, a cave piece's collision mesh is a HOLLOW SHELL - a room you walk inside - so its bounding
// radius is meaningless as a blocking circle (a 78-unit solid disc would seal a whole dungeon). Only the
// real triangles express "the walls block but the interior is open", which is why this parses geometry
// rather than just reading a bounding box.
//
// Wire format (validated: indices in range and a clean vertex+index parse on small AND large models):
//   magic "CDTA"(4) + version(4, observed 1) + 3 x int32 (unknown/flags) + int32 vertexCount
//   + vertexCount * Vector3 (12 bytes each, MODEL space)
//   + int32 triangleCount + triangleCount * 3 * uint16 vertex indices
//   + trailing data (not parsed - a BVH/material block; we only need the mesh itself)
public static class CdtParser
{
    public sealed class CollisionMesh
    {
        public required Vector3[] Vertices { get; init; }
        public required int[] Indices { get; init; } // 3 per triangle
        public int TriangleCount => Indices.Length / 3;
    }

    private const int VertexCountOffset = 20;
    private const int VertexDataOffset = 24;
    private const int MaxSaneVertices = 1_000_000;

    public static CollisionMesh? Parse(ReadOnlySpan<byte> raw)
    {
        var data = raw;

        // ".cdt.z" assets are raw-DEFLATE wrapped, same convention as every other client ".z" asset.
        byte[]? inflated = null;
        if (!MatchesMagic(data))
        {
            try { inflated = AssetCompression.InflateRaw(data); }
            catch { return null; }
            data = inflated;
        }

        if (!MatchesMagic(data) || data.Length < VertexDataOffset)
            return null;

        var vertexCount = BitConverter.ToInt32(data.Slice(VertexCountOffset, 4));
        if (vertexCount <= 0 || vertexCount > MaxSaneVertices)
            return null;

        var vertexBytes = (long)vertexCount * 12;
        if (VertexDataOffset + vertexBytes + 4 > data.Length)
            return null;

        var vertices = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var o = VertexDataOffset + i * 12;
            vertices[i] = new Vector3(
                BitConverter.ToSingle(data.Slice(o, 4)),
                BitConverter.ToSingle(data.Slice(o + 4, 4)),
                BitConverter.ToSingle(data.Slice(o + 8, 4)));
        }

        var indexCountOffset = (int)(VertexDataOffset + vertexBytes);
        var triangleCount = BitConverter.ToInt32(data.Slice(indexCountOffset, 4));
        if (triangleCount <= 0)
            return null;

        var indexBytes = (long)triangleCount * 6;
        if (indexCountOffset + 4 + indexBytes > data.Length)
            return null;

        var indices = new int[triangleCount * 3];
        for (var i = 0; i < indices.Length; i++)
        {
            var idx = BitConverter.ToUInt16(data.Slice(indexCountOffset + 4 + i * 2, 2));
            if (idx >= vertexCount)
                return null; // parse desync - reject rather than emit garbage geometry
            indices[i] = idx;
        }

        return new CollisionMesh { Vertices = vertices, Indices = indices };
    }

    public static CollisionMesh? ParseFile(string path)
    {
        try { return Parse(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    private static bool MatchesMagic(ReadOnlySpan<byte> b)
        => b.Length >= 4 && b[0] == (byte)'C' && b[1] == (byte)'D' && b[2] == (byte)'T' && b[3] == (byte)'A';
}
