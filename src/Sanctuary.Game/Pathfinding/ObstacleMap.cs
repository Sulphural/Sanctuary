using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sanctuary.Game.Pathfinding;

// Real-world obstacle set built from parsed .gcnk tile placements (see GcnkParser) - used to reject
// WaypointGraph edges that would cut through actual geometry (buildings, trees, etc.) instead of relying
// purely on NPC-proximity heuristics. We don't have real per-model collision extents wired up (the .cdt
// mesh format is parseable but not yet connected to placement data), so each placement gets a rough
// footprint RADIUS from its model name instead of an exact shape - a circle is a coarse approximation of
// a building footprint, but it's real placement data instead of no data at all.
public sealed class ObstacleMap
{
    private readonly record struct Obstacle(Vector4 Position, float Radius);

    private const float CellSize = 16f;
    private readonly Dictionary<(int, int), List<Obstacle>> _cells = [];
    private readonly int _obstacleCount;

    private ObstacleMap(List<(Vector4 Position, float Radius)> obstacles)
    {
        _obstacleCount = obstacles.Count;
        foreach (var (position, radius) in obstacles)
        {
            var obstacle = new Obstacle(position, radius);
            foreach (var cell in CellsCovering(position, radius))
            {
                if (!_cells.TryGetValue(cell, out var list))
                    _cells[cell] = list = [];
                list.Add(obstacle);
            }
        }
    }

    public int ObstacleCount => _obstacleCount;

    public static ObstacleMap Build(IReadOnlyList<GcnkParser.Placement> placements)
    {
        var obstacles = new List<(Vector4, float)>(placements.Count);
        foreach (var placement in placements)
        {
            var radius = FootprintRadius(placement.ModelName);
            if (radius > 0f)
                obstacles.Add((placement.Position, radius));
        }
        return new ObstacleMap(obstacles);
    }

    // Rough per-model-name footprint radius, biggest match wins. Not exact - a coarse stand-in for real
    // collision extents (see the class comment). Deliberately conservative: err toward blocking too much
    // rather than too little, since a false "blocked" just costs a slightly longer route, but a false
    // "clear" is exactly the "walks through buildings" bug this exists to fix. Buildings in particular are
    // often built from several separate placements (sg_shop_facades_back_01, _sign_01, _stringlights_01,
    // etc. - live-confirmed 2026-07-24 by scanning real tile data) rather than one object, so a generous
    // radius matters for keeping their combined footprint gap-free.
    private static float FootprintRadius(string modelName)
    {
        var name = modelName.ToLowerInvariant();

        // Explicitly small/no-block: decorative clutter that doesn't actually impede walking.
        if (name.Contains("flora") || name.Contains("cluster") || name.Contains("streetlamp") ||
            name.Contains("torch") || name.Contains("sign") || name.Contains("stringlights") ||
            name.Contains("flower") || name.Contains("grass") || name.Contains("stump")) return 0f;

        if (name.Contains("tree_giant")) return 9f;
        if (name.Contains("tree_medium")) return 6f;
        if (name.Contains("tree")) return 4f;
        if (name.Contains("building") || name.Contains("house") || name.Contains("tower") ||
            name.Contains("cabin") || name.Contains("hut") || name.Contains("castle") ||
            name.Contains("shop") || name.Contains("facade") || name.Contains("store") ||
            name.Contains("inn") || name.Contains("greenhouse") || name.Contains("mausoleum")) return 16f;
        if (name.Contains("wall") || name.Contains("fence") || name.Contains("hedge")) return 4f;
        if (name.Contains("rock") || name.Contains("boulder") || name.Contains("stone")) return 4f;
        if (name.Contains("bridge") || name.Contains("cart") || name.Contains("wagon")) return 6f;
        if (name.Contains("tent")) return 6f;
        if (name.Contains("statue") || name.Contains("fountain") || name.Contains("well")) return 5f;
        if (name.Contains("support") || name.Contains("conveyor") || name.Contains("platform") ||
            name.Contains("corral")) return 5f;

        return 4f; // generic default - conservative, see comment above
    }

    public bool IsBlocked(Vector4 point)
    {
        foreach (var cell in CellsCovering(point, 0f))
        {
            if (!_cells.TryGetValue(cell, out var list))
                continue;

            foreach (var obstacle in list)
            {
                var dx = obstacle.Position.X - point.X;
                var dz = obstacle.Position.Z - point.Z;
                if (dx * dx + dz * dz <= obstacle.Radius * obstacle.Radius)
                    return true;
            }
        }
        return false;
    }

    // Samples along the a->b segment (2D, X/Z only - matches how obstacles are stored) and rejects the
    // edge if any sample point falls inside an obstacle's footprint.
    public bool IsLineWalkable(Vector4 a, Vector4 b)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.001f)
            return true;

        const float sampleSpacing = 2f;
        var steps = Math.Max(1, (int)(length / sampleSpacing));

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            var sample = new Vector4(a.X + dx * t, a.Y, a.Z + dz * t, 1f);
            if (IsBlocked(sample))
                return false;
        }
        return true;
    }

    private static IEnumerable<(int, int)> CellsCovering(Vector4 position, float radius)
    {
        var minX = (int)MathF.Floor((position.X - radius) / CellSize);
        var maxX = (int)MathF.Floor((position.X + radius) / CellSize);
        var minZ = (int)MathF.Floor((position.Z - radius) / CellSize);
        var maxZ = (int)MathF.Floor((position.Z + radius) / CellSize);

        for (var cx = minX; cx <= maxX; cx++)
            for (var cz = minZ; cz <= maxZ; cz++)
                yield return (cx, cz);
    }
}
