-- Cracked Claw Caverns (DungeonDefinition.cs ActivityId 118, world bs_cracked_claw_caverns).
--
-- Real layout from a hand-captured coordinate sheet (2026-07-25): 6 Swamp Cray packs (static, aggro on
-- approach), 2 Elder Swamp Cray (one escorted by 4 extra crays), 13 roaming Venomous Frogs, and the boss
-- Cracked Claw. Each pack's individual crays are scattered in a small ring around its sheet-given center
-- so they don't stack on one point; centers/heights are the real sheet values, not estimates.
--
-- Order MUST match the Dungeon.Enemies group order in DungeonDefinition.cs: 50 Swamp Cray (46 packed +
-- 4 escorts around the 2nd Elder), then 2 Elder Swamp Cray, then 13 Venomous Frog, then the 1 boss.
-- getSpawnPoints must report EXACTLY 66 points total or EncounterArenaZone.BuildDungeonSpawns falls back
-- to the procedural layout instead.
--
-- NOT covered here (no spawner/hatch-on-trigger/interact-objective concept exists yet — see the comment
-- on DungeonCatalog[118] in DungeonDefinition.cs): the 3 Frog Log spawners, the Swamp Cray Brood hatching
-- from eggs in the boss room, and the "release the trapped spirits" bonus objective.
function getSpawnPoints(zone)
    -- Swamp Cray pack 1 (10) — near (228.14, 30.35, 228.99)
    zone.addSpawnPoint(233.14, 30.35, 228.99)
    zone.addSpawnPoint(232.19, 30.35, 231.93)
    zone.addSpawnPoint(229.69, 30.35, 233.75)
    zone.addSpawnPoint(226.59, 30.35, 233.75)
    zone.addSpawnPoint(224.09, 30.35, 231.93)
    zone.addSpawnPoint(223.14, 30.35, 228.99)
    zone.addSpawnPoint(224.09, 30.35, 226.05)
    zone.addSpawnPoint(226.59, 30.35, 224.23)
    zone.addSpawnPoint(229.69, 30.35, 224.23)
    zone.addSpawnPoint(232.19, 30.35, 226.05)

    -- Swamp Cray pack 2 (9) — near (159.48, 40.68, 319.66)
    zone.addSpawnPoint(163.98, 40.68, 319.66)
    zone.addSpawnPoint(162.93, 40.68, 322.55)
    zone.addSpawnPoint(160.26, 40.68, 324.09)
    zone.addSpawnPoint(157.23, 40.68, 323.56)
    zone.addSpawnPoint(155.25, 40.68, 321.20)
    zone.addSpawnPoint(155.25, 40.68, 318.12)
    zone.addSpawnPoint(157.23, 40.68, 315.76)
    zone.addSpawnPoint(160.26, 40.68, 315.23)
    zone.addSpawnPoint(162.93, 40.68, 316.77)

    -- Swamp Cray pack 3 (5) — near (184.96, 30.35, 284.36)
    zone.addSpawnPoint(188.46, 30.35, 284.36)
    zone.addSpawnPoint(186.04, 30.35, 287.69)
    zone.addSpawnPoint(182.13, 30.35, 286.42)
    zone.addSpawnPoint(182.13, 30.35, 282.30)
    zone.addSpawnPoint(186.04, 30.35, 281.03)

    -- Swamp Cray pack 4 (8) — near (149.52, 40.32, 305.74)
    zone.addSpawnPoint(154.02, 40.32, 305.74)
    zone.addSpawnPoint(152.70, 40.32, 308.92)
    zone.addSpawnPoint(149.52, 40.32, 310.24)
    zone.addSpawnPoint(146.34, 40.32, 308.92)
    zone.addSpawnPoint(145.02, 40.32, 305.74)
    zone.addSpawnPoint(146.34, 40.32, 302.56)
    zone.addSpawnPoint(149.52, 40.32, 301.24)
    zone.addSpawnPoint(152.70, 40.32, 302.56)

    -- Swamp Cray pack 5 (9) — near (157.98, 40.04, 261.19)
    zone.addSpawnPoint(162.48, 40.04, 261.19)
    zone.addSpawnPoint(161.43, 40.04, 264.08)
    zone.addSpawnPoint(158.76, 40.04, 265.62)
    zone.addSpawnPoint(155.73, 40.04, 265.09)
    zone.addSpawnPoint(153.75, 40.04, 262.73)
    zone.addSpawnPoint(153.75, 40.04, 259.65)
    zone.addSpawnPoint(155.73, 40.04, 257.29)
    zone.addSpawnPoint(158.76, 40.04, 256.76)
    zone.addSpawnPoint(161.43, 40.04, 258.30)

    -- Swamp Cray pack 6 (5) — near (191.36, 40.13, 260.19)
    zone.addSpawnPoint(194.86, 40.13, 260.19)
    zone.addSpawnPoint(192.44, 40.13, 263.52)
    zone.addSpawnPoint(188.53, 40.13, 262.25)
    zone.addSpawnPoint(188.53, 40.13, 258.13)
    zone.addSpawnPoint(192.44, 40.13, 256.86)

    -- 4 escort Swamp Cray around Elder Swamp Cray #2 (132.90, 40.31, 240.29)
    zone.addSpawnPoint(135.90, 40.31, 240.29)
    zone.addSpawnPoint(132.90, 40.31, 243.29)
    zone.addSpawnPoint(129.90, 40.31, 240.29)
    zone.addSpawnPoint(132.90, 40.31, 237.29)

    -- Elder Swamp Cray (2)
    zone.addSpawnPoint(199.14, 30.34, 278.76)
    zone.addSpawnPoint(132.90, 40.31, 240.29)

    -- Venomous Frogs (13) — roam within the area around (249.29, 41.11, 224.76)
    zone.addSpawnPoint(258.29, 41.11, 224.76)
    zone.addSpawnPoint(257.26, 41.11, 228.94)
    zone.addSpawnPoint(254.39, 41.11, 232.17)
    zone.addSpawnPoint(250.37, 41.11, 233.70)
    zone.addSpawnPoint(246.10, 41.11, 233.17)
    zone.addSpawnPoint(242.54, 41.11, 230.71)
    zone.addSpawnPoint(240.55, 41.11, 226.92)
    zone.addSpawnPoint(240.55, 41.11, 222.60)
    zone.addSpawnPoint(242.54, 41.11, 218.81)
    zone.addSpawnPoint(246.10, 41.11, 216.35)
    zone.addSpawnPoint(250.37, 41.11, 215.82)
    zone.addSpawnPoint(254.39, 41.11, 217.35)
    zone.addSpawnPoint(257.26, 41.11, 220.58)

    -- Cracked Claw, the boss (1)
    zone.addSpawnPoint(118.67, 41.28, 231.38)
end
