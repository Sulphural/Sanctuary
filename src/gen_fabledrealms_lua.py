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

# NPCs that stay DEFINED in Npcs.json but must not spawn into the overworld. Held back rather than
# deleted so they can be reused later (they are wanted for their own content, not as world dressing).
# Matched on Name so a re-export of Npcs.json that renumbers ids can't quietly put them back.
DO_NOT_SPAWN = {
    "Abominable Snowman",  # Snowhill, next to Candi Ivy / the Gifting Tree
    "Snowman Invader",
}

# Comment blocks emitted above a given NPC's spawn. They live here rather than in the .lua because that
# file is overwritten wholesale on every run - a hand-added note in it was silently lost once.
NOTES = {
    40032: [
        'Sobering Homecoming\'s three scattered Freewheelers (quest 3081, counted "Talk to Freewheelers 0/3").',
        "Placed beside real Sunstone Valley spawns at their exact ground height, per the doc's descriptions:",
        "by The Rumbledome, outside Wheelie Pete's Roadhouse, and at the Sandscale Oasis entrance.",
    ],
}

with open(SRC, "r", encoding="utf-8-sig") as f:
    npcs = json.load(f)

lines = [
    "-- Generated from Resources/Npcs.json by gen_fabledrealms_lua.py.",
    "-- Regenerate this file after editing Npcs.json; do not hand-edit spawn calls below.",
    "function onStart(zone)",
]

count = 0
held = 0
for npc in npcs:
    if (npc.get("Name") or "").strip() in DO_NOT_SPAWN:
        held += 1
        continue

    npc_id = npc["Id"]

    note = NOTES.get(npc_id)
    if note:
        lines.append("")
        lines.extend(f"    -- {text}" for text in note)

    guid = NPC_GUID_BASE + npc_id
    x, y, z = npc.get("SpawnPosition", [0.0, 0.0, 0.0])
    heading = npc.get("SpawnHeading", 0.0)
    lines.append(f"    zone.spawnNpcWithGuid({npc_id}, {guid}, {x!r}, {y!r}, {z!r}, {heading!r})")
    count += 1

lines.append("end")
lines.append("")

with open(DST, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines))

print(f"Wrote {count} spawn calls to {DST} (held back {held} via DO_NOT_SPAWN)")
