# Zone scripts

Drop a `<ZoneName>.lua` file here to give that zone a script. It runs once, on
zone creation, and its `onStart(zone)` function (if defined) is invoked after
the zone finishes constructing.

Available to every zone script via the injected `zone` table:

- `zone.id` — the zone's numeric instance id
- `zone.name` — the zone's definition name (matches this file's name)
- `zone.spawnNpc(npcId, x, y, z, heading)` — spawn a plain NPC by definition id, auto-assigned guid
- `zone.spawnNpcWithGuid(npcId, guid, x, y, z, heading)` — same, with an explicit guid
- `zone.spawnGatheringNode(modelId, itemDefinitionId, name, x, y, z)` — a harvestable resource node (an ore
  vein). `itemDefinitionId` is what a gather grants; deplete/respawn belongs to `GatheringManager`
- `zone.spawnSnowballPile(x, y, z, heading)` — a Snow Days snowball pile; clicking one hands out the tool
- `zone.spawnQuestCollectible(guid, x, y, z)` — a Collect-goal pickup. The guid is the identity the quest
  system gave it when `Quests.json` loaded, so it binds the pickup to its goal; an unknown guid is refused
- `zone.spawnDungeonEntrance(poiId, x, y, z, heading)` — the clickable widget at a walk-up dungeon's mouth,
  keyed by its atlas POI id. A POI with no dungeon behind it simply places nothing

The prop functions take a position plus only the ids that say *which* prop it is — the model, the real
localized nameplate and the interact behaviour stay in C#, so a script can place one but can't
misconfigure it. Zones that have no such props (every dungeon) log a warning and refuse.

`FabledRealms.lua` is **generated** — see `src/gen_fabledrealms_lua.py`, which builds it from
`Resources/Npcs.json`, `MiningNodes.json`, `SnowballPiles.json`, `Quests.json` and
`PointOfInterests.json`. Edit those and
re-run the generator; hand-edits to its spawn calls are overwritten. Comments that need to survive a
regeneration go in the generator's `NOTES` table, not in the `.lua`.

Zones without a matching `.lua` file here start with no script (this is not
an error — it's logged as a warning and skipped).
