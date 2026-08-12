using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sanctuary.Game.Pathfinding;

// Routes between two world positions, or null when nothing can route them. Supplied by the zone
// (BaseZone.TryFindPath), which prefers the native ".map" graph and falls back to its WaypointGraph -
// so a mover doesn't need to know or care which source its zone actually has.
public delegate List<Vector4>? PathQuery(Vector4 start, Vector4 goal);

// Per-mover routing state for ChaseNavigator. One instance per chasing entity, carried across ticks.
// A* isn't free, so the route is cached here and only replanned when it runs out, goes stale (the target
// moved far enough), or its refresh timer elapses.
public class PathChaseState
{
    public List<Vector4>? CachedPath;
    public int PathIndex;
    public long NextRepathTicks;
    public Vector4 PathTarget; // the position CachedPath was planned toward, for staleness checks

    // Stuck detection. The waypoint graph is a sampled approximation of real wall data, not a
    // guaranteed-connected navmesh, so a fragmented region can hand back a stale/dead-end route (or
    // nothing, falling back to a straight line INTO the very obstacle that made the search trigger).
    // Rather than trying to make graph construction perfect for every world's geometry, this detects the
    // SYMPTOM: a mover actively trying to move that barely progresses over a full window is following a
    // bad route - discard it and force a fresh plan.
    public Vector4 StuckCheckPos;
    public long NextStuckCheckTicks;

    // Drop the cached route (used when a mover changes what it's chasing).
    public void ResetPath()
    {
        CachedPath = null;
        PathIndex = 0;
        NextRepathTicks = 0;
    }
}

// Shared obstacle-aware steering, extracted from CombatEncounterZone.ChaseStep (2026-08-05) so the
// overworld CombatNpc AI, the dungeon encounter AI, and "Take Me There" all route over the same
// machinery instead of three separately-drifting copies - the overworld enemies previously had no
// obstacle awareness at all and walked straight through walls.
//
// Returns the direction + remaining distance to steer along this tick: the plain straight-line vector
// when there's no obstacle data or the straight line is genuinely clear (the common case - identical
// output to a naive chase, so open ground is unaffected), or a step along a cached A* route when it's
// blocked. Falls back to the straight line rather than freezing when the graph is missing or the
// endpoints are in disconnected components.
public static class ChaseNavigator
{
    // Long enough that normal per-tick movement noise (attack pauses, waypoint corners) doesn't
    // false-positive, short enough that a genuinely bad route gets abandoned quickly instead of grinding
    // into a wall for many seconds.
    private const float StuckThreshold = 1.2f;
    private const int StuckCheckIntervalMs = 1200;

    // How far the target must move before a cached route counts as stale (squared units).
    private const float TargetMovedThresholdSq = 16f;
    private const int RepathIntervalMs = 1000;

    public static (Vector2 Dir, float Dist) Step(
        Vector3 here,
        Vector3 target,
        PathChaseState state,
        ObstacleMap? obstacles,
        PathQuery? findPath,
        long now)
    {
        // Stuck detection: if a cached route hasn't actually moved us over the last window, it's leading
        // into a dead end - drop it so the repath check below plans fresh instead of faithfully re-walking
        // the same bad route every tick.
        if (state.NextStuckCheckTicks == 0)
        {
            state.StuckCheckPos = new Vector4(here.X, here.Y, here.Z, 1f);
            state.NextStuckCheckTicks = now + StuckCheckIntervalMs;
        }
        else if (now >= state.NextStuckCheckTicks)
        {
            var progress = Vector2.Distance(new Vector2(here.X, here.Z), new Vector2(state.StuckCheckPos.X, state.StuckCheckPos.Z));
            if (progress < StuckThreshold && state.CachedPath is not null)
            {
                state.CachedPath = null;
                state.NextRepathTicks = 0; // force the repath check below to actually replan this tick
            }
            state.StuckCheckPos = new Vector4(here.X, here.Y, here.Z, 1f);
            state.NextStuckCheckTicks = now + StuckCheckIntervalMs;
        }

        var toTarget = new Vector2(target.X - here.X, target.Z - here.Z);
        var distToTarget = toTarget.Length();
        if (distToTarget <= 0.01f)
            return (Vector2.Zero, 0f);
        var straight = toTarget / distToTarget;

        var hereV4 = new Vector4(here.X, here.Y, here.Z, 1f);
        var targetV4 = new Vector4(target.X, target.Y, target.Z, 1f);

        if (obstacles is null || obstacles.IsLineWalkable(hereV4, targetV4))
        {
            state.CachedPath = null;
            return (straight, distToTarget);
        }

        var targetMoved = Vector2.DistanceSquared(
            new Vector2(state.PathTarget.X, state.PathTarget.Z),
            new Vector2(target.X, target.Z)) > TargetMovedThresholdSq;

        if (state.CachedPath is null || state.PathIndex >= state.CachedPath.Count || now >= state.NextRepathTicks || targetMoved)
        {
            state.CachedPath = findPath?.Invoke(hereV4, targetV4);
            state.PathIndex = 0;
            state.PathTarget = targetV4;
            state.NextRepathTicks = now + RepathIntervalMs;
        }

        if (state.CachedPath is not { Count: > 0 })
            return (straight, distToTarget); // no graph, or disconnected - straight-line fallback, not a freeze

        var here2 = new Vector2(here.X, here.Z);
        while (state.PathIndex < state.CachedPath.Count - 1 &&
               Vector2.DistanceSquared(here2, new Vector2(state.CachedPath[state.PathIndex].X, state.CachedPath[state.PathIndex].Z)) < 1f)
            state.PathIndex++;

        var waypoint = state.CachedPath[state.PathIndex];
        var toWaypoint = new Vector2(waypoint.X - here.X, waypoint.Z - here.Z);
        var distToWaypoint = toWaypoint.Length();
        return distToWaypoint > 0.01f ? (toWaypoint / distToWaypoint, distToWaypoint) : (straight, distToTarget);
    }
}
