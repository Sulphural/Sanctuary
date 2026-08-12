using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sanctuary.Game.Pathfinding;

// Shared seeding helpers for WaypointGraph.BuildFromPoints. Extracted from
// CombatEncounterZone.BuildMobPathfinding (2026-08-05) so the overworld graph can use the same tricks the
// dungeon graphs already relied on - the overworld was seeded ONLY from curated NPC spawn points, which is
// far sparser than a dungeon's sampled grid and so much more prone to the fragmentation described below.
public static class WaypointGraphBuilder
{
    // Candidate nodes hugging both perpendicular sides of every real wall segment, just outside the wall's
    // own margin. A point set that doesn't deliberately include these can easily miss the one clear cell
    // that hugs a wall closely enough to matter (especially for short/angled strips), leaving the graph
    // fragmented right around wall clusters - exactly where routing matters most. When that happens
    // FindPath silently fails and the caller falls back to a straight line, reproducing the very clipping
    // the obstacle data was added to prevent. Seeding explicit corner candidates is the standard navmesh
    // trick for reliable routing around obstacle edges instead of hoping the sampling lands nearby.
    //
    // `flattenY` replaces every candidate's Y (dungeons: the arena's single floor height). Pass null to
    // keep each wall point's own real Y, which is what varied outdoor terrain needs.
    // `inBounds` optionally rejects candidates outside the playable area - wall strips aren't generated
    // relative to any bounds, so a strip near the edge can otherwise produce a node genuinely off the map.
    public static void AddWallHugPoints(
        List<Vector4> points,
        IReadOnlyList<GzneParser.WallStrip> wallStrips,
        ObstacleMap obstacles,
        float? flattenY = null,
        Func<Vector4, bool>? inBounds = null,
        float hugDistance = 3f)
    {
        float[] hugSigns = [1f, -1f];

        foreach (var strip in wallStrips)
        {
            for (var i = 0; i < strip.Points.Count; i++)
            {
                var p = strip.Points[i];
                // Direction along the wall: the next point, or the previous one at the strip's tail.
                var neighbor = i < strip.Points.Count - 1 ? strip.Points[i + 1] : strip.Points[i - 1];
                var dir = new Vector2(neighbor.X - p.X, neighbor.Z - p.Z);
                if (dir.LengthSquared() < 0.0001f)
                    continue;

                dir = Vector2.Normalize(dir);
                var perp = new Vector2(-dir.Y, dir.X);

                foreach (var sign in hugSigns)
                {
                    var candidate = new Vector4(
                        p.X + perp.X * hugDistance * sign,
                        flattenY ?? p.Y,
                        p.Z + perp.Y * hugDistance * sign,
                        1f);

                    if (inBounds is not null && !inBounds(candidate))
                        continue;
                    if (!obstacles.IsBlocked(candidate))
                        points.Add(candidate);
                }
            }
        }
    }

    // Grid-samples a circular playable area for walkable points. Spacing adapts to the radius so node
    // count (and therefore the O(n^2) graph build) stays bounded regardless of area size. Denser sampling
    // than a first guess is deliberate: a coarse grid frequently left the graph fragmented around wall
    // clusters - see AddWallHugPoints.
    public static List<Vector4> SampleWalkableGrid(Vector4 center, float radius, ObstacleMap obstacles, out float spacing)
    {
        spacing = MathF.Max(3f, radius / 35f);
        var points = new List<Vector4>();

        for (var x = -radius; x <= radius; x += spacing)
        {
            for (var z = -radius; z <= radius; z += spacing)
            {
                if (x * x + z * z > radius * radius)
                    continue; // stay inside the playable circle, not the bounding square

                var p = new Vector4(center.X + x, center.Y, center.Z + z, 1f);
                if (!obstacles.IsBlocked(p))
                    points.Add(p);
            }
        }

        return points;
    }
}
