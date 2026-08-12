using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Extensions.Logging;

namespace Sanctuary.Game.Pathfinding;

// Single place that turns a world name into a real ObstacleMap. Both kinds of geometry matter and they
// cover different things, so a caller that loads only one silently pathfinds through the other:
//
//   .gcnk  - per-tile placement files: discrete placed props (trees, buildings, decorative clutter).
//   .gzne  - the world's single zone file: the REAL cave/terrain WALL boundary (see GzneParser).
//
// This existed twice before, once in StartingZone (overworld "Take Me There") and once in
// CombatEncounterZone (dungeon mob chase), with the asset path hardcoded in both - and the overworld copy
// only ever parsed .gcnk. So the overworld's obstacle map had ZERO wall coverage while the dungeons' had
// full coverage from the same on-disk data: Take Me There happily routed straight through terrain that a
// dungeon mob would correctly walk around. Sharing one loader is what keeps those two honest about each
// other.
//
// The assets are EXTERNAL to this repo (the game client's own files, not something we ship or commit), so
// every path here is best-effort: a missing directory or a world with no geometry on disk returns null -
// NOT an empty map - so callers can tell "no data, fall back to straight lines" apart from "real data that
// happens to contain no obstacles".
public static class ObstacleMapLoader
{
    // Client asset root. External to the repo - see the class comment.
    public const string AssetsDirectory = @"C:\Users\nadim\Desktop\sharedVM\everythingFR\FRAssets\Assets";

    public static bool AssetsAvailable => Directory.Exists(AssetsDirectory);

    // Loads the props (.gcnk) AND walls (.gzne) for `world`. Returns null when the asset directory is
    // absent or the world has neither kind of file, so the caller keeps its straight-line fallback.
    // `wallStrips` is handed back because graph builders want the raw strips too (corner-hug node
    // seeding - see WaypointGraphBuilder.BuildForArea).
    public static ObstacleMap? TryLoad(string world, ILogger? logger, out IReadOnlyList<GzneParser.WallStrip> wallStrips)
    {
        wallStrips = [];

        if (!AssetsAvailable)
            return null;

        var placements = new List<GcnkParser.Placement>();
        foreach (var file in Directory.EnumerateFiles(AssetsDirectory, $"{world}_*.gcnk*", SearchOption.AllDirectories))
        {
            try { placements.AddRange(GcnkParser.ParseFile(file)); }
            catch (Exception ex) { logger?.LogWarning(ex, "Failed to parse {file} while building the obstacle map for {world}.", file, world); }
        }

        var strips = new List<GzneParser.WallStrip>();
        foreach (var file in Directory.EnumerateFiles(AssetsDirectory, $"{world}.gzne*", SearchOption.AllDirectories))
        {
            try { strips.AddRange(GzneParser.ParseFile(file)); }
            catch (Exception ex) { logger?.LogWarning(ex, "Failed to parse {file} while building the wall map for {world}.", file, world); }
        }

        if (placements.Count == 0 && strips.Count == 0)
            return null; // no real geometry for this world - stay null so the caller straight-lines

        wallStrips = strips;
        return ObstacleMap.Build(placements, strips);
    }
}
