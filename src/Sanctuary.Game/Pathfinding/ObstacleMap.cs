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
    private readonly record struct WallSegment(Vector4 A, Vector4 B);

    // Real cave/terrain wall boundaries (see GzneParser) block within this margin of the segment - a rough
    // stand-in for a character's collision half-width, same spirit as FootprintRadius's per-prop guesses.
    //
    // WIDENED 2026-07-29 (live feedback: "enemies... taking a path that is blocked then eventually going
    // through it") - 1.5 was thinner than the real cave wall geometry it's approximating: a .gzne wall strip
    // traces the boundary CURVE of the walkable area (not a filled solid), so anything past this margin from
    // the nearest strip segment reads as open space regardless of how much solid rock actually sits there.
    // 1.5 units is barely wider than a character model - too easy for IsLineWalkable's blocked check to miss
    // an actual wall a sample happened to graze the very edge of. Same reasoning as FootprintRadius's own
    // comment ("a false 'blocked' just costs a slightly longer route, but a false 'clear' is exactly the
    // 'walks through buildings' bug this exists to fix").
    private const float WallMargin = 2.5f;

    private const float CellSize = 16f;
    private readonly Dictionary<(int, int), List<Obstacle>> _cells = [];
    private readonly Dictionary<(int, int), List<WallSegment>> _wallCells = [];
    private readonly int _obstacleCount;
    private readonly int _wallSegmentCount;

    private ObstacleMap(List<(Vector4 Position, float Radius)> obstacles, List<(Vector4 A, Vector4 B)> walls)
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

        _wallSegmentCount = walls.Count;
        foreach (var (a, b) in walls)
        {
            var segment = new WallSegment(a, b);
            foreach (var cell in CellsCoveringSegment(a, b, WallMargin))
            {
                if (!_wallCells.TryGetValue(cell, out var list))
                    _wallCells[cell] = list = [];
                list.Add(segment);
            }
        }
    }

    public int ObstacleCount => _obstacleCount;
    public int WallSegmentCount => _wallSegmentCount;

    public static ObstacleMap Build(IReadOnlyList<GcnkParser.Placement> placements, IReadOnlyList<GzneParser.WallStrip>? wallStrips = null)
    {
        var obstacles = new List<(Vector4, float)>(placements.Count);
        foreach (var placement in placements)
        {
            var radius = FootprintRadius(placement.ModelName);
            if (radius > 0f)
                obstacles.Add((placement.Position, radius));
        }

        var walls = new List<(Vector4, Vector4)>();
        if (wallStrips is not null)
        {
            foreach (var strip in wallStrips)
            {
                for (var i = 0; i < strip.Points.Count - 1; i++)
                    walls.Add((strip.Points[i], strip.Points[i + 1]));
            }
        }

        return new ObstacleMap(obstacles, walls);
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
        // Structural cave-wall/tunnel-boundary pieces (e.g. "cave_01_naturalcool_piece02.agr",
        // "cave_02_naturalcool_blockade.agr") - a modular kit for building cave interiors, found 2026-07-26
        // via a properly structured GcnkParser (the earlier ".adr\0"-only string-matching silently missed
        // every one of these, since they use the ".agr" extension - this was the actual root cause of mobs
        // still clipping through tunnel walls after the first placement-based obstacle pass). Sized like a
        // building, not a small prop - these are large modular architecture pieces.
        if (name.Contains("naturalcool_piece") || name.Contains("blockade")) return 12f;
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

        foreach (var cell in CellsCovering(point, WallMargin))
        {
            if (!_wallCells.TryGetValue(cell, out var list))
                continue;

            foreach (var wall in list)
            {
                if (DistanceToSegmentSquared(point, wall.A, wall.B) <= WallMargin * WallMargin)
                    return true;
            }
        }

        return false;
    }

    // 2D (X/Z) squared distance from `point` to the segment a-b.
    private static float DistanceToSegmentSquared(Vector4 point, Vector4 a, Vector4 b)
    {
        var abx = b.X - a.X;
        var abz = b.Z - a.Z;
        var lenSq = abx * abx + abz * abz;
        var apx = point.X - a.X;
        var apz = point.Z - a.Z;

        var t = lenSq > 0.0001f ? (apx * abx + apz * abz) / lenSq : 0f;
        t = Math.Clamp(t, 0f, 1f);

        var cx = a.X + abx * t;
        var cz = a.Z + abz * t;
        var dx = point.X - cx;
        var dz = point.Z - cz;
        return dx * dx + dz * dz;
    }

    // Samples along the a->b segment (2D, X/Z only - matches how obstacles are stored) and rejects the
    // edge if any sample point falls inside an obstacle's footprint.
    //
    // TIGHTENED 2026-07-29 (live feedback: "enemies... taking a path that is blocked then eventually going
    // through it", same root cause as WallMargin above) - 2-unit sample spacing could ALIAS PAST a short
    // wall segment entirely: two consecutive samples straddling a thin blocking strip with neither one
    // landing within WallMargin of it makes the whole line read as clear even though it visibly crosses
    // solid geometry. 1 unit halves that gap.
    public bool IsLineWalkable(Vector4 a, Vector4 b)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.001f)
            return true;

        const float sampleSpacing = 1f;
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

    // Every cell touching the segment's bounding box (padded by margin) - coarser than a true line
    // rasterization, but segments are short (adjacent wall-strip points) so the padding waste is small,
    // and it guarantees IsBlocked's cell lookup at any point within margin of the segment always hits.
    private static IEnumerable<(int, int)> CellsCoveringSegment(Vector4 a, Vector4 b, float margin)
    {
        var minX = (int)MathF.Floor((MathF.Min(a.X, b.X) - margin) / CellSize);
        var maxX = (int)MathF.Floor((MathF.Max(a.X, b.X) + margin) / CellSize);
        var minZ = (int)MathF.Floor((MathF.Min(a.Z, b.Z) - margin) / CellSize);
        var maxZ = (int)MathF.Floor((MathF.Max(a.Z, b.Z) + margin) / CellSize);

        for (var cx = minX; cx <= maxX; cx++)
            for (var cz = minZ; cz <= maxZ; cz++)
                yield return (cx, cz);
    }
}
