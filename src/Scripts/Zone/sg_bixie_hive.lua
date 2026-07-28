-- Bixie Hive (DungeonDefinition.cs ActivityId 37, world sg_bixie_hive).
--
-- Real layout from a hand-captured coordinate sheet (2026-07-28). Each NPC/ASSET row on the sheet is a
-- MARKER, not an individual: the coordinate is the real captured point for that pack, and the "PACK OF N"
-- note is how many of that enemy spawn scattered around it - the engine does the scattering via
-- zone.addSpawnArea, not hand-plotted per-individual points (same convention as Cracked Claw Caverns).
--
-- Order MUST match the Dungeon.Enemies group order in DungeonDefinition.cs: 63 Unruly Warrior (across the
-- 12 real pack markers), then 26 Unruly Mage (across the 7 real pack markers, each stationed behind its
-- own warrior pack per the sheet's notes), then 2 lone Unruly Elite. getSpawnPoints must report EXACTLY 91
-- points total (addSpawnArea expands into that many individual points internally) or
-- EncounterArenaZone.BuildDungeonSpawns falls back to the procedural layout instead.
--
-- NOT covered here (spawned separately, see DungeonCatalog[37] in DungeonDefinition.cs): the 2 "Bixie
-- Tunnel" spawners (FrogLogPositions), the captive Bixie Queen escort + her 2 post-rescue reinforcement
-- waves + Drone Fauzz the boss (EscortStages), and the 4 "Frightened Bixie Worker" rescue props
-- (BonusInteract) - none of those are part of the fixed 91-enemy starting roster this script places.
function getSpawnPoints(zone)
    -- 12 real Unruly Warrior pack markers (63 total)
    zone.addSpawnArea(314.30, 81.44, 260.38, 4)
    zone.addSpawnArea(276.95, 80.20, 265.44, 3)
    zone.addSpawnArea(255.35, 79.98, 293.12, 4)
    zone.addSpawnArea(290.01, 80.47, 306.65, 6)
    zone.addSpawnArea(249.68, 80.57, 339.67, 10)
    zone.addSpawnArea(197.77, 81.77, 334.52, 5)
    zone.addSpawnArea(179.60, 79.91, 379.73, 5)
    zone.addSpawnArea(202.48, 82.15, 299.07, 4)
    zone.addSpawnArea(171.46, 80.04, 411.62, 6)
    zone.addSpawnArea(141.59, 80.50, 427.69, 8)
    zone.addSpawnArea(112.72, 79.20, 399.78, 5)
    zone.addSpawnArea(112.73, 80.86, 363.00, 3)

    -- 7 real Unruly Mage pack markers (26 total), each behind its own warrior pack per the sheet
    zone.addSpawnArea(273.18, 80.08, 268.88, 3)
    zone.addSpawnArea(252.78, 80.28, 296.22, 4)
    zone.addSpawnArea(296.56, 82.49, 310.06, 6)
    zone.addSpawnArea(199.54, 83.01, 294.30, 4)
    zone.addSpawnArea(170.78, 79.96, 421.27, 3)
    zone.addSpawnArea(110.27, 80.03, 396.10, 3)
    zone.addSpawnArea(113.34, 82.09, 352.61, 3)

    -- 2 lone Unruly Elite (no "pack of" note on the sheet - one each)
    zone.addSpawnPoint(233.16, 80.97, 309.70)
    zone.addSpawnPoint(173.79, 79.81, 385.20)
end
