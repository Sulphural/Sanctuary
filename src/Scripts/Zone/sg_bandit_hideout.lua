-- Bandit Hideout (DungeonDefinition.cs ActivityId 29, world sg_bandit_hideout).
--
-- These 15 points are BAKED from EncounterArenaZone's procedural walk-through layout (same math it still
-- falls back to for every other dungeon), centered on the REAL floor position measured in-game via /pos
-- (152.21, 20.04, 110.80) after noclipping to the actual walkable ground — the original Bed-sphere-center
-- estimate (153, 34, 168) was well off for this room. Hand-tune freely from here.
--
-- Order MUST match the Dungeon.Enemies group order in DungeonDefinition.cs: 9 regular thugawugs, then the
-- 5 "Big Bandits" (the bonus objective), then the 1 boss (Muggenstomp). getSpawnPoints must report EXACTLY
-- 15 points total or EncounterArenaZone.BuildDungeonSpawns falls back to the procedural layout instead.
function getSpawnPoints(zone)
    -- thugawugs (9) — ModelId 199, 800 HP
    zone.addSpawnPoint(125.610, 20.04, 130.200)
    zone.addSpawnPoint(130.752, 20.04, 128.328)
    zone.addSpawnPoint(133.488, 20.04, 123.589)
    zone.addSpawnPoint(132.538, 20.04, 118.200)
    zone.addSpawnPoint(128.346, 20.04, 114.682)
    zone.addSpawnPoint(122.874, 20.04, 114.682)
    zone.addSpawnPoint(118.682, 20.04, 118.200)
    zone.addSpawnPoint(117.732, 20.04, 123.589)
    zone.addSpawnPoint(120.468, 20.04, 128.328)

    -- Big Bandits (5) — ModelId 200, 900 HP, the "Defeat all of the Big Bandits!" bonus objective
    zone.addSpawnPoint(178.810, 20.04, 158.500)
    zone.addSpawnPoint(182.614, 20.04, 155.736)
    zone.addSpawnPoint(181.161, 20.04, 151.264)
    zone.addSpawnPoint(176.459, 20.04, 151.264)
    zone.addSpawnPoint(175.006, 20.04, 155.736)

    -- Muggenstomp, the boss (1) — ModelId 202, 1600 HP, 1.3x scale, far end of the room
    zone.addSpawnPoint(152.210, 20.04, 190.800)
end
