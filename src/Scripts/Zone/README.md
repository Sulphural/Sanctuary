# Zone scripts

Drop a `<ZoneName>.lua` file here to give that zone a script. It runs once, on
zone creation, and its `onStart(zone)` function (if defined) is invoked after
the zone finishes constructing.

Available to every zone script via the injected `zone` table:

- `zone.id` — the zone's numeric instance id
- `zone.name` — the zone's definition name (matches this file's name)
- `zone.spawnNpc(npcId, x, y, z, heading)` — spawn a plain NPC by definition id, auto-assigned guid
- `zone.spawnNpcWithGuid(npcId, guid, x, y, z, heading)` — same, with an explicit guid

Zones without a matching `.lua` file here start with no script (this is not
an error — it's logged as a warning and skipped).
