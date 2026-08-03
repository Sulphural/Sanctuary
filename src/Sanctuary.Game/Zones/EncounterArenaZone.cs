using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Dungeons;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Zones;

// GENERIC data-driven combat dungeon (battle instance). One class runs ANY dungeon defined in
// DungeonCatalog: it reuses the proven Frostfang/Tormented-Spirits pipeline (world entry + ground
// adoption, the combat gate + goals burst, the chase/claw pack AI, death -> win, loot wheel + score +
// exit door, party co-op) but takes the world, arena center, enemy roster, text and XP from a
// DungeonDefinition. Goal is always "defeat every enemy". See DungeonDefinition.cs for the data.
public sealed class EncounterArenaZone : CombatEncounterZone
{
    private sealed class EncounterArenaDefinition : BaseZoneDefinition { }

    public DungeonDefinition Dungeon { get; }
    public int EncounterId => Dungeon.ActivityId;
    // Offset well clear of any real activity id so the bonus goal (e.g. "Big Bandits! 0/5") can't
    // collide with the main "defeat everyone" objective's id (=EncounterId) in the client's goal list.
    private int BonusObjectiveId => 900000 + Dungeon.ActivityId;
    // The Goals-panel row's "Category Prefix" enum value for a bonus goal (see
    // UiObjectiveAddPacket.CategoryPrefixId) - resolves the client's own named template key
    // "ObjectiveCategoryPrefixBonus", i.e. the real "Bonus:" prefix.
    private const int BonusCategoryPrefixId = 4;
    private const int EncounterInstanceId = 1;

    private const int CombatMiniGameType = 4; // client MINI_GAME_TYPE_COMBAT — the goals-pane gate
    // How long to hold the player in the dungeon after clicking the exit door before actually teleporting
    // them out, on a WIN only. The score card + loot wheel packets (WinEncounter) already reach the client
    // well before this (sent at kill time) and the GameOver trigger (EndEncounterForPlayer) fires right
    // before this delay starts - but live feedback showed the teleport packet gets processed fast enough to
    // cut the card off before it can actually render, so the player saw themselves back in the overworld
    // first and the card only caught up after. Delaying just the teleport (not the GameOver signal, which
    // stays immediate) fixes the ordering without touching the "don't gate on the client reporting card
    // dismissal" lesson already documented on EndEncounterForPlayer above.
    // Now the HOLD after the wheel actually stops spinning (see NotifyRewardWheelStopped) rather than
    // timed from the door click directly - live feedback wanted the clock to start once the player has
    // actually seen what they won, not from an arbitrary earlier point.
    private const int WinCardDelayMs = 45000;
    // Safety net if the client never reports the wheel stopping (dropped packet, alt-tab, etc.) - without
    // this the player would be stuck in the dungeon forever once they've clicked the door.
    private const int WinReturnFallbackMs = 120000;
    // KnockoutLimit + the knockout/fail/revive lifecycle now live in CombatEncounterZone.
    // (2026-07-25: briefly added a 3rd "Don't get knocked out N times!" Goals-pane row here - REVERTED,
    // live-confirmed retail only shows 2 rows (Primary + Bonus), the knockout limit is not one of them.)

    // Enemy recipe (Frostfang pack-wolf / spirit recipe).
    private const int MobActiveProfile = 151;
    private const int SpawnPoofFxId = 46;
    private const int DeathPoofFxId = 5017;
    private const int DeathHoldMs = 1500;

    // Per-kill XP (added 2026-07-29, live feedback: "dungeon/encounter enemies should give a small amount
    // of exp when killing them") - every kill inside a dungeon used to award NOTHING; only Dungeon.Xp at
    // the very end (WinEncounter) ever touched AwardXp, so a 90-enemy dungeon like Bixie Hive paid the exact
    // same total XP as a 10-enemy one. A flat SMALL trickle per real kill, scaled up a bit for a mini-boss
    // (Boss=true), on the same small-number scale Dungeon.Xp itself already uses across every dungeon
    // (12-38) - not wiki-sourced (no real per-kill dungeon XP data exists anywhere), same ours-to-tune
    // status as every other small-number constant in this file.
    private const int PerKillXp = 3;
    private const int PerKillBossXp = 10;
    // Real client effect "PFX_dirt_brown_exp_sph_lg_troll-despawn" (ActorCompositeEffectDefinitions.xml) -
    // a dirt-explosion-sphere effect the client already ties to a despawn event, the closest real match to
    // "dirt explosion" for the Frog Log's destruction.
    private const int FrogLogDestroyFxId = 16297;
    private const int CharState_Baseline = 0x1;
    private const int CharState_Charging = 0x8001;
    private const int MovementTypePhysics = 2;

    // Chase/claw tuning lives in CombatEncounterZone now; only the approach-aggro range is per-zone here.
    private const float AggroRange = 16f;
    // How close a player must walk to the escort NPC (e.g. Bixie Queen) before her ambient greeting fires -
    // unsourced estimate (no ground-truth range for this), sized a bit tighter than AggroRange since this
    // is "you've noticed her," not a combat trigger.
    private const float EscortGreetRange = 10f;

    // Exit door (Frostfang/Spirits recipe).
    private const int DoorModelId = 846;
    private const int DoorNameId = 4826;
    private const float DoorScale = 1.2f;
    private const int DoorInteractRange = 125;
    private const int DoorActiveProfile = 28;
    private const int DoorCursorId = 17;
    private const int DoorMinimapImageId = 186;
    private const int DoorBadgeType = 7;
    private const int DoorBadgeUnknown3 = 102;

    // Wander (live feedback 2026-07-28, Bixie Hive's reinforcement wave: "make the last set of npcs that
    // spawn at the end wander around a bit") - opt-in (Wander=false by default, zero-risk for the pre-placed
    // static roster/every other dungeon): while idle and not yet in aggro range, amble around Home instead
    // of standing frozen. Same pattern as FrostfangArenaZone.TickRoamer, scoped to this zone's own MobState.
    private sealed class MobState : EncounterMobState
    {
        public bool Wander;
        public Vector2? WanderTarget;
        public long WanderPauseUntil;

        // Converge-on-escort (DungeonEscortStage.ConvergeOnEscort, live feedback 2026-07-29: "the last set
        // of bee enemies should spawn then start running over towards where the queen is"). Checked BEFORE
        // Wander in the tick loop so a converging mob runs straight for the escort instead of ambling; a
        // player within AggroRange still takes priority (see the tick loop), so the charge is interceptable.
        // CORRECTED 2026-07-29 (live feedback: "they are teleporting to the queen until they get close and
        // start walking.. i want them to walk all the way... with good pathfinding") - the first version
        // moved these mobs with its own bespoke straight-line stepper that (a) never sent
        // PlayerUpdatePacketExpectedSpeed, which a PHYSICS-actor client needs to INTERPOLATE between the
        // 10Hz position updates instead of snapping straight to each one (the same reason CombatNpc.
        // MoveTowards sends it for the overworld AI) - explains "teleporting" turning into real walking the
        // moment BeginCharge fired (that method DOES send it) - and (b) had no obstacle/pathfinding
        // awareness at all, unlike every other mob movement in this file. Fixed by repointing Home to the
        // escort position (instead of the mob's own spawn point) and reusing TickMobReturnHome - already
        // the exact "walk to Home with real A* pathfinding around dungeon geometry via ChaseStep, then plant
        // idle" engine every other mob's disengage/return-to-post uses - instead of a bespoke duplicate. No
        // separate target field needed any more; ConvergeToEscort is now just the branch flag.
        public bool ConvergeToEscort;
    }

    private readonly object _stateLock = new();
    private readonly List<Npc> _mobs = [];
    private readonly Dictionary<ulong, MobState> _mobStates = [];
    private readonly List<Npc> _bonusInteractables = [];
    // Guids of hostiles spawned BY a bonus-prop interact (not part of Dungeon.Enemies). They join _mobs/
    // _mobStates so they get the exact same shared chase/attack AI as real enemies, but are excluded from
    // the main "defeat everyone" win gate (allClear in OnNpcKilled) - the bonus is optional, so an un-killed
    // spirit must never block finishing the dungeon.
    private readonly HashSet<ulong> _bonusSpiritGuids = [];
    // "Frog Log" spawner props (Dungeon.FrogLogPositions) + guids of the frogs THEY spawn. Frog-log spawns
    // join _mobs/_mobStates for the same shared chase/attack AI as real enemies, but are tracked here so
    // OnNpcKilled's win gate excludes them (same reasoning as _bonusSpiritGuids: an open-ended spawn while
    // the log survives must never block finishing the dungeon). The logs themselves live in their OWN list,
    // not _mobs, since they're stationary/non-aggressive and destroyed via a dedicated OnNpcKilled branch.
    private readonly List<Npc> _frogLogs = [];
    private readonly HashSet<ulong> _frogLogSpawnGuids = [];
    private readonly Dictionary<ulong, long> _frogLogNextSpawnTicks = [];
    // Spawns produced so far per log, enforcing Dungeon.FrogLogMaxSpawns (0 = unlimited, unchanged for
    // every dungeon that doesn't set it).
    private readonly Dictionary<ulong, int> _frogLogSpawnCounts = [];
    // Players who clicked the exit door on a win and are waiting to actually leave — see ReturnHome/
    // NotifyRewardWheelStopped/TryCompleteWinReturn. Value = the overworld position to return them to.
    private readonly Dictionary<ulong, Vector4> _pendingWinReturn = [];
    private int _killed;
    private int _bonusKilled;
    private int _bonusInteracted;
    private bool _won;
    private int _encounterRun;
    private float _groundY;

    // Escort NPC (Dungeon.EscortModelId/EscortStages, e.g. Bixie Hive's captive Bixie Queen) - a friendly,
    // stationary companion who speaks staged dialogue as the player clears content. _escortStageIndex is
    // the NEXT stage to fire (0-based into Dungeon.EscortStages); see AdvanceEscortStage. _escortGreeted
    // gates the ambient greeting line (Dungeon.EscortGreetingLineId) to fire once, on PROXIMITY - live
    // feedback 2026-07-28: it was firing immediately at encounter start regardless of the player's actual
    // distance from her, when it should hold until they walk up and "initiate a bit of a cutscene."
    private Npc? _escort;
    private int _escortStageIndex;
    private bool _escortGreeted;
    // Guards the Mystery Chest (Dungeon.MysteryChestModelId) against being opened twice by a race of two
    // near-simultaneous clicks.
    private bool _mysteryChestOpened;
    // Guid of the current MainBoss (0 = none/not set yet) - lets OnNpcKilled send the real boss-plate
    // Enable=false (CombatPacketEnableBossDisplay) ONLY for the actual on-screen boss, not every Boss=true
    // mini-boss (see CreateMob's MainBoss gate).
    private ulong _mainBossGuid;

    private readonly List<Player> _activePlayers = [];
    private Player? _anchor;

    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;
    private readonly Sanctuary.Game.Quests.IQuestManager _questManager;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext> _dbContextFactory;
    private readonly Random _rng = new();

    public EncounterArenaZone(DungeonDefinition dungeon, IServiceProvider serviceProvider)
        : base(CreateDefinition(dungeon), serviceProvider)
    {
        Dungeon = dungeon;
        _groundY = dungeon.GroundY;
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _questManager = serviceProvider.GetRequiredService<Sanctuary.Game.Quests.IQuestManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext>>();

        // One-time cost (this zone is cached per-activity by ZoneManager, not rebuilt per player entry) —
        // see CombatEncounterZone.BuildMobPathfinding. No-op if the world has no .gcnk data on disk.
        BuildMobPathfinding(dungeon.World, new Vector4(dungeon.CenterX, dungeon.GroundY, dungeon.CenterZ, 1f), dungeon.Radius);
    }

    // How far from center (as a fraction of the Bed radius) we place the FAR end of the walk-through. Kept
    // conservative because the Bed sphere is a loose bounding volume — the real walkable cave is smaller,
    // so staying well inside it keeps enemies + the exit on actual floor rather than in a wall / the void.
    private const float SafeReach = 0.38f;

    // Maps with Radius above this are the big real dungeon worlds -> walk-through layout (enemies spread
    // north from the centered spawn). At or below, it's a small scattered-encounter arena -> tight ring.
    private const float WalkThroughRadius = 120f;

    private static BaseZoneDefinition CreateDefinition(DungeonDefinition d)
    {
        const int tile = 64;
        const float pad = 96f; // extra margin so entities near the edge always have a tile

        // Scale the tile grid to the ACTUAL map bounds (center +/- radius). The old fixed -2..8 grid
        // (coords -128..512) only fit the tiny arenas; the real dungeon worlds have centers up to ~670 and
        // radii up to ~600, so their entities fell outside the grid and never rendered. longitude = X,
        // latitude = Z.
        return new EncounterArenaDefinition
        {
            Id = d.ActivityId,
            Name = d.World,
            TileSize = tile,
            StartLongitude = (int)MathF.Floor((d.CenterX - d.Radius - pad) / tile),
            EndLongitude = (int)MathF.Ceiling((d.CenterX + d.Radius + pad) / tile),
            StartLatitude = (int)MathF.Floor((d.CenterZ - d.Radius - pad) / tile),
            EndLatitude = (int)MathF.Ceiling((d.CenterZ + d.Radius + pad) / tile),
            Sky = null,
            // SPAWN AT THE BED CENTER (dropped ~20u so the client settles onto the floor via ground
            // adoption). The client stores NO player-spawn point for these worlds, and the Bed sphere is
            // only the bounding volume — an edge offset (my earlier south-edge spawn) lands OUTSIDE the
            // actual cave geometry and the player falls through ("way below the map"). The center is the
            // one point guaranteed to be inside the room, so we spawn there and keep enemies within a
            // safe fraction of the radius. Per-dungeon spawns can be refined by measuring in-game.
            // SpawnOverride uses a REAL captured spawn point instead when one is known (still dropped
            // ~20u for the same settle-onto-floor behavior).
            SpawnPosition = d.SpawnOverride is { } so
                ? new Vector4(so.X, so.Y + 20f, so.Z, 1f)
                : new Vector4(d.CenterX, d.GroundY + 20f, d.CenterZ, 1f),
            SpawnRotation = Quaternion.Identity,
        };
    }

    #region Zone lifecycle

    public override void OnClientIsReady(Player player)
    {
        EnterAtFullVitals(player); // real max HP + mana so the bar matches the real-damage claw
        player.SendTunneled(new PacketZoneDoneSendingInitialData());
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());
        JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager);
    }

    public override void OnClientFinishedLoading(Player player)
    {
        ActivePlayers();

        bool first;
        lock (_stateLock)
        {
            if (!_activePlayers.Any(p => p.Guid == player.Guid))
                _activePlayers.Add(player);
            first = _activePlayers.Count == 1;
        }

        if (first)
        {
            _anchor = player;
            StartEncounter(player);
        }
        else
        {
            _logger.LogInformation("{dungeon}: {name} joined the party fight (member #{n}).",
                Dungeon.Comment, player.Name, _activePlayers.Count);
            DeliverEntrySequence(player, _encounterRun);
            PushLiveEncounterTo(player);
        }
    }

    protected override void Broadcast(ISerializablePacket packet)
    {
        foreach (var p in ActivePlayers())
            p.SendTunneled(packet);
    }

    private Player[] ActivePlayers()
    {
        lock (_stateLock)
        {
            _activePlayers.RemoveAll(p => p.Zone != this);
            if (_anchor is not null && _anchor.Zone != this)
                _anchor = _activePlayers.Count > 0 ? _activePlayers[0] : null;
            return [.. _activePlayers];
        }
    }

    private void PushLiveEncounterTo(Player player)
    {
        List<Npc> live = [];
        lock (_stateLock)
        {
            live.AddRange(_mobs);
            live.AddRange(_bonusInteractables);
            live.AddRange(_frogLogs);
            if (_escort is { } escort) live.Add(escort);
            if (ExitDoor is { } exitDoor) live.Add(exitDoor);
        }
        foreach (var npc in live)
        {
            player.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(player);
            SendNpcRelevance(player, npc);
        }
    }

    #endregion

    #region Encounter

    // A random jittered position around basePos, retried (bounded) against MobObstacleMap so props don't
    // land inside real wall/prop geometry - the same obstacle data the mob pathfinding fix uses (see
    // CombatEncounterZone.BuildMobPathfinding). Falls back to basePos itself (a real enemy spawn point,
    // already proven walkable) if every jitter attempt lands somewhere blocked.
    private Vector4 JitteredWalkablePos(Vector4 basePos, float minRadius, float maxRadius)
    {
        // Same safe boundary enemy/exit placement already uses (Dungeon.Radius is a loose bounding volume,
        // not the real walkable extent - see SafeReach's own header comment). basePos itself already comes
        // from a real spawn point presumably within safe bounds, but the jitter OFFSET was never checked
        // against this boundary at all - only against wall/prop obstacles - so an outward jitter from a
        // basePos near the edge could still land past the real cave boundary ("outside the dungeon").
        var safeReach = Dungeon.Radius * SafeReach;
        var safeReachSq = safeReach * safeReach;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var angle = (float)(_rng.NextDouble() * Math.Tau);
            var r = minRadius + (float)_rng.NextDouble() * (maxRadius - minRadius);
            var pos = new Vector4(basePos.X + MathF.Sin(angle) * r, basePos.Y, basePos.Z + MathF.Cos(angle) * r, 1f);
            var dx = pos.X - Dungeon.CenterX;
            var dz = pos.Z - Dungeon.CenterZ;
            if (dx * dx + dz * dz > safeReachSq)
                continue;
            if (MobObstacleMap is null || !MobObstacleMap.IsBlocked(pos))
                return pos;
        }
        return basePos;
    }

    private void StartEncounter(Player player)
    {
        var spawns = BuildDungeonSpawns(out var spawnsAreReal);

        lock (_stateLock)
        {
            foreach (var old in _mobs)
                old.Dispose();
            _mobs.Clear();
            _mobStates.Clear();
            _bonusSpiritGuids.Clear();
            foreach (var old in _bonusInteractables)
                old.Dispose();
            _bonusInteractables.Clear();
            foreach (var old in _frogLogs)
                old.Dispose();
            _frogLogs.Clear();
            _frogLogSpawnGuids.Clear();
            _frogLogNextSpawnTicks.Clear();
            _frogLogSpawnCounts.Clear();
            ExitDoor?.Dispose();
            SetExitDoor(null);
            _escort?.Dispose();
            _escort = null;
            _escortStageIndex = 0;
            _escortGreeted = false;
            _mysteryChestOpened = false;
            _mainBossGuid = 0;
            _killed = 0;
            _bonusKilled = 0;
            _bonusInteracted = 0;
            _won = false;
            _groundY = Dungeon.GroundY;
            _encounterRun++;

            var guids = new List<ulong>();
            var slot = 0;
            foreach (var group in Dungeon.Enemies)
            {
                for (var i = 0; i < group.Count; i++)
                {
                    var pos = spawns[slot % spawns.Count];
                    slot++;
                    var mob = CreateMob(group, pos);
                    if (mob is null) continue;
                    _mobs.Add(mob);
                    _mobStates[mob.Guid] = new MobState { SlotAngle = (float)(_rng.NextDouble() * Math.Tau), Home = pos };
                    guids.Add(mob.Guid);
                }
            }

            // Interact-based bonus (e.g. "Release the trapped spirits... 0/6"): scattered among the same
            // spawn-point cloud used for enemies (jittered so they don't sit exactly on top of a mob), since
            // no real captured positions exist for these - everything else in a scripted dungeon IS a real
            // position, this is the one exception.
            if (Dungeon.BonusInteractCount > 0)
            {
                for (var i = 0; i < Dungeon.BonusInteractCount; i++)
                {
                    var basePos = spawns[_rng.Next(spawns.Count)];
                    // TIGHTENED 2026-07-27 (live feedback: "some bones are spawning outside the map too" -
                    // same root cause as the Swamp Cray underground reports: basePos.Y is reused unchanged,
                    // only X/Z move, and a 6-12u jump is far more likely to cross real terrain/boundary the
                    // obstacle map doesn't know about than the enemy packs' own 2-3u cap). Matches that cap.
                    var pos = JitteredWalkablePos(basePos, 1.5f, 3f);
                    var spirit = CreateBonusInteractable(pos);
                    if (spirit is null) continue;
                    _bonusInteractables.Add(spirit);
                    guids.Add(spirit.Guid);
                }
            }

            // "Frog Log" spawners: real, exact sheet positions (unlike the bonus interactables above, these
            // don't need jitter - the sheet gives 3 real marker coordinates).
            foreach (var (x, y, z) in Dungeon.FrogLogPositions)
            {
                var log = CreateFrogLog(new Vector4(x, y, z, 1f));
                if (log is null) continue;
                _frogLogs.Add(log);
                guids.Add(log.Guid);
            }

            // Escort NPC (e.g. Bixie Hive's captive Bixie Queen) - stationary, friendly, not a combat
            // target, so she's deliberately NOT added to `guids` (SendCombatMinimapMarkers below stamps
            // every guid there as a HOSTILE combat badge). Her greeting voiceline is PROXIMITY-triggered
            // (see StartAi's EscortGreetRange check), not fired here - live feedback 2026-07-28: it was
            // firing immediately at spawn regardless of the player's actual distance from her, when it
            // should hold until they walk up and "initiate a bit of a cutscene."
            if (Dungeon.EscortModelId > 0 && Dungeon.EscortPosition is { } ep)
                _escort = CreateEscortNpc(new Vector4(ep.X, ep.Y, ep.Z, 1f));

            SendCombatMinimapMarkers(guids);
        }

        DeliverEntrySequence(player, _encounterRun);

        _logger.LogInformation("{dungeon}: encounter start for {name} — {n} enemies pre-spawned in {world}.",
            Dungeon.Comment, player.Name, Dungeon.TotalEnemies, Dungeon.World);

        // Ground adoption measures ONE player-settle height and applies it to EVERY idle mob - correct for
        // the procedural layout (which only ever had one guessed GroundY to begin with), but WRONG for a
        // scripted layout with real per-marker heights (a cave can have multiple real floor levels, e.g.
        // this dungeon's sheet Y values range 30.34-41.37 across its packs) - it would flatten every mob
        // onto whichever single height the player happens to settle at, sinking/floating them through the
        // real terrain at every other elevation. Skip it entirely when the spawns are real.
        if (!spawnsAreReal)
            StartGroundAdoption(player, _encounterRun);
        StartAi(player, _encounterRun);
    }

    // Enemy spawn points, ordered to match the group iteration in StartEncounter
    // (group 0's enemies first, then group 1's, ...).
    // Small arenas (Radius <= WalkThroughRadius): the original tight two-ring cluster at center —
    // an in-place arena brawl.
    // Big dungeon worlds: each enemy GROUP is a "station" spread along the path from the entrance
    // (south edge) to the far end (north), so the player walks through fighting cluster after cluster,
    // with the last group (usually the boss) waiting at the far end. Enemies only aggro within
    // AggroRange, so distant clusters stay put until you reach them.
    private List<Vector4> BuildDungeonSpawns(out bool spawnsAreReal)
    {
        // A zone script (Scripts/Zone/<world>.lua) can supply fixed spawn points via a getSpawnPoints(zone)
        // function that calls zone.addSpawnPoint(x, y, z) once per enemy, in the same group order as
        // Dungeon.Enemies. Only used if it reports EXACTLY the expected count — a mismatch (script written
        // for a different enemy composition than what's currently in DungeonDefinition) falls back to the
        // procedural layout below rather than silently spawning too few/many enemies.
        var scripted = CollectScriptSpawnPoints("getSpawnPoints");
        if (scripted.Count == Dungeon.TotalEnemies)
        {
            spawnsAreReal = true;
            return scripted;
        }

        spawnsAreReal = false;
        if (scripted.Count > 0)
        {
            _logger.LogWarning(
                "{dungeon}: script reported {n} spawn points, expected {expected} — falling back to procedural placement.",
                Dungeon.Comment, scripted.Count, Dungeon.TotalEnemies);
        }

        var pts = new List<Vector4>(Math.Max(Dungeon.TotalEnemies, 1));
        var cx = Dungeon.CenterX;
        var cz = Dungeon.CenterZ;
        var gy = Dungeon.GroundY;

        // Small arena: concentric rings around center (unchanged behavior for the scattered encounters).
        if (Dungeon.Radius <= WalkThroughRadius)
        {
            var count = Math.Max(Dungeon.TotalEnemies, 1);
            for (var i = 0; i < count; i++)
            {
                var ring = i % 2;
                var radius = 22f + ring * 12f;
                var angle = (float)(i * Math.Tau / count) + ring * 0.4f;
                pts.Add(new Vector4(cx + MathF.Sin(angle) * radius, gy, cz + MathF.Cos(angle) * radius, 1f));
            }
            return pts;
        }

        // Walk-through: spawn is at CENTER, so lay the stations out NORTH of it within SafeReach, closest
        // group just ahead of the spawn and the last group (usually the boss) at the far end. You fight
        // forward from the middle of the room toward the boss; distant clusters stay dormant until you
        // reach them (AggroRange). Everything stays inside the safe radius so it lands on real floor.
        var groups = Dungeon.Enemies;
        var ng = groups.Length;
        var reach = Dungeon.Radius * SafeReach;
        for (var g = 0; g < ng; g++)
        {
            var group = groups[g];
            var t = ng == 1 ? 0.5f : g / (float)(ng - 1);        // 0 = nearest .. 1 = far end
            var stationZ = cz + (0.15f + 0.85f * t) * reach;     // north of center, within reach
            // zig-zag the mid stations left/right so it isn't a straight line; boss station centered.
            var lateral = t >= 0.99f ? 0f : ((g % 2 == 0) ? -1f : 1f) * (reach * 0.35f);
            var stationX = cx + lateral;
            var c = Math.Max(group.Count, 1);
            var clusterR = 4f + (c > 6 ? 4f : 0f);
            for (var i = 0; i < c; i++)
            {
                var a = (float)(i * Math.Tau / c);
                pts.Add(new Vector4(stationX + MathF.Sin(a) * clusterR, gy, stationZ + MathF.Cos(a) * clusterR, 1f));
            }
        }
        return pts;
    }

    private void DeliverEntrySequence(Player player, int run)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                if (player.Zone != this || run != _encounterRun)
                    return;

                // Real Goals-panel text for the primary objective (e.g. Bixie Hive's "Save the Queen from
                // unruly Bixies!") - a DIFFERENT real string from the details-popup DescriptionId used just
                // below (Bixie Hive's "Drone Fauzz is trying to take over the hive!..."). 0 = most dungeons,
                // whose "defeat everyone" goal happens to read the same as the overall description.
                var goalTextId = Dungeon.PrimaryGoalNameId != 0 ? Dungeon.PrimaryGoalNameId : Dungeon.DescriptionId;

                EncounterDetailsResponsePacket MakeLaunch() => new()
                {
                    Unknown = EncounterId,
                    Unknown2 = EncounterInstanceId,
                    NameId = Dungeon.TitleNameId,
                    DescriptionId = Dungeon.DescriptionId,
                    Difficulty = Dungeon.Difficulty,
                    IconId = Dungeon.IconId,
                    MiniGameType = CombatMiniGameType,
                    // The win screen's "Members Only Bonus" (Coins) box is gated on THIS top-level flag,
                    // separate from whatever the member bundle itself carries - it stayed false everywhere
                    // in this flow, which is why the coins box never rendered even once MemberCoins had a
                    // real value on the wire.
                    MembersOnly = true,
                    Launch = true,
                    // Real retail goal for a dungeon with a bonus (kill-based, e.g. Bandit Hideout's "Defeat
                    // all of the Big Bandits! 0/5", OR interact-based, e.g. Cracked Claw Caverns' "Release
                    // the trapped spirits... 0/6") is a SECOND objective row alongside the main "defeat
                    // everyone" one.
                    // CORRECTED 2026-07-26 (2nd pass - decompiled ObjectiveData::sub_8FD770 + the ctor it
                    // feeds, FUN_00c42870/FUN_00c42280, to get the exact wire->struct mapping instead of
                    // trusting the old "0 inline" header comment, which turned out to describe the wrong
                    // thing): the wire "Status" field is NOT the literal internal status - it's a REQUEST
                    // code (0-4) fed into a transition switch (FUN_00c42280) that maps it to the real
                    // internal enum. Request 1 maps to internal status 2 (InProgress, the one that shows a
                    // live "Count/Total") given our Unknown4=false/Unknown8=0. Wire Count/Total ARE copied
                    // in literally (no transform) into the exact same struct fields FUN_00c42440 renders
                    // from, and the ctor renders immediately and unconditionally after setting them - so
                    // the count is already showing correctly right out of this inline definition, no
                    // Activate call even required for it to render (Activate below still matters for the
                    // "New Objective" announce / to match ground truth timing). Setting Status/Total to 0
                    // here in the previous pass was wrong - it produced request-code 0 -> internal status 1
                    // (NotStarted, no digits) which is exactly the blank-row regression that pass caused.
                    Objectives = Dungeon.HasBonus
                        ?
                        [
                            new EncounterObjective
                            {
                                ObjectiveId = EncounterId, NameId = goalTextId,
                                DescriptionId = goalTextId,
                                Status = 1, Count = 0, Total = 1, Xp = Dungeon.Xp,
                            },
                            new EncounterObjective
                            {
                                ObjectiveId = BonusObjectiveId, NameId = Dungeon.BonusNameId,
                                DescriptionId = Dungeon.BonusNameId,
                                Status = 1, Count = 0, Total = Dungeon.BonusTotal,
                            },
                        ]
                        :
                        [
                            new EncounterObjective
                            {
                                ObjectiveId = EncounterId, NameId = goalTextId,
                                DescriptionId = goalTextId,
                                Status = 1, Count = 0, Total = 1, Xp = Dungeon.Xp,
                            },
                        ],
                    PreviewRewards = FrostfangArenaZone.GetPrizePreviewFor(player),
                    PreviewCoins = Dungeon.Coins,
                    PreviewXp = FrostfangArenaZone.PrizeXp,
                    // The win-screen's own "Stars" + "Members Only Bonus" Coins boxes (distinct bundles
                    // from the preview above — see EncounterDetailsResponsePacket.RewardXp/MemberCoins).
                    RewardXp = Dungeon.Xp,
                    MemberCoins = Dungeon.Coins,
                    ProfileType = FrostfangArenaZone.CombatProfileType,
                    ActivityId = EncounterId,
                };

                EncounterPacketPlayerEnter MakeEnter(ulong guid) => new()
                {
                    EncounterId = EncounterId,
                    InstanceId = EncounterInstanceId,
                    PlayerGuid = guid,
                };

                UiObjectiveAddPacket GoalRow() => new()
                {
                    ObjectiveId = EncounterId,
                    NameId = goalTextId,
                };

                // The Goals-panel "N/6" count (2026-07-26, root-caused with a live frida memory patch on
                // the real client — see project_cracked_claw_caverns_dungeon.md for the full trace):
                // UiObjectiveAddPacket's Total field gates whether the row EVER shows a count at all — the
                // client's own status-text builder (FUN_00A8B9A0) only formats "Count/Total" when Total>1
                // (a plain Total=1 objective, like the primary row here, always shows a generic label with
                // no digits — this matches retail, not a bug). But Total alone isn't enough: the row's
                // Count/Total are only ever read when the client's "_OnDataChanged" redraw notify fires,
                // which the ADD packet only fires ONCE at row creation - a live memory patch proved that
                // writing the row's fields directly does nothing without also re-firing that notify.
                // UiObjectiveUpdateCountPacket (op47/sub2, undocumented until this session) is what
                // actually fires it - send it once right after the row exists to make the count paint.
                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit));
                player.SendTunneled(MakeLaunch());
                player.SendTunneled(MakeEnter(0));
                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit));
                player.SendTunneled(new ObjectiveActivatePacket { ObjectiveId = EncounterId, Total = 1 });
                if (Dungeon.HasBonus)
                    player.SendTunneled(new ObjectiveActivatePacket { ObjectiveId = BonusObjectiveId, Total = Dungeon.BonusTotal });
                player.SendTunneled(GoalRow());
                if (Dungeon.HasBonus)
                {
                    player.SendTunneled(new UiObjectiveAddPacket
                    {
                        ObjectiveId = BonusObjectiveId, NameId = Dungeon.BonusNameId, Total = Dungeon.BonusTotal,
                        IsBonus = true, CategoryPrefixId = BonusCategoryPrefixId,
                    });
                    player.SendTunneled(new UiObjectiveUpdateCountPacket { ObjectiveId = BonusObjectiveId, Count = 0 });
                }
                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit));
                player.SendTunneled(PacketEncounterDataCommon.CreateCombatRules());
                player.SendTunneled(MakeEnter(player.Guid));

                player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = true });
                player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = true });
                player.SendTunneled(new EncounterStatePacket
                {
                    EncounterId = EncounterId,
                    InstanceId = EncounterInstanceId,
                    State = 6,
                });

                _logger.LogInformation("{dungeon}: entry sequence delivered to {name} (run {run}).",
                    Dungeon.Comment, player.Name, run);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{dungeon}: entry sequence delivery failed.", Dungeon.Comment);
            }
        });
    }

    private void StartGroundAdoption(Player player, int run)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Poll for the player's Y to STOP CHANGING (settled on real floor) instead of trusting a
                // single fixed-delay sample. A big drop (GroundY is a rough estimate from the world's Bed
                // sphere center, not a measured floor height — it can be well off for a room with real
                // elevation change) can still be mid-fall at a fixed 3s mark, which would adopt a mid-air
                // height for every enemy in the room instead of the real floor.
                const float SettleEpsilon = 0.1f;
                const int PollMs = 400;
                const int MaxPolls = 20; // ~8s ceiling so a stuck/endlessly-falling player can't hang this

                var lastY = player.Position.Y;
                var measured = lastY;
                var settled = false;

                for (var i = 0; i < MaxPolls; i++)
                {
                    await Task.Delay(PollMs);
                    if (player.Zone != this || run != _encounterRun)
                        return;

                    var y = player.Position.Y;
                    if (MathF.Abs(y - lastY) < SettleEpsilon)
                    {
                        measured = y;
                        settled = true;
                        break;
                    }
                    lastY = y;
                }

                if (!settled)
                    measured = lastY; // best available sample if it never fully stopped moving

                if (MathF.Abs(measured - Dungeon.GroundY) < 0.75f)
                    return;

                Npc[] mobs;
                lock (_stateLock)
                {
                    _groundY = measured;
                    mobs = [.. _mobs];
                }

                foreach (var actor in mobs)
                {
                    bool idle;
                    lock (_stateLock)
                        idle = _mobStates.TryGetValue(actor.Guid, out var s) && !s.Charging;
                    if (!idle)
                        continue;

                    var p = actor.Position;
                    var lifted = new Vector4(p.X, measured, p.Z, p.W);
                    actor.UpdatePosition(lifted, actor.Rotation);
                    Broadcast(new PlayerUpdatePacketUpdatePosition
                    {
                        Guid = actor.Guid, Position = lifted, Rotation = actor.Rotation, State = 1, Unknown = 0,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{dungeon}: ground adoption failed.", Dungeon.Comment);
            }
        });
    }

    private Npc? CreateMob(DungeonEnemy group, Vector4 pos)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        npc.ModelId = group.ModelId;
        // group.NameId (added 2026-07-28, Bixie Hive) overrides the old Boss-fallback convention - a boss
        // used to always be stamped with the DUNGEON's own title, which only happened to be right for a
        // dungeon literally named after its boss (Cracked Claw Caverns' "Cracked Claw"). Bixie Hive's boss
        // (Drone Fauzz) needs his OWN name, not "Bixie Hive".
        npc.NameId = group.NameId != 0 ? group.NameId : (group.Boss ? Dungeon.TitleNameId : 0);
        npc.Name = null;
        npc.HideNamePlate = false;
        // CORRECTED (live retail feedback): a persistent bar on every regular mob was a deliberate earlier
        // choice to fix "bars sometimes pop up, sometimes not" — but that earlier flash-only behavior was
        // actually the real retail behavior, not a bug. Only the boss keeps a persistent bar; regular mobs
        // now rely purely on the on-hit flash (SendNpcHealth still fires from damage handling regardless).
        npc.ShowHealthBar = group.Boss;
        npc.Scale = group.Scale;
        npc.Disposition = 0;              // hostile
        npc.ActiveProfile = MobActiveProfile;
        npc.CompositeEffectId = 0;
        npc.MaxHealth = group.Health;
        npc.Health = group.Health;
        // A combat target, NOT an NPC: no "Press X to talk" interact prompt (they have no InteractAction, so
        // the prompt did nothing anyway). Same recipe as the overworld training dummy / world hostiles —
        // IsInteractable=false + the crossed-swords cursor keeps them attackable without the talk affordance.
        npc.IsInteractable = false;
        npc.InteractRange = 100;
        npc.Visible = true;
        npc.CursorId = 11;                // attack cursor
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = MovementTypePhysics;
        npc.Speed = 0f;
        npc.RiderGuid = ulong.MaxValue;

        npc.UpdatePosition(pos, Quaternion.Identity);

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketUpdateMana { Guid = npc.Guid });
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = npc.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });
            SendNpcRelevance(p, npc);
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = 0 });
            if (group.Boss)
                SendNpcHealth(p, npc);
        }

        // Real BOSS PLATE (op32/sub9, RE'd for Frostfang's Alpha but never actually wired into any zone
        // until now - live feedback 2026-07-28, Bixie Hive's Drone Fauzz: "his health bar should display on
        // the screen for all players"). Enable=true sets the client's boss actor flag (AddBoss) - the red
        // boss name + prominent on-screen boss health display, distinct from the regular floating nameplate
        // bar ShowHealthBar already gives. Broadcast (not per-player) so every party member gets it.
        // GATED ON MainBoss, NOT Boss (live feedback: "Unruly elite dont get boss health, just the main
        // boss... should just show health bar on top of his head") - a mini-boss keeps Boss=true's
        // nameplate bar + coin drop, but only the dungeon's real final boss gets this prominent display.
        if (group.MainBoss)
        {
            lock (_stateLock)
                _mainBossGuid = npc.Guid;
            Broadcast(new CombatPacketEnableBossDisplay { Guid = npc.Guid, Enable = true });
        }

        return npc;
    }

    // A single "Lost Explorer Bones" prop (Dungeon.BonusInteractModelId/NameId/Count). A clickable prop
    // (same recipe as StartingZone's quest collectibles: IsInteractable=true, hand cursor, InteractAction),
    // NOT a combat target — the 2026-07-26 attack-destroy rewrite was reverted, the user confirmed live
    // gameplay has this as a click interact. Consumed on click via OnBonusInteract below.
    private Npc? CreateBonusInteractable(Vector4 pos)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        npc.ModelId = Dungeon.BonusInteractModelId;
        npc.NameId = Dungeon.BonusInteractNameId;
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = false;
        npc.Scale = 1f;
        npc.CompositeEffectId = 0;
        npc.MaxHealth = 0;
        npc.IsInteractable = true;
        // 100 (the value every other prop/mob in this zone class uses, copy-pasted without adjustment) is
        // WAY too generous for something genuinely clickable: the client's "free interact" system
        // (CommandPacketFreeInteractionNpcHandler) auto-triggers the NEAREST interactable NPC in range
        // while the player lingers nearby, with no actual click - live-confirmed 2026-07-26 (bones were
        // triggering just from walking near them, well before the player ever clicked). Real interactable
        // props elsewhere in this codebase (StartingZone.cs quest collectibles, kiosks) use 6-18; matching
        // that scale instead.
        npc.InteractRange = 6;
        npc.Visible = true;
        npc.CursorId = 17;                // hand cursor, matches other clickable props
        npc.Static = true;                // it's a bones pile — nothing should try to move it
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = MovementTypePhysics;
        npc.RiderGuid = ulong.MaxValue;

        npc.UpdatePosition(pos, Quaternion.Identity);
        npc.InteractAction = _ => OnBonusInteract(npc);

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = npc.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });
            SendNpcRelevance(p, npc);
        }

        return npc;
    }

    // Bones were clicked — remove them and spawn the hostile spirit on the spot. Doesn't touch the kill
    // counter or either objective by itself; the spirit's OWN death (via the normal _mobs/_bonusSpiritGuids
    // path in OnNpcKilled) is what actually ticks the bonus.
    private void OnBonusInteract(Npc npc)
    {
        bool wasBones;
        lock (_stateLock)
            wasBones = _bonusInteractables.Remove(npc);
        if (!wasBones)
            return;

        Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } });
        var bonesPos = npc.Position;
        npc.GracefulRemoval = (false, 0, 0, DeathPoofFxId, 1000);
        npc.Dispose();

        // No BonusSpawnModelId configured (e.g. Bixie Hive's "rescue" bonus - the wiki has the player FREE
        // the Frightened Bixie Workers, not fight anything) - tick the bonus directly instead of spawning a
        // hostile to kill. Cracked Claw's "bones disturb a spirit" flow (the only other user of this
        // mechanic so far) keeps its existing spawn-then-kill behavior unchanged since it still sets
        // BonusSpawnModelId, so this is purely additive.
        if (Dungeon.BonusSpawnModelId <= 0)
        {
            int count;
            lock (_stateLock)
                count = ++_bonusInteracted;

            if (count >= Dungeon.BonusTotal)
            {
                Broadcast(new ObjectiveCompletePacket { ObjectiveId = BonusObjectiveId });
                Broadcast(new UiObjectiveCompletePacket { ObjectiveId = BonusObjectiveId });
                GrantBonusGoalReward();
            }
            else
            {
                Broadcast(new UiObjectiveUpdateCountPacket { ObjectiveId = BonusObjectiveId, Count = count });
            }

            _logger.LogInformation("{dungeon}: a captive was rescued ({count}/{total}).",
                Dungeon.Comment, count, Dungeon.BonusTotal);
            return;
        }

        var spirit = SpawnBonusSpirit(bonesPos);
        AnnounceBonesText(spirit?.Guid);
        _logger.LogInformation("{dungeon}: bones disturbed — a spirit materializes.", Dungeon.Comment);
    }

    // ObjectiveAddPacket (op45/sub5) was tried first — live-confirmed to display the real text, but the
    // client hardcodes it as "New Objective: \"%s\"" in its OWN chat window specifically (not a banner),
    // exactly matching what the user reported. Switched to ChatPacketFromStringId (op4) instead - the SAME,
    // already-proven mechanism `Npc.SayStringId` uses for real NPC overhead speech bubbles (IsChatLogged=
    // false -> no chat-log line, per that method's own verified header comment). SpeakerGuid=0 ("no
    // speaker") produced nothing live-tested - matches the earlier !hudtext guid=0 negative result from
    // this same investigation - so this now anchors to the just-spawned spirit (a real, currently-visible
    // entity) instead of a null/absent speaker. HasColor+ColorId=1 (red) matches the user's own description
    // of the retail text.
    private void AnnounceBonesText(ulong? speakerGuid)
    {
        // Live-tested 2026-07-26: with a nameless (NameId=0) speaker, the client still renders a bare ":"
        // where the "SpeakerName: " prefix would go - the colon is part of the fixed template, not
        // conditional on having a name. Trying IsEmote=true instead - narrated third-person text like this
        // ("The bones crumble...") is semantically an emote/action, not a spoken line, and emote formatting
        // in most chat systems doesn't use the "Name: " quote-style prefix at all.
        var msg = new ChatPacketFromStringId
        {
            SpeakerGuid = speakerGuid ?? 0,
            StringId = 139366,
            IsEmote = true,
            IsChatLogged = false,
            HasColor = true,
            ColorId = 1,
        };
        Broadcast(msg);
    }

    // Escort dialogue (e.g. Bixie Queen's voicelines) rendered the SAME prominent colored on-screen
    // announcement AnnounceBonesText uses - live feedback 2026-07-28: "it should appear on screen like how
    // we do when we interact with the bones... but with 'Bixie Queen:' in the beginning of each dialog...
    // and the color should be green." UNLIKE the bones' anonymous narration, this uses the escort's REAL
    // SpeakerGuid (so the client's own "Name: text" prefix resolves from her NameId) with IsEmote=false
    // (AnnounceBonesText's own header comment: IsEmote is what strips that prefix - we want it here).
    // ColorId=3 is Green per ChatPacketFromStringId's own documented palette (0=white/1=red/2=yellow/
    // 3=green/4=blue).
    private void AnnounceEscortText(Npc escort, int stringId)
    {
        Broadcast(new ChatPacketFromStringId
        {
            SpeakerGuid = escort.Guid,
            StringId = stringId,
            IsEmote = false,
            IsChatLogged = false,
            HasColor = true,
            ColorId = 3,
        });
    }

    // The bones were destroyed (via OnNpcKilled) — spawn a hostile spirit on the spot. Added to _mobs/
    // _mobStates so it gets the exact same shared chase/attack AI as every other mob, tracked in
    // _bonusSpiritGuids so OnNpcKilled routes ITS death to the bonus objective instead of the main one.
    private Npc? SpawnBonusSpirit(Vector4 spawnPos)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        npc.ModelId = Dungeon.BonusSpawnModelId;
        npc.NameId = Dungeon.BonusSpawnNameId;
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = false; // regular hostile, not a boss — see CreateMob's note
        npc.Scale = 1f;
        npc.Disposition = 0;          // hostile
        npc.ActiveProfile = MobActiveProfile;
        npc.CompositeEffectId = SpawnPoofFxId;
        npc.MaxHealth = Dungeon.BonusSpawnHealth;
        npc.Health = Dungeon.BonusSpawnHealth;
        npc.IsInteractable = false;
        npc.InteractRange = 100;
        npc.Visible = true;
        npc.CursorId = 11;
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = MovementTypePhysics;
        npc.Speed = 0f;
        npc.RiderGuid = ulong.MaxValue;
        npc.UpdatePosition(spawnPos, Quaternion.Identity);

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketUpdateMana { Guid = npc.Guid });
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = npc.Guid,
                Status = (CharacterStatus)CharState_Baseline, // spawn idle, BeginCharge below provokes it
            });
            SendNpcRelevance(p, npc);
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = 0 });
        }

        MobState state;
        lock (_stateLock)
        {
            _mobs.Add(npc);
            state = new MobState { SlotAngle = 0f, Home = spawnPos };
            _mobStates[npc.Guid] = state;
            _bonusSpiritGuids.Add(npc.Guid);
        }
        // Spawns already provoked (per the sheet) - go through the SAME charge-start every other mob uses
        // (this sends the ExpectedSpeed packets that drive its movement; setting state.Charging directly
        // without them left it walking wrong - it had a target but no client-known speed).
        BeginCharge(npc, state);
        SendCombatMinimapMarkers([npc.Guid]);
        return npc;
    }

    // A "Frog Log" spawner prop (Dungeon.FrogLogPositions): a stationary, attackable target with 1 HP —
    // any hit destroys it — that periodically spawns a hostile while a player lingers nearby (see the AI
    // tick loop's Frog Log proximity check). Lives in _frogLogs, NOT _mobs: it never moves/attacks, and its
    // destruction goes through a dedicated OnNpcKilled branch rather than the shared kill-counter path.
    private Npc? CreateFrogLog(Vector4 pos)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        npc.ModelId = Dungeon.FrogLogModelId;
        npc.NameId = Dungeon.FrogLogNameId;
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = false; // 1 HP, dies to any hit — a bar would be meaningless
        npc.Scale = 1f;
        npc.Disposition = 0;
        npc.ActiveProfile = MobActiveProfile;
        npc.CompositeEffectId = 0;
        npc.MaxHealth = Dungeon.FrogLogHealth;
        npc.Health = Dungeon.FrogLogHealth;
        // A combat target like a regular mob (attackable, no talk prompt), but Static — it never moves.
        npc.IsInteractable = false;
        npc.InteractRange = 100;
        npc.Visible = true;
        npc.CursorId = 11;
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = MovementTypePhysics;
        npc.Speed = 0f;
        npc.RiderGuid = ulong.MaxValue;
        npc.Static = true;

        npc.UpdatePosition(pos, Quaternion.Identity);

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketUpdateMana { Guid = npc.Guid });
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = npc.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });
            SendNpcRelevance(p, npc);
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = 0 });
        }

        return npc;
    }

    // A Frog Log triggered (see the AI tick loop) — spawn its hostile a few units off the log itself so it
    // doesn't stack on the prop. Joins _mobs/_mobStates for the shared chase/attack AI (same recipe as
    // SpawnBonusSpirit), tracked in _frogLogSpawnGuids so the "defeat everyone" win gate excludes it — an
    // open-ended spawn while its log survives must never block finishing the dungeon.
    private void SpawnFrogFromLog(Vector4 logPos)
    {
        if (!TryCreateNpc(out var npc))
            return;

        var angle = (float)(_rng.NextDouble() * Math.Tau);
        var spawnPos = new Vector4(logPos.X + MathF.Sin(angle) * 3f, logPos.Y, logPos.Z + MathF.Cos(angle) * 3f, 1f);

        npc.ModelId = Dungeon.FrogLogSpawnModelId;
        npc.NameId = 0;
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = false; // regular hostile, not a boss — see CreateMob's note
        npc.Scale = 1f;
        npc.Disposition = 0;
        npc.ActiveProfile = MobActiveProfile;
        npc.CompositeEffectId = SpawnPoofFxId;
        npc.MaxHealth = Dungeon.FrogLogSpawnHealth;
        npc.Health = Dungeon.FrogLogSpawnHealth;
        npc.IsInteractable = false;
        npc.InteractRange = 100;
        npc.Visible = true;
        npc.CursorId = 11;
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = MovementTypePhysics;
        npc.Speed = 0f;
        npc.RiderGuid = ulong.MaxValue;
        npc.UpdatePosition(spawnPos, Quaternion.Identity);

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketUpdateMana { Guid = npc.Guid });
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = npc.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });
            SendNpcRelevance(p, npc);
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = 0 });
        }

        MobState state;
        lock (_stateLock)
        {
            _mobs.Add(npc);
            state = new MobState { SlotAngle = (float)(_rng.NextDouble() * Math.Tau), Home = spawnPos };
            _mobStates[npc.Guid] = state;
            _frogLogSpawnGuids.Add(npc.Guid);
        }
        BeginCharge(npc, state); // already provoked - see SpawnBonusSpirit's identical comment
        SendCombatMinimapMarkers([npc.Guid]);
    }

    // The escort NPC (Dungeon.EscortModelId, e.g. Bixie Hive's captive Bixie Queen) - friendly, stationary
    // (no follow/pathing AI exists yet - she stays put at her real sheet position while the fight moves on
    // without her), not a combat target and not clickable (the sheet only ever has her speaking, never
    // interacted with). Same recipe as StartingZone's stationary friendly NPCs (Disposition=1 neutral).
    private Npc? CreateEscortNpc(Vector4 pos)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        npc.ModelId = Dungeon.EscortModelId;
        npc.NameId = Dungeon.EscortNameId;
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = false;
        npc.Scale = 1f;
        npc.Disposition = 1;              // neutral/friendly, not a combat target
        npc.CompositeEffectId = 0;
        npc.MaxHealth = 0;
        npc.IsInteractable = false;
        npc.InteractRange = 100;
        npc.Visible = true;
        npc.CursorId = 0;
        npc.Static = true;
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = MovementTypePhysics;
        npc.Speed = 0f;
        npc.RiderGuid = ulong.MaxValue;

        npc.UpdatePosition(pos, Quaternion.Identity);

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = npc.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });
            SendNpcRelevance(p, npc);
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = 1 });
        }

        return npc;
    }

    // The static roster (or the prior stage's wave) just cleared and an escort stage is still pending -
    // speak this stage's voiceline, optionally run a delayed "gift" beat (power-up pickups + a second
    // voiceline) and/or hold the actual enemy spawn for DelayMs, instead of ending the encounter. Called
    // from OnNpcKilled OUTSIDE its lock (same call site WinEncounter uses).
    private void AdvanceEscortStage()
    {
        var stage = Dungeon.EscortStages[_escortStageIndex++];
        var run = _encounterRun;
        if (_escort is { } escort)
            AnnounceEscortText(escort, stage.VoicelineNameId);

        if (stage.GiftVoicelineNameId != 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(stage.GiftDelayMs);
                    if (run != _encounterRun)
                        return;

                    if (_escort is { } giftEscort)
                        AnnounceEscortText(giftEscort, stage.GiftVoicelineNameId);

                    var (gx, gy, gz) = stage.GiftPosition;
                    var (px, pz) = stage.GiftPerpendicular;
                    for (var i = 0; i < stage.GiftPowerupKinds.Length; i++)
                    {
                        // Side by side ALONG GiftPerpendicular, centered on GiftPosition (live feedback:
                        // "in front of her like this and side to side" - a row relative to her own facing,
                        // not the world's raw X axis).
                        var offset = (i - (stage.GiftPowerupKinds.Length - 1) / 2f) * 1.5f;
                        SpawnPowerupPickup(new Vector4(gx + px * offset, gy, gz + pz * offset, 1f), stage.GiftPowerupKinds[i]);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{dungeon}: escort gift beat failed.", Dungeon.Comment);
                }
            });
        }

        if (stage.DelayMs > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(stage.DelayMs);
                    if (run != _encounterRun)
                        return;

                    if (stage.WaveVoicelineNameId != 0 && _escort is { } waveEscort)
                        AnnounceEscortText(waveEscort, stage.WaveVoicelineNameId);

                    SpawnEscortStageEnemies(stage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{dungeon}: escort delayed wave failed.", Dungeon.Comment);
                }
            });
        }
        else
        {
            SpawnEscortStageEnemies(stage);
        }
    }

    // The actual enemy spawn for an escort stage (scattered around SpawnPosition, same small-radius
    // philosophy the pack scatter/bonus-prop jitter already use). Split out of AdvanceEscortStage so a
    // stage can delay this past its own voiceline (see DelayMs). InstantAggro=false spawns them idle,
    // relying on the normal AI tick's AggroRange proximity check instead of an immediate ambush charge.
    private void SpawnEscortStageEnemies(DungeonEscortStage stage)
    {
        var (sx, sy, sz) = stage.SpawnPosition;
        var guids = new List<ulong>();
        foreach (var group in stage.Enemies)
        {
            for (var i = 0; i < group.Count; i++)
            {
                var angle = (float)(_rng.NextDouble() * Math.Tau);
                var r = 2f + (float)_rng.NextDouble() * 2f;
                var pos = new Vector4(sx + MathF.Sin(angle) * r, sy, sz + MathF.Cos(angle) * r, 1f);
                var mob = CreateMob(group, pos);
                if (mob is null) continue;

                MobState state;
                lock (_stateLock)
                {
                    _mobs.Add(mob);
                    // Wander = !InstantAggro - a wave that spawns idle instead of ambushing (live feedback:
                    // "should start chasing the player... when player gets close to them") also reads as
                    // more alive if it ambles around while waiting, instead of standing frozen.
                    state = new MobState { SlotAngle = (float)(_rng.NextDouble() * Math.Tau), Home = pos, Wander = !stage.InstantAggro };
                    // ConvergeOnEscort overrides plain Wander with a real-pathfinding run at the escort -
                    // see MobState.ConvergeToEscort's header comment. Repointing Home to the escort position
                    // (instead of leaving it at this mob's own spawn point) is what makes TickMobReturnHome
                    // walk it THERE instead of back to SpawnPosition; ExpectedSpeed is sent once up front
                    // (mirrors BeginCharge) so the client interpolates a real walk from the very first tick
                    // instead of snapping between position updates until real combat kicks in. Silently
                    // no-ops if the dungeon has no escort position set (ConvergeOnEscort should never be
                    // true without one, but this stays safe).
                    if (stage.ConvergeOnEscort && Dungeon.EscortPosition is { } ep)
                    {
                        state.Home = new Vector4(ep.X, ep.Y, ep.Z, pos.W);
                        state.ConvergeToEscort = true;
                        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = mob.Guid, ExpectedSpeed = MobChaseSpeed });
                    }
                    _mobStates[mob.Guid] = state;
                }
                if (stage.InstantAggro) // already provoked - same as SpawnBonusSpirit/SpawnFrogFromLog
                    BeginCharge(mob, state);
                guids.Add(mob.Guid);
            }
        }

        SendCombatMinimapMarkers(guids);
        _logger.LogInformation("{dungeon}: escort stage {stage}/{total} - {n} enemies inbound.",
            Dungeon.Comment, _escortStageIndex, Dungeon.EscortStages.Length, guids.Count);
    }

    private void SendCombatMinimapMarkers(IReadOnlyList<ulong> guids)
    {
        if (guids.Count == 0)
            return;
        var badge = new PlayerUpdatePacketAddNotifications();
        foreach (var guid in guids)
            badge.Notifications.Add(new NotificationInfo { Guid = guid, Combat = true, Type = 3, Unknown10 = true });
        Broadcast(badge);
    }

    #endregion

    #region AI

    private void StartAi(Player player, int run)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var elapsed = 0; elapsed < 15 * 60 * 1000; elapsed += TickMs)
                {
                    await Task.Delay(TickMs);
                    if (run != _encounterRun)
                        return;

                    // Target the whole GROUP, not a fixed anchor: each mob picks its nearest live player every
                    // tick (see NearestLivePlayer), so the pack spreads across the party and re-targets when a
                    // player falls. (Loop lifetime is the encounter run + any players remaining, not one anchor
                    // — an anchor leaving used to kill AI for everyone.)
                    var players = ActivePlayers();
                    if (players.Length == 0)
                        return;

                    var now = Environment.TickCount64;
                    var dt = TickMs / 1000f;

                    // "Frog Log" spawners: proximity-triggered, per-log cooldown, until destroyed. Runs
                    // even when the regular mob pack is empty (below), so a surviving log keeps spawning
                    // after the rest of the dungeon is cleared, matching the sheet ("takes one shot to
                    // destroy... so frogs don't keep spawning" implies otherwise they DO keep spawning).
                    if (Dungeon.FrogLogSpawnModelId != 0)
                    {
                        Npc[] logs;
                        lock (_stateLock)
                            logs = [.. _frogLogs];

                        foreach (var log in logs)
                        {
                            if (!log.IsAlive)
                                continue;
                            var nearest = NearestLivePlayer(new Vector3(log.Position.X, log.Position.Y, log.Position.Z), players);
                            if (nearest is null)
                                continue;
                            var dx = nearest.Position.X - log.Position.X;
                            var dz = nearest.Position.Z - log.Position.Z;
                            if (dx * dx + dz * dz > Dungeon.FrogLogTriggerRange * Dungeon.FrogLogTriggerRange)
                                continue;

                            lock (_stateLock)
                            {
                                if (_frogLogNextSpawnTicks.TryGetValue(log.Guid, out var next) && now < next)
                                    continue;
                                // FrogLogMaxSpawns=0 (default) = unlimited, Cracked Claw's original
                                // behavior unchanged. > 0 caps the total the log will EVER produce (live
                                // feedback: "defeated enemies will respawn.. this shouldn't happen" - an
                                // unbounded spawner never lets the room actually feel cleared).
                                var spawned = _frogLogSpawnCounts.GetValueOrDefault(log.Guid);
                                if (Dungeon.FrogLogMaxSpawns > 0 && spawned >= Dungeon.FrogLogMaxSpawns)
                                    continue;
                                _frogLogSpawnCounts[log.Guid] = spawned + 1;
                                _frogLogNextSpawnTicks[log.Guid] = now + Dungeon.FrogLogSpawnCooldownMs;
                            }

                            SpawnFrogFromLog(log.Position);
                        }
                    }

                    // Escort ambient greeting (Dungeon.EscortGreetingLineId, e.g. Bixie Queen's "Get your
                    // hands off of us you brutes!") - PROXIMITY-triggered, not fired at encounter start (see
                    // StartEncounter's comment) - live feedback 2026-07-28: "this shouldn't happen until the
                    // player gets close to the queen. That will initiate a bit of a cutscene."
                    if (_escort is { } escort && !_escortGreeted && Dungeon.EscortGreetingLineId > 0)
                    {
                        var ePos = new Vector3(escort.Position.X, escort.Position.Y, escort.Position.Z);
                        var nearest = NearestLivePlayer(ePos, players);
                        if (nearest is not null)
                        {
                            var dx = nearest.Position.X - ePos.X;
                            var dz = nearest.Position.Z - ePos.Z;
                            if (dx * dx + dz * dz <= EscortGreetRange * EscortGreetRange)
                            {
                                _escortGreeted = true;
                                AnnounceEscortText(escort, Dungeon.EscortGreetingLineId);
                            }
                        }
                    }

                    Npc[] pack;
                    lock (_stateLock)
                        pack = [.. _mobs];
                    if (pack.Length == 0)
                        continue;

                    foreach (var mob in pack)
                    {
                        if (!mob.IsAlive)
                            continue;

                        MobState? state;
                        lock (_stateLock)
                            _mobStates.TryGetValue(mob.Guid, out state);
                        if (state is null)
                            continue;

                        var here = new Vector3(mob.Position.X, mob.Position.Y, mob.Position.Z);

                        // Whole party down: disengage to the spawn post + idle (shared). Otherwise chase the
                        // nearest player still standing (sticky - see NearestLivePlayerSticky).
                        var tgt = NearestLivePlayerSticky(here, players, state);
                        if (tgt is null)
                        {
                            TickMobReturnHome(mob, state, dt, now);
                            continue;
                        }

                        var target = new Vector3(tgt.Position.X, tgt.Position.Y, tgt.Position.Z);

                        // Aggro on approach, then run the shared chase/plant/attack tick.
                        if (!state.Charging)
                        {
                            var dx = target.X - here.X;
                            var dz = target.Z - here.Z;
                            if (dx * dx + dz * dz > AggroRange * AggroRange)
                            {
                                if (state.ConvergeToEscort)
                                    TickMobReturnHome(mob, state, dt, now);
                                else if (state.Wander)
                                    TickMobWander(mob, state, here, now, dt);
                                continue;
                            }
                            BeginCharge(mob, state);
                        }

                        TickMobCombat(mob, state, tgt, target, now, dt);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{dungeon}: AI loop failed (run {run}).", Dungeon.Comment, run);
            }
        });
    }

    private void BeginCharge(Npc mob, MobState state)
    {
        state.Charging = true;
        state.NextAttackTicks = Environment.TickCount64 + 1000 + _rng.Next(1500);
        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = mob.Guid, ExpectedSpeed = 3f });
        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = mob.Guid, ExpectedSpeed = MobChaseSpeed });
        Broadcast(new PlayerUpdatePacketUpdateCharacterState
        {
            Guid = mob.Guid,
            Status = (CharacterStatus)CharState_Charging,
        });
    }

    // Wander tick (MobState.Wander) - walk to a random waypoint near Home at a slow amble speed, pause a
    // beat, pick another. No aggro/attack logic here; the caller only reaches this branch while the mob is
    // NOT yet in AggroRange of anyone. Modeled on FrostfangArenaZone.TickRoamer, scoped tighter (3-7u vs
    // Frostfang's 5-14u) since this is a small pack milling around one spot, not a lone roamer covering a
    // whole arena.
    private const float WanderSpeed = 2.5f;
    private void TickMobWander(Npc mob, MobState state, Vector3 here, long now, float dt)
    {
        if (state.WanderTarget is null)
        {
            if (now < state.WanderPauseUntil)
                return;

            var angle = (float)(_rng.NextDouble() * Math.Tau);
            var dist = 3f + (float)_rng.NextDouble() * 4f;
            state.WanderTarget = new Vector2(
                state.Home.X + MathF.Sin(angle) * dist,
                state.Home.Z + MathF.Cos(angle) * dist);
        }

        var wt = state.WanderTarget.Value;
        var to = new Vector2(wt.X - here.X, wt.Y - here.Z);
        var d = to.Length();

        if (d < 0.5f)
        {
            // Arrived — stand for a couple seconds (send one stopped update so the client halts locomotion).
            state.WanderTarget = null;
            state.WanderPauseUntil = now + 1500 + _rng.Next(2500);
            Broadcast(new PlayerUpdatePacketUpdatePosition
            {
                Guid = mob.Guid, Position = mob.Position, Rotation = mob.Rotation, State = 1, Unknown = 0,
            });
            return;
        }

        var dir = to / d;
        var step = MathF.Min(WanderSpeed * dt, d);
        var newPos = new Vector4(here.X + dir.X * step, MoveToward(here.Y, state.Home.Y, MobYSpeed * dt),
            here.Z + dir.Y * step, mob.Position.W);
        var rot = new Quaternion(dir.X, 0f, dir.Y, 0f);

        mob.UpdatePosition(newPos, rot);
        Broadcast(new PlayerUpdatePacketUpdatePosition
        {
            Guid = mob.Guid, Position = newPos, Rotation = rot, State = 0, Unknown = 0,
        });
    }

    public override void OnNpcDamaged(Player player, Npc npc)
    {
        lock (_stateLock)
        {
            if (_mobStates.TryGetValue(npc.Guid, out var state) && !state.Charging)
                BeginCharge(npc, state);
        }
    }

    #endregion

    #region Kills / victory

    public override void OnNpcKilled(Player killer, Npc npc)
    {
        bool wasFrogLog;
        lock (_stateLock)
            wasFrogLog = _frogLogs.Remove(npc);
        if (wasFrogLog)
        {
            // Destroying the log just removes it — permanently stops that log's spawning. Doesn't touch
            // the kill counter or either objective; no win-gate interaction since Frog Logs were never in
            // _mobs to begin with.
            _frogLogNextSpawnTicks.Remove(npc.Guid);
            _frogLogSpawnCounts.Remove(npc.Guid);
            Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } });
            npc.GracefulRemoval = (true, DeathHoldMs, 0, FrogLogDestroyFxId, 1000);
            npc.Dispose();
            _logger.LogInformation("{dungeon}: a Frog Log was destroyed — it will no longer spawn frogs.", Dungeon.Comment);
            return;
        }

        bool allClear;
        bool wasBonusSpirit;
        int? bonusCount = null;
        lock (_stateLock)
        {
            if (!_mobs.Remove(npc))
                return;
            _mobStates.Remove(npc.Guid);
            _killed++;
            wasBonusSpirit = _bonusSpiritGuids.Remove(npc.Guid);
            _frogLogSpawnGuids.Remove(npc.Guid);
            if (wasBonusSpirit && _bonusInteracted < Dungeon.BonusInteractCount)
                bonusCount = ++_bonusInteracted;
            else if (!wasBonusSpirit && Dungeon.BonusTargetCount > 0 && npc.ModelId == Dungeon.BonusTargetModelId && _bonusKilled < Dungeon.BonusTargetCount)
                bonusCount = ++_bonusKilled;
            // A bonus spirit or a frog-log spawn is optional/open-ended content spawned after the fact - it
            // must never block finishing the dungeon, so the win gate only counts the mobs that were part
            // of the original Dungeon.Enemies.
            allClear = !_won && _mobs.All(m => _bonusSpiritGuids.Contains(m.Guid) || _frogLogSpawnGuids.Contains(m.Guid));
        }

        Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } });
        var deathPos = npc.Position;
        // ShowHealthBar is only ever true for a boss mob (see CreateMob: npc.ShowHealthBar = group.Boss;
        // every other spawn path in this zone - regular mobs, bonus spirits, frog-log spawns - sets it
        // false) - reuse it as the boss signal here instead of adding a new field. Captured before Dispose.
        var wasBoss = npc.ShowHealthBar;

        // Real BOSS PLATE removal (op32/sub9, see CreateMob's Enable=true) - drops the on-screen boss
        // health display for everyone the moment he dies, matching the Enable when he spawned. Checked
        // against _mainBossGuid, NOT wasBoss - a mini-boss (Boss=true, e.g. Unruly Elite) never got the
        // Enable in the first place, so it must not get a stray Disable either.
        if (npc.Guid == _mainBossGuid)
            Broadcast(new CombatPacketEnableBossDisplay { Guid = npc.Guid, Enable = false });

        npc.GracefulRemoval = (true, DeathHoldMs, 0, DeathPoofFxId, 1000);
        npc.Dispose();

        // Coin drops only from bosses (live feedback, 2026-07-26) - not every regular kill.
        if (wasBoss)
            GrantKillCoins(killer);

        // Small per-kill XP trickle (see PerKillXp's header comment) - every real kill, not just bosses.
        killer.AwardXp(wasBoss ? PerKillBossXp : PerKillXp);

        // Real power-up drops (user-supplied tooltip, 2026-07-27) - any kill has a chance, not just bosses
        // (matches "items that drop off enemies during combat", no boss-only wording).
        TryDropPowerup(deathPos);

        // Bonus goal progress (kill-based, e.g. Bandit Hideout's "Big Bandits! N/5", OR the interact-then-
        // kill spirits like Cracked Claw Caverns) — a plain count tick until the target's hit, then the
        // same complete banner + Goals-row removal the main objective gets at win.
        if (bonusCount is { } count)
        {
            if (count >= Dungeon.BonusTotal)
            {
                Broadcast(new ObjectiveCompletePacket { ObjectiveId = BonusObjectiveId });
                Broadcast(new UiObjectiveCompletePacket { ObjectiveId = BonusObjectiveId });
                GrantBonusGoalReward();
            }
            else
            {
                // op47/sub2 (UiObjectiveUpdateCountPacket), NOT op45/sub2 (ObjectiveUpdatePacket) - the
                // latter only touches the separate MiniGameGoalState object, not the VISIBLE row (see the
                // 2026-07-26 breakthrough notes in project_cracked_claw_caverns_dungeon.md).
                Broadcast(new UiObjectiveUpdateCountPacket { ObjectiveId = BonusObjectiveId, Count = count });
            }
        }

        if (allClear)
        {
            // An escort dungeon (e.g. Bixie Hive) isn't actually done when the static roster clears - there
            // may be more staged reinforcement waves (or the boss itself, held back as the FINAL stage)
            // still to come. Advance to the next stage instead of ending the encounter; only once every
            // stage has fired AND its spawns are also dead does allClear naturally stay true with nothing
            // left to advance to, and WinEncounter finally runs.
            if (_escortStageIndex < Dungeon.EscortStages.Length)
                AdvanceEscortStage();
            else
                WinEncounter(killer, deathPos);
        }
    }

    // Real retail behavior (wiki "Bonus Rewards" section, freerealms.fandom.com/wiki/Cracked_Claw_Caverns):
    // completing the bonus goal grants EVERY player in the instance one article of clothing from a named
    // set matching their own current job (Archer: Hen Feather, Brawler: Saved by the Bell, Ninja: Kusa,
    // Warrior: Standard Action, Wizard: Novice - Medic isn't an implemented job here). Retail says "the
    // color and body part... is random" - we only have ONE real, verified piece per set (the boots entry
    // already used as the hidden slot in FrostfangArenaZone.GetPrizePreviewFor, matching these exact same
    // set names), not a full random-body-part/color catalog, so every completion grants that same fixed
    // piece rather than a genuinely random one - flagged here rather than silently claimed as complete.
    private void GrantBonusGoalReward()
    {
        foreach (var player in ActivePlayers())
        {
            var reward = FrostfangArenaZone.GetPrizePreviewFor(player).FirstOrDefault();
            if (reward is null)
                continue;

            GrantItem(player, reward.ItemDefId);
            player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = reward.ItemDefId, Quantity = 1 });
            SendReceiveItemText(player, reward.DisplayName);
        }
    }

    // Blue "You receive 1 <item>." toast for this zone's own item grants (bonus-goal reward above).
    // BaseMiniGamePacketHandler.HandleLootWheelStopped (Gateway layer, can't reference this Game-layer
    // class) has its own equivalent copy for the end-of-dungeon wheel-prize grant — see that handler for
    // why ChatPacketDebugChat + a pre-substituted string is used instead of the client's own
    // #count([*item*]) locale template.
    internal static void SendReceiveItemText(Player player, string displayName)
    {
        player.SendTunneled(new ChatPacketDebugChat
        {
            Message = $"<font color='#0000FF'>You receive 1 {(string.IsNullOrEmpty(displayName) ? "item" : displayName)}.</font>",
            PrintToChat = true,
        });
    }

    // Grants one of definitionId to the player: stacks it in the DB (by definition + tint), mirrors it
    // into the in-memory inventory, and tells the client (ItemAdd for a new item, ItemUpdate for an
    // incremented stack). Mirrors QuestManager.GrantItem's proven pattern (same shape, different layer).
    private void GrantItem(Player player, int definitionId)
    {
        if (!_resourceManager.ClientItemDefinitions.TryGetValue(definitionId, out var itemDef))
            return;

        var tint = itemDef.IsTintable ? 0 : itemDef.Icon.TintId;

        int itemId, count;
        using (var db = _dbContextFactory.CreateDbContext())
        {
            var row = db.Characters
                .Where(c => c.Id == Sanctuary.Core.Helpers.GuidHelper.GetPlayerId(player.Guid))
                .Select(c => new
                {
                    Character = c,
                    Item = c.Items.FirstOrDefault(i => i.Definition == definitionId && i.Tint == tint),
                    NextId = c.Items.Max(i => (int?)i.Id) ?? 0
                })
                .FirstOrDefault();

            if (row is null)
                return;

            if (row.Item is not null)
            {
                row.Item.Count += 1;
                itemId = row.Item.Id;
                count = row.Item.Count;
            }
            else
            {
                var dbItem = new Sanctuary.Database.Entities.DbItem { Id = row.NextId + 1, Definition = definitionId, Tint = tint, Count = 1 };
                row.Character.Items.Add(dbItem);
                itemId = dbItem.Id;
                count = 1;
            }

            db.SaveChanges();
        }

        var clientItem = player.Items.FirstOrDefault(x => x.Definition == definitionId && x.Tint == tint);
        if (clientItem is not null)
        {
            clientItem.Count = count;
            player.SendTunneled(new ClientUpdatePacketItemUpdate { ItemGuid = clientItem.Id, Count = clientItem.Count });
        }
        else
        {
            clientItem = new ClientItem { Id = itemId, Tint = tint, Count = count, Definition = definitionId };
            player.Items.Add(clientItem);

            using var writer = new PacketWriter();
            clientItem.Serialize(writer);
            player.SendTunneled(new ClientUpdatePacketItemAdd { Payload = writer.Buffer });
        }
    }

    // Real retail behavior (2026-07-26 reference screenshot): boss kills drop small coin amounts DURING
    // the fight, each with its own "You receive N coins" blue toast - distinct from the fixed Dungeon.Coins
    // grant at the very end. Range comes from Dungeon.BossCoinsMin/Max (per-dungeon tunable, defaults
    // shared) rather than a flat constant, so a dungeon with real wiki data can override it later.

    // Text ids 2/"You receive #count([*item*])" and 3/"You receive #count([*experience*]) and
    // #count([*coins*])" are the real client locale strings for this event (mined from the game's own
    // en_us_data dump), but the wire mechanism the client uses to fill in the #count(...) placeholders
    // from a ChatPacketFromStringId isn't confirmed - that packet has no generic numeric parameter field,
    // and guessing wrong would print the literal broken placeholder text on screen. ChatPacketDebugChat
    // is used instead: real, already-confirmed to accept inline <font color> markup (see its own header
    // comment), with the coin count pre-substituted server-side into plain text - same wording, safe wire
    // format. #0000FF matches ChatPacketFromStringId's own documented "4 = Blue" palette entry.
    private void GrantKillCoins(Player killer)
    {
        var coins = _rng.Next(Dungeon.BossCoinsMin, Dungeon.BossCoinsMax + 1);

        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == Sanctuary.Core.Helpers.GuidHelper.GetPlayerId(killer.Guid));
        if (dbCharacter is null)
            return;

        dbCharacter.Coins += coins;
        dbContext.SaveChanges();
        killer.Coins = dbCharacter.Coins;

        killer.SendTunneled(new ClientUpdatePacketCoinCount { Coins = killer.Coins });
        killer.SendTunneled(new RewardBundlePacket { Coins = coins, Unknown15 = 957 });
        killer.SendTunneled(new ChatPacketDebugChat
        {
            Message = $"<font color='#0000FF'>You receive {coins} coins.</font>",
            PrintToChat = true,
        });
    }

    // Knockout / fail / revive lifecycle lives in CombatEncounterZone — supply the encounter id + log label.
    protected override int FailEncounterId => EncounterId;
    protected override int FailInstanceId => EncounterInstanceId;
    protected override string EncounterLogName => Dungeon.Comment;
    protected override IResourceManager ResourceManagerForPowerups => _resourceManager;

    private void WinEncounter(Player player, Vector4 lastKillPos)
    {
        lock (_stateLock)
            _won = true;

        var enemies = _killed;
        var knockoutsLeft = KnockoutLimit;
        MiniGameGameEndScorePacket MakeScore()
        {
            var s = new MiniGameGameEndScorePacket();
            s.Rows.Add(new MiniGameScoreRow { Name = "scoreEnemiesDefeated", Order = 0, Value = enemies, Points = enemies * 300 });
            s.Rows.Add(new MiniGameScoreRow { Name = "scorePlayerKnockouts", Order = 3, Value = knockoutsLeft, Max = KnockoutLimit, Points = knockoutsLeft * 5000 });
            s.Rows.Add(new MiniGameScoreRow { Name = "scoreTotalScore", Order = 4, Points = enemies * 300 + knockoutsLeft * 5000 });
            return s;
        }

        foreach (var member in ActivePlayers())
        {
            member.SendTunneled(new ObjectiveCompletePacket { ObjectiveId = EncounterId });
            member.SendTunneled(new UiObjectiveCompletePacket { ObjectiveId = EncounterId });

            member.AwardXp(Dungeon.Xp);
            // The GRANT BANNER (RewardBundlePacket) is held until the wheel stops - see
            // HandleLootWheelStopped - so it lands in ONE combined "here's everything you got" popup
            // alongside the coins/item, instead of firing as its own disconnected toast the instant you
            // win, before the score/reward card is even up.
            member.PendingWheelXp = Dungeon.Xp;

            _questManager.OnEncounterComplete(member, EncounterId);

            var prizes = FrostfangArenaZone.GetPrizePreviewFor(member);
            var slice = _rng.Next(prizes.Count + 1);
            var wheel = new MiniGameLootWheelSetItemToLandOnPacket();
            if (slice < prizes.Count)
            {
                member.PendingWheelPrize = prizes[slice];
                member.PendingWheelCoins = 0;
                wheel.Entries.Add(prizes[slice]);
            }
            else
            {
                member.PendingWheelPrize = null;
                member.PendingWheelCoins = Dungeon.Coins;
                wheel.Coins = Dungeon.Coins;
            }

            member.SendTunneled(wheel);
            member.SendTunneled(MakeScore());
        }

        // Escort victory line (e.g. Bixie Hive's Queen: "Thank you so much for saving me!") + the Mystery
        // Chest on the final kill's body - both opt-in, 0/none for every dungeon that doesn't set them.
        if (Dungeon.EscortVictoryLineId != 0 && _escort is { } escort)
            AnnounceEscortText(escort, Dungeon.EscortVictoryLineId);
        if (Dungeon.MysteryChestModelId > 0)
            SpawnMysteryChest(lastKillPos);

        SpawnExitDoor(player);
        _logger.LogInformation("{dungeon}: WON — wheel armed, exit door out ({kills} enemies).", Dungeon.Comment, enemies);
    }

    // A clickable "Mystery Chest" prop (Dungeon.MysteryChestModelId, e.g. Bixie Hive's real
    // sg_mystery_chest_01.adr) left on the final kill's body at victory. Same clickable-prop recipe as
    // CreateBonusInteractable (IsInteractable=true, hand cursor). Not tracked in _mobs/objectives - purely
    // a bonus loot prop, opening it just grants an item and removes itself.
    private void SpawnMysteryChest(Vector4 pos)
    {
        if (!TryCreateNpc(out var npc))
            return;

        npc.ModelId = Dungeon.MysteryChestModelId;
        npc.NameId = Dungeon.MysteryChestNameId;
        // UNVERIFIED (live feedback 2026-07-28: "mystery chest model... should be golden") - Models.txt has
        // no color/description data for any of the 5 real mystery-chest tiers, and no existing NPC entry
        // uses this model to crib a real tint from. "gold" matches this game's other real tint-alias naming
        // convention (Frostfang's wolves: "evil_purple"/"snow_blue"/"base_metal") but is a best-effort
        // guess, not a sourced value - if it doesn't render gold in-game, flag it and we'll try TintId
        // instead or track down the real tier that's actually golden by default.
        npc.TintAlias = "gold";
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = false;
        npc.Scale = 1f;
        npc.Disposition = 1; // neutral, not a combat target
        npc.CompositeEffectId = 0;
        npc.MaxHealth = 0;
        npc.IsInteractable = true;
        npc.InteractRange = 6; // matches CreateBonusInteractable's real-prop tuning
        npc.Visible = true;
        npc.CursorId = 17; // hand cursor
        npc.Static = true;
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = MovementTypePhysics;
        npc.RiderGuid = ulong.MaxValue;

        npc.UpdatePosition(pos, Quaternion.Identity);
        npc.InteractAction = player => OnMysteryChestInteract(npc, player);

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);
            SendNpcRelevance(p, npc);
        }
    }

    // The chest was clicked - grant the CLICKING player a real item, then remove it so it can't be opened
    // twice (by anyone). CORRECTED 2026-07-28 (live feedback: "the mystery chest should give 1 mystery
    // chest item") - there is NO "Mystery Chest" item anywhere in this game's resources (checked by name,
    // by its exact locale text id, and by item-model reference - only the world-prop MODEL exists, no
    // matching grantable item), so it was granting the real Battle Item Mystery Pack (10482, every
    // dungeon's own fixed reward) as the closest substitute. Per the user's own explicit choice, switched
    // to the real "Treasure Chest" item (3016) instead - a genuine housing decoration/furniture piece
    // (hsg_chest_02.adr), not a lootbox, but a real grantable item rather than an invented one.
    private const int MysteryPackItemDefId = 3016;
    private void OnMysteryChestInteract(Npc npc, Player player)
    {
        lock (_stateLock)
        {
            if (_mysteryChestOpened)
                return;
            _mysteryChestOpened = true;
        }

        npc.GracefulRemoval = (false, 0, 0, DeathPoofFxId, 1000);
        npc.Dispose();

        GrantItem(player, MysteryPackItemDefId);
        player.SendTunneled(new RewardNonBundledItemPacket { ItemDefinitionId = MysteryPackItemDefId, Quantity = 1 });
        SendReceiveItemText(player, "Treasure Chest");

        _logger.LogInformation("{dungeon}: Mystery Chest opened by {name}.", Dungeon.Comment, player.Name);
    }

    private void SpawnExitDoor(Player player)
    {
        if (!TryCreateNpc(out var door))
            return;

        door.ModelId = DoorModelId;
        door.NameId = DoorNameId;
        door.Name = null;
        door.Disposition = 0;
        door.Scale = DoorScale;
        door.IsInteractable = true;
        door.InteractRange = DoorInteractRange;
        door.Visible = true;
        door.MaxHealth = 0;
        door.ShowHealthBar = false;
        door.HideNamePlate = false;
        door.ActiveProfile = DoorActiveProfile;
        door.CursorId = DoorCursorId;
        door.WalkAnimId = -1;
        door.RunAnimId = -1;
        door.StandAnimId = -1;
        door.MovementType = MovementTypePhysics;
        door.RiderGuid = ulong.MaxValue;
        // Arena: near center (by the spawn). Walk-through: at the FAR end, where the player finishes the
        // last cluster — a portal out at the end of the dungeon (the arena's 125u interact range wouldn't
        // reach the far end of a big map from center). ExitOverride uses a REAL captured exit point instead
        // when one is known.
        if (Dungeon.ExitOverride is { } eo)
        {
            door.UpdatePosition(new Vector4(eo.X, eo.Y, eo.Z, 1f), Quaternion.Identity);
        }
        else
        {
            var doorZ = Dungeon.Radius > WalkThroughRadius
                ? Dungeon.CenterZ + Dungeon.Radius * SafeReach
                : Dungeon.CenterZ - 12f;
            door.UpdatePosition(new Vector4(Dungeon.CenterX, _groundY, doorZ, 1f), Quaternion.Identity);
        }

        var badge = new PlayerUpdatePacketAddNotifications();
        badge.Notifications.Add(new NotificationInfo
        {
            Guid = door.Guid, Combat = false, Type = DoorBadgeType, Unknown3 = DoorBadgeUnknown3,
            ImageId = DoorMinimapImageId, DescriptionId = 0, NameId = DoorNameId, SubTextId = -1,
            Unknown8 = true, CompositeEffectId = 0, Unknown10 = true,
        });

        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(door);
            door.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = door.Guid, Disposition = 1 });
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = door.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });
            SendNpcRelevance(p, door);
            p.SendTunneled(badge);
        }

        SetExitDoor(door);
    }

    protected override void ReturnHome(Player player, bool immediate)
    {
        if (player.Zone != this)
            return;

        bool won;
        lock (_stateLock)
            won = _won;

        EndEncounterForPlayer(player, won);

        var home = _zoneManager.StartingZone;
        var returnPos = player.EncounterReturnPosition ?? home.SpawnPosition;
        player.EncounterReturnPosition = null;

        if (won && !immediate)
        {
            // Don't teleport yet — wait for the REAL "the reward wheel finished spinning" signal
            // (NotifyRewardWheelStopped, fired from op39/sub46 LootWheelOnRotationStopped) instead of a
            // fixed delay from the door click, so the player has actually SEEN their prize before the
            // screen changes rather than the timer possibly running out mid-spin. WinReturnFallbackMs is a
            // safety net in case that signal never arrives (e.g. a dropped packet) so they're never stuck.
            lock (_stateLock)
                _pendingWinReturn[player.Guid] = returnPos;

            _ = Task.Run(async () =>
            {
                await Task.Delay(WinReturnFallbackMs);
                TryCompleteWinReturn(player);
            });
        }
        else
        {
            lock (_stateLock)
                _pendingWinReturn.Remove(player.Guid);
            player.TeleportToZone(home, returnPos, home.SpawnRotation, sky: null, geometryId: 0);
        }
    }

    // The real trigger: the client reports the reward wheel actually finished spinning (op39/sub46,
    // BaseMiniGamePacketHandler.HandleLootWheelStopped) — used instead of timing from the door click so the
    // hold can't expire while the player is still watching the wheel animate. No-op if this player isn't
    // currently in the post-win "clicked the door, waiting to leave" state (e.g. a stray/duplicate packet,
    // or they already left via the card's own Leave button).
    public override void NotifyRewardWheelStopped(Player player)
    {
        bool pending;
        lock (_stateLock)
            pending = _pendingWinReturn.ContainsKey(player.Guid);
        if (!pending)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(WinCardDelayMs);
            TryCompleteWinReturn(player);
        });
    }

    // Shared completion for both the wheel-stopped path and the fallback timer - whichever fires first
    // wins, the Dictionary.Remove(key, out _) below only succeeds once so a race between the two can't
    // double-teleport.
    private void TryCompleteWinReturn(Player player)
    {
        Vector4 returnPos;
        lock (_stateLock)
        {
            if (!_pendingWinReturn.Remove(player.Guid, out returnPos))
                return; // already handled (card's own Leave button, or the other timer already fired)
        }

        if (player.Zone != this)
            return;

        var home = _zoneManager.StartingZone;
        player.TeleportToZone(home, returnPos, home.SpawnRotation, sky: null, geometryId: 0);
    }


    #endregion
}
