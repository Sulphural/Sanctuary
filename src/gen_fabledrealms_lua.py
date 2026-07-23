#!/usr/bin/env python3
# Regenerate Scripts/Zone/FabledRealms.lua from Resources/Npcs.json.
#
# The starting zone's NPC roster is spawned by that .lua script (StartingZone.TrySpawnNpc), one
# zone.spawnNpcWithGuid(id, guid, x, y, z, heading) call per Npcs.json entry, in file order (the order
# matters: the FIRST Tormented Spirit entry found becomes the dungeon-entrance spirit). guid = the same
# NpcGuidBase (100000000000) + id scheme StartingZone.cs and NpcVendors.json/Quests.json already use.
#
# Usage: python gen_fabledrealms_lua.py
# Re-run any time Npcs.json changes; do not hand-edit the generated spawn calls.

import json
import os

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "Resources", "Npcs.json")
DST = os.path.join(ROOT, "Scripts", "Zone", "FabledRealms.lua")
NPC_GUID_BASE = 100000000000

with open(SRC, "r", encoding="utf-8-sig") as f:
    npcs = json.load(f)

lines = [
    "-- Generated from Resources/Npcs.json by gen_fabledrealms_lua.py.",
    "-- Regenerate this file after editing Npcs.json; do not hand-edit spawn calls below.",
    "function onStart(zone)",
]

count = 0
for npc in npcs:
    npc_id = npc["Id"]
    guid = NPC_GUID_BASE + npc_id
    x, y, z = npc.get("SpawnPosition", [0.0, 0.0, 0.0])
    heading = npc.get("SpawnHeading", 0.0)
    lines.append(f"    zone.spawnNpcWithGuid({npc_id}, {guid}, {x!r}, {y!r}, {z!r}, {heading!r})")
    count += 1

lines.append("end")
lines.append("")

with open(DST, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines))

print(f"Wrote {count} spawn calls to {DST}")
