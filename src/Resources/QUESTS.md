# Adding Quests

Quests are entirely data-driven: everything from goals to rewards to NPC gating lives in
[`Quests.json`](Quests.json). Adding a quest means adding a JSON entry - no C# code changes
required.

`Quests.json` is loaded once at startup by `QuestDefinitionCollection.Load` (called from
`ResourceManager`) into `src/Sanctuary.Game/Resources/Definitions/QuestDefinition.cs` /
`QuestGoal.cs` - read those two files for the authoritative field list. This doc is a guide for
using them, not a substitute.

## Before you start

`GiverGuid` and `TargetGuid` must be the guid of an NPC already spawned in a zone script (the
`100000000000 + definitionId` convention used by `zone.spawnNpcWithGuid` calls, e.g.
`src/Scripts/Zone/FabledRealms.lua`) - not the `Npcs.json` definition id itself.

There's no validation: if the guid doesn't match a spawned NPC, the quest silently can never be
offered or turned in - no error, no log. `/npc spawn <definitionId>` (admin-only) prints a guid you
can use for ad hoc testing.

## Minimal quest

The smallest valid entry is a single "talk to this NPC" quest with no reward:

```json
{
  "Comment": "My New Quest",
  "QuestId": 5000,
  "TitleId": 0,
  "DescriptionId": 0,
  "GiverDialogueId": 0,
  "ObjectiveDescriptionId": 0,
  "SubGoalId": 0,
  "TargetDialogueId": 0,
  "IconId": 43278,
  "GiverGuid": 100000001201,
  "TargetGuid": 100000001201,
  "RewardCoins": 0,
  "RewardExperience": 0,
  "RewardItems": [],
  "PrerequisiteQuestId": 0,
  "NextQuestId": 0
}
```

`Comment` is ignored by the loader - it's purely a label for whoever's reading the file. `QuestId`
must be unique; a duplicate is skipped with a startup warning. `IconId` 43278 is what every
existing quest uses.

With no `Goals` array, the quest falls back to a single synthesized "talk to `TargetGuid`" goal
built from `ObjectiveDescriptionId`/`SubGoalId`/`TargetDialogueId` (see
`QuestDefinition.EffectiveGoals`). This is the legacy shape and still works fine for simple
give-and-turn-in-at-the-same-NPC quests (see `QuestId: 3001` "Nomi's Little Brother" style quests
in `Quests.json` for a real single-goal example, or `3010`/`3011` for a real multi-step chain).

## Multi-goal quests

For anything with more than one step, use `Goals` - an ordered checklist. Goals complete in order;
the active goal is the first incomplete one, and the quest is ready to turn in once every goal is
done. Each goal becomes its own tracker row.

```json
"Goals": [
  { "NameId": 100103, "DescriptionId": 100104, "DialogueId": 384144, "TargetGuid": 100000003018 },
  { "NameId": 94511,  "DescriptionId": 94512,  "DialogueId": 94257,  "TargetGuid": 100000002049 }
]
```

- `NameId` is the tracker row text AND the goal's client-side identity - it must be unique across
  a quest's goals, or the client can't tell them apart (checkmarks/progress won't render right).
  The loader warns on a duplicate.
- `DialogueId` on the **final** goal becomes the turn-in speech bubble. On an *intermediate* goal
  it's the mid-quest NPC reply, but only `TalkToNpc` goals actually pop it - the other types fire
  from field events where there's no NPC to camera-focus.
- `Type` defaults to `0` (TalkToNpc) if omitted.

Every goal's row is added to the quest helper as soon as the quest is taken, so the player sees the
whole checklist - completed, current, and still to come - not just the step they're on.

### Multi-turn NPC conversations

`DialogueId` is one bubble with a generic "You got it!" button. When the NPC and player go
back-and-forth, use `Dialogue` instead - an ordered list of turns, each the NPC's line plus the
caption for the player's reply:

```json
{
  "NameId": 101404,
  "TargetGuid": 100000001758,
  "Dialogue": [
    { "TextId": 101414, "ResponseTextId": 101415 },
    { "TextId": 101416, "ResponseTextId": 103597 }
  ]
}
```

Emuzz says "...what are you trying to steal from me?", the button reads "Yancy Gilbert sent me.",
and clicking it plays her real answer instead of closing the dialog. The conversation ends (camera
restores) on the last turn's click. `ResponseTextId` of `0` falls back to "You got it!", so a
one-entry `Dialogue` is just `DialogueId`; `Dialogue` wins over `DialogueId` when both are set.

The button's icon is picked automatically to match retail: a **plus** (`ui_dialog_plus`, 303) while
the NPC still has an answer coming, and the orange **curved leave arrow** (`ui_dialog_leave`, 4008)
on the reply that ends the conversation. Nothing to author.

On a **counted talk** goal, per-NPC replies go in `TargetResponseIds`, index-aligned with
`TargetDialogueIds`.

Don't use `Dialogue` on the **final** goal: that bubble is the turn-in end screen (a different UI,
fed by `TurnInDialogueId`), which has no response buttons to advance. Keep `DialogueId` there.

## Goal types

| Type | Value | Completes when... |
|---|---|---|
| `TalkToNpc` | 0 | player interacts with `TargetGuid` (or, with `RequiredCount` > 1, with `RequiredCount` of the NPCs in `TargetGuid`/`TargetGuids` - see below) |
| `ReachLocation` | 1 | player gets within `ReachRadius` (default 12) of `ReachPosition`, checked on every position update (2D, X/Z only) |
| `Collect` | 2 | player gathers `RequiredCount` pickups from `CollectSpawns` |
| `Kill` | 3 | player defeats `RequiredCount` NPCs matching `KillNpcNameId` / `KillNpcNameIds` |
| `EncounterComplete` | 4 | player **wins** the battle instance whose activity id is `EncounterId` |

`Kill`, `Collect` and `EncounterComplete` goals never advance by talking to an NPC - they credit
only from their own events (`OnNpcKilled` / `OnCollectInteract` / `OnEncounterComplete`), so
`QuestManager.OnNpcInteract` deliberately skips them.

### Counted talk goals

Retail sometimes gives several interchangeable NPCs **one** tracker row with a counter - "Talk to
Freewheelers - 0/3" - instead of a row each. That can't be modelled as three `TalkToNpc` goals,
because each row needs its own unique `NameId` and the client string table only has the one plural
string. Set `RequiredCount` on a `TalkToNpc` goal and list the extra NPCs in `TargetGuids`:

```json
{
  "NameId": 438985,
  "RequiredCount": 3,
  "TargetGuid": 100000040032,
  "TargetGuids": [100000040033, 100000040034],
  "TargetDialogueIds": [438971, 438973, 438975]
}
```

Each NPC credits the counter once (talking to the same one twice does nothing but replay their
line), and the goal ticks off on the last one. `TargetDialogueIds` is optional and index-aligned
with `TargetGuid` first then `TargetGuids`, so each NPC speaks their own reply; anything it doesn't
cover falls back to the goal's `DialogueId`. The objective marker walks the player to the nearest
NPC they haven't reached yet. `RequiredCount` of 1 (or omitted) is the ordinary single-NPC goal.

Like a Collect goal's count, progress persists across relog via `DbCharacterQuest.GoalCount`, but
*which* NPCs were credited doesn't - so a relog mid-step lets an already-credited NPC be talked to
again. That errs toward the player and never loses progress; see `Player.TalkedQuestNpcs`.

### ReachLocation example

Use `/whereami` in-game to grab coordinates while standing where you want the goal to trigger.

```json
{ "NameId": 93294, "Type": 1, "ReachPosition": [-690.38, 2.3, -1060.25], "ReachRadius": 15 }
```

The check is 2D (X/Z only); the Y coordinate only feeds the map pin.

### Collect example

```json
{
  "NameId": 46489,
  "Type": 2,
  "RequiredCount": 8,
  "CollectModelId": 584,
  "CollectNameId": 74449,
  "CollectSpawns": [
    [-1090.0, 5.40, 384.0],
    [-1072.0, 5.30, 348.0]
  ]
}
```

`RequiredCount` of `0` (or omitted) defaults to "collect them all" (`CollectSpawns.Count`), so
place at least `RequiredCount` spawns. `CollectModelId` is a `Models.txt` id (e.g. `93` =
`bw_collectible_mushrooms_01`); `CollectNameId` is the hover/name text shown on the pickup.

No script wiring needed: `StartingZone.OnStart` calls `SpawnQuestCollectibles()` itself, which spawns
**every** collectible pickup across **all** quests in `Quests.json`. Just make sure the positions are
in that zone.

### Kill example

```json
{ "NameId": 76191, "Type": 3, "RequiredCount": 6, "KillNpcNameId": 76190 }
```

`KillNpcNameId` is the victim's `NameId`, not a definition or guid. Every world NPC with that
`NameId` is made hostile/damageable at spawn. For hunts where several NPC variants count toward the
same goal (a camp with Soldiers, Guardians and Magi), add the extras to `KillNpcNameIds` - the
single id and the list are combined, and every listed id is made hostile too.

### EncounterComplete example

```json
{ "NameId": 93820, "Type": 4, "EncounterId": 174 }
```

`EncounterId` is the activity id of a battle instance (e.g. `174` = the Frostfang Growler arena).
The goal credits on a **win** only - losing or leaving the instance does nothing. The encounter has
to already exist; this goal type wires a quest to it, it doesn't create one.

## Chaining and gating

- `PrerequisiteQuestId`: must be completed before this quest can be offered. `0` = none.
- `NextQuestId`: purely cosmetic automation - once this quest completes, the next quest's giver
  badge refreshes immediately (no relog needed) so players see it's available right away. It does
  **not** replace setting that quest's own `PrerequisiteQuestId`.
- `ExcludesQuestIds`: quests that block this one while active *or* completed, and vice versa - list
  both directions. Used for the two race-specific "Introduce Yourself" quests (`2563`/`2564`) so a
  player only ever gets one. Abandoning a quest clears it from the player's quest state, which lifts
  the exclusion automatically, and the excluded quests' giver badges refresh at the same time.

## Rewards

- `RewardCoins`, `RewardExperience` - plain ints, granted on completion.
- `RewardItems` - a list of item definition ids added to the player's bags. These **are**
  validated against the item definitions at grant time; an unknown id won't grant (check the
  server log if a reward item silently doesn't show up).

## Badges

`NotificationAvailable` (default `2`, the "!" icon) and `NotificationActive` (default `6`, the "?"
icon) control the world badge shown above the giver/target's head. You'll rarely need to change
these - the defaults match every existing quest.

## Text ids

`TitleId`, `DescriptionId`, `GiverDialogueId`, goal `NameId`/`DescriptionId`/`DialogueId`, etc. are
SOE T4 client localization ids (resolved client-side as `Global.Text.<id>`). There's no
server-side validation - any int is accepted - but an id with no matching client string just shows
as unresolved text in-game. You need ids that already exist in the client's string table; this
repo can't add new client-side strings.

## No database migration needed

`CharacterQuests` stores `QuestId, CharacterId, Completed, GoalProgress, GoalCount, IsActive`
generically - adding quests to `Quests.json` is purely additive and needs no EF Core migration.

## Checklist

1. Confirm the giver/target NPCs are already spawned in a zone script - note their guids.
2. Pick a unique `QuestId`.
3. Write the entry: single goal (legacy fields) or a `Goals` list for multi-step.
4. Wire `PrerequisiteQuestId`/`NextQuestId` if it's part of a chain, `ExcludesQuestIds` if it's
   mutually exclusive with another quest.
5. If it has a `Collect` goal, check the pickup positions are inside the zone that spawns them.
6. Build, spawn any test NPCs with `/npc spawn`, and walk through accept -> goals -> turn-in
   in-client.
