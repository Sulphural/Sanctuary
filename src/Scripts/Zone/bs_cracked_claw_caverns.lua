-- Cracked Claw Caverns (DungeonDefinition.cs ActivityId 118, world bs_cracked_claw_caverns).
--
-- Real layout from a hand-captured coordinate sheet (2026-07-25). Each NPC/ASSET row on the sheet is a
-- MARKER, not an individual: the coordinate is the real captured point for that pack/area, and the "Pack
-- of N" note is how many of that enemy spawn scattered around it (confirmed directly by the sheet's
-- author - the engine does the scattering via zone.addSpawnArea, not hand-plotted per-individual points).
--
-- Order MUST match the Dungeon.Enemies group order in DungeonDefinition.cs: 50 Swamp Cray (46 across the
-- 6 real pack markers + 4 escorts around the 2nd Elder), then 2 Elder Swamp Cray, then 13 Venomous Frog,
-- then the 1 boss. getSpawnPoints must report EXACTLY 66 points total (addSpawnArea expands into that many
-- individual points internally) or EncounterArenaZone.BuildDungeonSpawns falls back to the procedural
-- layout instead.
--
-- NOT covered here (no spawner/hatch-on-trigger/interact-objective concept exists yet — see the comment
-- on DungeonCatalog[118] in DungeonDefinition.cs): the 3 Frog Log spawners, the Swamp Cray Brood hatching
-- from eggs in the boss room, and the "release the trapped spirits" bonus objective.
function getSpawnPoints(zone)
    -- 6 real Swamp Cray pack markers
    zone.addSpawnArea(228.14, 30.35, 228.99, 10)
    zone.addSpawnArea(159.48, 40.68, 319.66, 9)
    zone.addSpawnArea(184.96, 30.35, 284.36, 5)
    zone.addSpawnArea(149.52, 40.32, 305.74, 8)
    zone.addSpawnArea(157.98, 40.04, 261.19, 9)
    zone.addSpawnArea(191.36, 40.13, 260.19, 5)

    -- 4 escort Swamp Cray around Elder Swamp Cray #2's marker (132.90, 40.31, 240.29)
    zone.addSpawnArea(132.90, 40.31, 240.29, 4)

    -- Elder Swamp Cray (2 real markers)
    zone.addSpawnPoint(199.14, 30.34, 278.76)
    zone.addSpawnPoint(132.90, 40.31, 240.29)

    -- Venomous Frogs (13, one real marker — "spread out within the area")
    zone.addSpawnArea(249.29, 41.11, 224.76, 13)

    -- Cracked Claw, the boss (1 real marker)
    zone.addSpawnPoint(118.67, 41.28, 231.38)
end
