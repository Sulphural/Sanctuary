using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sanctuary.Game.Pathfinding;

// No client-facing navmesh exists anywhere in the extracted assets (the client's Kynapse middleware
// builds its own nav mesh at runtime from terrain/collision geometry the server never sees) - this is a
// hand-rollable substitute: a proximity-linked waypoint graph seeded from real walkable positions
// (curated NPC spawn points), with A* over it. Not as precise as a true navmesh, but real edges instead
// of a single straight line through walls.
public sealed class WaypointGraph
{
    private readonly List<Vector4> _nodes = [];
    private readonly List<List<int>> _edges = [];
    private ObstacleMap? _obstacles;

    public int NodeCount => _nodes.Count;

    // Debug/visualization aid (see CommandRouter's /waypoints) - lets an admin spawn markers at nearby
    // nodes in-game and report back exact node ids for edges that cut through geometry.
    public List<(int Id, Vector4 Position, IReadOnlyList<int> Neighbors)> GetNodesNear(Vector4 position, float radius)
    {
        var result = new List<(int, Vector4, IReadOnlyList<int>)>();
        var radiusSq = radius * radius;
        for (var i = 0; i < _nodes.Count; i++)
        {
            if (DistanceSquared(_nodes[i], position) <= radiusSq)
                result.Add((i, _nodes[i], _edges[i]));
        }
        return result;
    }

    private int AddNode(Vector4 position)
    {
        _nodes.Add(position);
        _edges.Add([]);
        return _nodes.Count - 1;
    }

    private void AddEdge(int a, int b)
    {
        if (a == b)
            return;
        if (!_edges[a].Contains(b))
            _edges[a].Add(b);
        if (!_edges[b].Contains(a))
            _edges[b].Add(a);
    }

    // Builds a graph from a set of known-walkable points: every node links to its K nearest WALKABLE
    // neighbors within maxEdgeDistance (walkable = obstacles is null, or obstacles.IsLineWalkable says the
    // straight segment between them doesn't cross real placed geometry - see ObstacleMap/GcnkParser).
    // maxYDelta rejects candidates on a different floor/elevation despite being close in X/Z (a decent
    // proxy for "not actually the same walkable ground" even with obstacle data, since placements don't
    // cover every possible indoor/upstairs case). O(n^2) candidate generation - fine for a few thousand
    // points as a one-time zone-startup cost; the (more expensive) line-of-sight check only runs on the
    // nearest few candidates per node, not the whole candidate set.
    public static WaypointGraph BuildFromPoints(IReadOnlyList<Vector4> points, float maxEdgeDistance, int maxNeighborsPerNode, float maxYDelta = float.MaxValue, ObstacleMap? obstacles = null)
    {
        var graph = new WaypointGraph { _obstacles = obstacles };
        if (points.Count == 0)
            return graph;

        foreach (var point in points)
            graph.AddNode(point);

        var maxDistSq = maxEdgeDistance * maxEdgeDistance;

        for (var i = 0; i < graph._nodes.Count; i++)
        {
            // (distanceSquared, neighborIndex) candidates within range, kept sorted/trimmed to the K closest.
            var candidates = new List<(float DistSq, int Index)>();

            for (var j = 0; j < graph._nodes.Count; j++)
            {
                if (i == j)
                    continue;

                if (MathF.Abs(graph._nodes[i].Y - graph._nodes[j].Y) > maxYDelta)
                    continue;

                var distSq = DistanceSquared(graph._nodes[i], graph._nodes[j]);
                if (distSq <= maxDistSq)
                    candidates.Add((distSq, j));
            }

            candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

            var added = 0;
            foreach (var (_, candidateIndex) in candidates)
            {
                if (added >= maxNeighborsPerNode)
                    break;

                if (obstacles is not null && !obstacles.IsLineWalkable(graph._nodes[i], graph._nodes[candidateIndex]))
                    continue;

                graph.AddEdge(i, candidateIndex);
                added++;
            }
        }

        return graph;
    }

    private static float DistanceSquared(Vector4 a, Vector4 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float Distance(Vector4 a, Vector4 b) => MathF.Sqrt(DistanceSquared(a, b));

    // Nearest node with a clear line back to `position` - NOT just the nearest node by distance. The
    // entry/exit hops (start -> first node, last node -> destination) are the two segments the graph-
    // building obstacle check never covers (that only validates edges BETWEEN nodes), so without this,
    // routing could avoid every building along the way and still cut straight through one on the very
    // first or last hop - which is exactly what "still hitting things" turned out to be. Falls back to
    // the plain nearest node if nothing within range has a clear line (better to route through/near an
    // obstacle than fail to find a path at all).
    private int FindNearestWalkableNode(Vector4 position)
    {
        if (_nodes.Count == 0)
            return -1;

        var ordered = new List<(float DistSq, int Index)>(_nodes.Count);
        for (var i = 0; i < _nodes.Count; i++)
            ordered.Add((DistanceSquared(_nodes[i], position), i));
        ordered.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

        if (_obstacles is not null)
        {
            foreach (var (_, index) in ordered)
            {
                if (_obstacles.IsLineWalkable(position, _nodes[index]))
                    return index;
            }
        }

        return ordered[0].Index; // no obstacle data, or nothing nearby is clear - fall back to nearest
    }

    // A* from start to destination over the graph, entering/exiting via the nearest WALKABLE node to
    // each (see FindNearestWalkableNode). Returns null if the graph is empty or start/destination land
    // in disconnected components - callers should fall back to a straight line in that case, not fail
    // outright.
    public List<Vector4>? FindPath(Vector4 start, Vector4 destination)
    {
        if (_nodes.Count == 0)
            return null;

        var startNode = FindNearestWalkableNode(start);
        var endNode = FindNearestWalkableNode(destination);

        if (startNode == endNode)
            return [start, destination];

        var openSet = new PriorityQueue<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var gScore = new Dictionary<int, float> { [startNode] = 0f };
        var visited = new HashSet<int>();

        openSet.Enqueue(startNode, Distance(_nodes[startNode], _nodes[endNode]));

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();

            if (current == endNode)
                return ReconstructPath(cameFrom, current, start, destination);

            if (!visited.Add(current))
                continue;

            foreach (var neighbor in _edges[current])
            {
                var tentativeG = gScore[current] + Distance(_nodes[current], _nodes[neighbor]);
                if (gScore.TryGetValue(neighbor, out var existingG) && tentativeG >= existingG)
                    continue;

                gScore[neighbor] = tentativeG;
                cameFrom[neighbor] = current;
                var fScore = tentativeG + Distance(_nodes[neighbor], _nodes[endNode]);
                openSet.Enqueue(neighbor, fScore);
            }
        }

        return null; // disconnected - caller falls back to a straight line
    }

    private List<Vector4> ReconstructPath(Dictionary<int, int> cameFrom, int current, Vector4 start, Vector4 destination)
    {
        var nodePath = new List<int> { current };
        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            nodePath.Add(current);
        }
        nodePath.Reverse();

        var path = new List<Vector4>(nodePath.Count + 2) { start };
        foreach (var node in nodePath)
            path.Add(_nodes[node]);
        path.Add(destination);
        return path;
    }
}
