using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sanctuary.Game.Pathfinding;

// Blocking tests against REAL per-model collision geometry (see CdtParser/ModelCollisionLibrary), as
// opposed to ObstacleMap's name-matched radius approximations.
//
// Built 2026-08-06 after measuring how wrong the radius guesses were: props were being sized 4x too
// large and modular cave shells 6x too small. More importantly a cave piece is a HOLLOW SHELL - the
// room you walk inside - so no single circle can represent it. Only the triangles express "walls block,
// interior is open".
//
// Two filters make this a 2D walkability test rather than a full 3D collision query:
//   - Only WALL-LIKE triangles count (near-vertical, |normal.Y| below WallNormalYThreshold). Floors and
//     ceilings are horizontal and must NOT block, or every walkable surface would read as solid.
//   - Only triangles overlapping the walkable height band around the zone's ground level count, so a
//     tunnel on another level doesn't block the one being walked.
//
// This is deliberately built for OFFLINE use (navigation-graph generation), where spending a second per
// world on 100k+ triangles is free. The runtime path still uses ObstacleMap.
public sealed class MeshObstacleMap
{
    // cos of the angle from vertical; a triangle whose normal is this horizontal is a wall, not a floor.
    private const float WallNormalYThreshold = 0.5f;
    private const float CellSize = 8f;

    private readonly Dictionary<(int, int), List<(Vector2 A, Vector2 B)>> _cells = [];
    public int WallEdgeCount { get; private set; }

    // How close a point must be to a wall edge to count as blocked - a character's collision half-width.
    private readonly float _clearance;

    private MeshObstacleMap(float clearance) => _clearance = clearance;

    public static MeshObstacleMap Build(
        IReadOnlyList<GcnkParser.Placement> placements,
        ModelCollisionLibrary library,
        float groundY,
        float bandBelow = 3f,
        float bandAbove = 8f,
        float clearance = 1.2f)
    {
        var map = new MeshObstacleMap(clearance);

        foreach (var placement in placements)
        {
            var mesh = library.TryGet(placement.ModelName);
            if (mesh is null)
                continue;

            // Placement rotation is stored as (yaw, pitch, roll) in the .gcnk RuntimeObject; only yaw
            // matters for a 2D walkability projection.
            var yaw = placement.Rotation.X;
            var (sin, cos) = ((float)Math.Sin(yaw), (float)Math.Cos(yaw));
            var scale = placement.Scale <= 0f ? 1f : placement.Scale;

            Vector3 ToWorld(Vector3 v)
            {
                var x = v.X * scale;
                var y = v.Y * scale;
                var z = v.Z * scale;
                return new Vector3(
                    placement.Position.X + x * cos + z * sin,
                    placement.Position.Y + y,
                    placement.Position.Z - x * sin + z * cos);
            }

            for (var i = 0; i < mesh.Indices.Length; i += 3)
            {
                var a = ToWorld(mesh.Vertices[mesh.Indices[i]]);
                var b = ToWorld(mesh.Vertices[mesh.Indices[i + 1]]);
                var c = ToWorld(mesh.Vertices[mesh.Indices[i + 2]]);

                var minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                var maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                if (maxY < groundY - bandBelow || minY > groundY + bandAbove)
                    continue; // another floor entirely

                var normal = Vector3.Cross(b - a, c - a);
                var len = normal.Length();
                if (len < 1e-6f)
                    continue;
                if (MathF.Abs(normal.Y / len) > WallNormalYThreshold)
                    continue; // floor/ceiling - walkable, must not block

                map.AddEdge(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
                map.AddEdge(new Vector2(b.X, b.Z), new Vector2(c.X, c.Z));
                map.AddEdge(new Vector2(c.X, c.Z), new Vector2(a.X, a.Z));
            }
        }

        return map;
    }

    private void AddEdge(Vector2 a, Vector2 b)
    {
        if (Vector2.DistanceSquared(a, b) < 1e-6f)
            return;

        WallEdgeCount++;
        var minX = (int)MathF.Floor(MathF.Min(a.X, b.X) / CellSize);
        var maxX = (int)MathF.Floor(MathF.Max(a.X, b.X) / CellSize);
        var minY = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / CellSize);
        var maxY = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / CellSize);

        for (var cx = minX; cx <= maxX; cx++)
        for (var cy = minY; cy <= maxY; cy++)
        {
            if (!_cells.TryGetValue((cx, cy), out var list))
                _cells[(cx, cy)] = list = [];
            list.Add((a, b));
        }
    }

    public bool IsBlocked(Vector4 point)
    {
        var p = new Vector2(point.X, point.Z);
        var reach = (int)MathF.Ceiling(_clearance / CellSize);
        var bx = (int)MathF.Floor(p.X / CellSize);
        var by = (int)MathF.Floor(p.Y / CellSize);

        for (var cx = bx - reach; cx <= bx + reach; cx++)
        for (var cy = by - reach; cy <= by + reach; cy++)
        {
            if (!_cells.TryGetValue((cx, cy), out var list))
                continue;
            foreach (var (a, b) in list)
                if (PointSegmentDistanceSquared(p, a, b) <= _clearance * _clearance)
                    return true;
        }

        return false;
    }

    // Walkable when no wall edge crosses the segment. Sampled rather than analytic: matches how
    // ObstacleMap already tests lines, and the sample step is well below the clearance radius so a
    // crossing can't slip between samples.
    public bool IsLineWalkable(Vector4 from, Vector4 to)
    {
        var a = new Vector2(from.X, from.Z);
        var b = new Vector2(to.X, to.Z);
        var distance = Vector2.Distance(a, b);
        if (distance < 1e-3f)
            return !IsBlocked(from);

        var steps = (int)MathF.Ceiling(distance / (_clearance * 0.75f));
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            var p = Vector2.Lerp(a, b, t);
            if (IsBlocked(new Vector4(p.X, from.Y, p.Y, 1f)))
                return false;
        }

        return true;
    }

    private static float PointSegmentDistanceSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-9f)
            return Vector2.DistanceSquared(p, a);

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }
}
