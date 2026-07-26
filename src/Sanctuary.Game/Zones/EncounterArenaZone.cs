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
    private const int EncounterInstanceId = 1;

    private const int CombatMiniGameType = 4; // client MINI_GAME_TYPE_COMBAT — the goals-pane gate
    // KnockoutLimit + the knockout/fail/revive lifecycle now live in CombatEncounterZone.

    // Enemy recipe (Frostfang pack-wolf / spirit recipe).
    private const int MobActiveProfile = 151;
    private const int SpawnPoofFxId = 46;
    private const int DeathPoofFxId = 5017;
    private const int DeathHoldMs = 1500;
    private const int CharState_Baseline = 0x1;
    private const int CharState_Charging = 0x8001;
    private const int MovementTypePhysics = 2;

    // Chase/claw tuning lives in CombatEncounterZone now; only the approach-aggro range is per-zone here.
    private const float AggroRange = 16f;

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

    private sealed class MobState : EncounterMobState { }

    private readonly object _stateLock = new();
    private readonly List<Npc> _mobs = [];
    private readonly Dictionary<ulong, MobState> _mobStates = [];
    private int _killed;
    private int _bonusKilled;
    private bool _won;
    private int _encounterRun;
    private float _groundY;

    private readonly List<Player> _activePlayers = [];
    private Player? _anchor;

    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;
    private readonly Sanctuary.Game.Quests.IQuestManager _questManager;
    private readonly Random _rng = new();

    public EncounterArenaZone(DungeonDefinition dungeon, IServiceProvider serviceProvider)
        : base(CreateDefinition(dungeon), serviceProvider)
    {
        Dungeon = dungeon;
        _groundY = dungeon.GroundY;
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _questManager = serviceProvider.GetRequiredService<Sanctuary.Game.Quests.IQuestManager>();
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

    private void StartEncounter(Player player)
    {
        var spawns = BuildDungeonSpawns();

        lock (_stateLock)
        {
            foreach (var old in _mobs)
                old.Dispose();
            _mobs.Clear();
            _mobStates.Clear();
            ExitDoor?.Dispose();
            SetExitDoor(null);
            _killed = 0;
            _bonusKilled = 0;
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

            SendCombatMinimapMarkers(guids);
        }

        DeliverEntrySequence(player, _encounterRun);

        _logger.LogInformation("{dungeon}: encounter start for {name} — {n} enemies pre-spawned in {world}.",
            Dungeon.Comment, player.Name, Dungeon.TotalEnemies, Dungeon.World);

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
    private List<Vector4> BuildDungeonSpawns()
    {
        // A zone script (Scripts/Zone/<world>.lua) can supply fixed spawn points via a getSpawnPoints(zone)
        // function that calls zone.addSpawnPoint(x, y, z) once per enemy, in the same group order as
        // Dungeon.Enemies. Only used if it reports EXACTLY the expected count — a mismatch (script written
        // for a different enemy composition than what's currently in DungeonDefinition) falls back to the
        // procedural layout below rather than silently spawning too few/many enemies.
        var scripted = CollectScriptSpawnPoints("getSpawnPoints");
        if (scripted.Count == Dungeon.TotalEnemies)
            return scripted;

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

                EncounterDetailsResponsePacket MakeLaunch() => new()
                {
                    Unknown = EncounterId,
                    Unknown2 = EncounterInstanceId,
                    NameId = Dungeon.TitleNameId,
                    DescriptionId = Dungeon.DescriptionId,
                    Difficulty = Dungeon.Difficulty,
                    IconId = Dungeon.IconId,
                    MiniGameType = CombatMiniGameType,
                    Launch = true,
                    // Real retail goal for a dungeon with a BonusTargetCount (e.g. Bandit Hideout's
                    // "Defeat all of the Big Bandits! 0/5") is a SECOND objective row alongside the main
                    // "defeat everyone" one — NameId=0 renders blank until we source the real text id.
                    Objectives = Dungeon.BonusTargetCount > 0
                        ?
                        [
                            new EncounterObjective
                            {
                                ObjectiveId = EncounterId, NameId = Dungeon.DescriptionId,
                                DescriptionId = Dungeon.DescriptionId,
                                Status = 1, Count = 0, Total = 1, Unknown8 = 0,
                            },
                            new EncounterObjective
                            {
                                ObjectiveId = BonusObjectiveId, NameId = 0,
                                DescriptionId = 0,
                                Status = 1, Count = 0, Total = Dungeon.BonusTargetCount, Unknown8 = 0,
                            },
                        ]
                        :
                        [
                            new EncounterObjective
                            {
                                ObjectiveId = EncounterId, NameId = Dungeon.DescriptionId,
                                DescriptionId = Dungeon.DescriptionId,
                                Status = 1, Count = 0, Total = 1, Unknown8 = 0,
                            },
                        ],
                    PreviewRewards = FrostfangArenaZone.GetPrizePreviewFor(player),
                    PreviewCoins = FrostfangArenaZone.PrizeCoins,
                    PreviewXp = FrostfangArenaZone.PrizeXp,
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
                    NameId = Dungeon.DescriptionId,
                };

                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit));
                player.SendTunneled(new ObjectiveActivatePacket { ObjectiveId = EncounterId, Total = 1 });
                player.SendTunneled(GoalRow());
                if (Dungeon.BonusTargetCount > 0)
                {
                    player.SendTunneled(new ObjectiveActivatePacket { ObjectiveId = BonusObjectiveId, Total = Dungeon.BonusTargetCount });
                    player.SendTunneled(new UiObjectiveAddPacket { ObjectiveId = BonusObjectiveId, NameId = 0 });
                }
                player.SendTunneled(MakeLaunch());
                player.SendTunneled(MakeEnter(0));
                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit));
                player.SendTunneled(MakeLaunch());
                player.SendTunneled(GoalRow());
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
        // Boss shows the dungeon name; regular mobs show a NAMELESS plate so their HEALTH BAR still renders
        // (the bar is a nameplate element — a hidden plate meant regular mobs had no bar, only a flash-on-hit,
        // which read as "health bars sometimes pop up, sometimes not").
        npc.NameId = group.Boss ? Dungeon.TitleNameId : 0;
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = true;
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

        return npc;
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

                    Npc[] pack;
                    lock (_stateLock)
                        pack = [.. _mobs];
                    if (pack.Length == 0)
                        continue;

                    var now = Environment.TickCount64;
                    var dt = TickMs / 1000f;

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
                            TickMobReturnHome(mob, state, dt);
                            continue;
                        }

                        var target = new Vector3(tgt.Position.X, tgt.Position.Y, tgt.Position.Z);

                        // Aggro on approach, then run the shared chase/plant/attack tick.
                        if (!state.Charging)
                        {
                            var dx = target.X - here.X;
                            var dz = target.Z - here.Z;
                            if (dx * dx + dz * dz > AggroRange * AggroRange)
                                continue;
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
        bool allClear;
        int? bonusCount = null;
        lock (_stateLock)
        {
            if (!_mobs.Remove(npc))
                return;
            _mobStates.Remove(npc.Guid);
            _killed++;
            if (Dungeon.BonusTargetCount > 0 && npc.ModelId == Dungeon.BonusTargetModelId && _bonusKilled < Dungeon.BonusTargetCount)
                bonusCount = ++_bonusKilled;
            allClear = !_won && _mobs.Count == 0;
        }

        Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } });
        var deathPos = npc.Position;

        npc.GracefulRemoval = (true, DeathHoldMs, 0, DeathPoofFxId, 1000);
        npc.Dispose();

        // Bonus goal progress (e.g. Bandit Hideout's "Big Bandits! N/5") — a plain count tick until the
        // target's hit, then the same complete banner + Goals-row removal the main objective gets at win.
        if (bonusCount is { } count)
        {
            if (count >= Dungeon.BonusTargetCount)
            {
                Broadcast(new ObjectiveCompletePacket { ObjectiveId = BonusObjectiveId });
                Broadcast(new UiObjectiveCompletePacket { ObjectiveId = BonusObjectiveId });
            }
            else
            {
                Broadcast(new ObjectiveUpdatePacket { ObjectiveId = BonusObjectiveId, Count = count });
            }
        }

        if (allClear)
            WinEncounter(killer, deathPos);
    }

    // Knockout / fail / revive lifecycle lives in CombatEncounterZone — supply the encounter id + log label.
    protected override int FailEncounterId => EncounterId;
    protected override int FailInstanceId => EncounterInstanceId;
    protected override string EncounterLogName => Dungeon.Comment;

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
            member.SendTunneled(new RewardBundlePacket { Xp = Dungeon.Xp });

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
                member.PendingWheelCoins = FrostfangArenaZone.PrizeCoins;
                wheel.Coins = FrostfangArenaZone.PrizeCoins;
            }

            member.SendTunneled(wheel);
            member.SendTunneled(MakeScore());
        }

        SpawnExitDoor(player);
        _logger.LogInformation("{dungeon}: WON — wheel armed, exit door out ({kills} enemies).", Dungeon.Comment, enemies);
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

    protected override void ReturnHome(Player player)
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
        player.TeleportToZone(home, returnPos, home.SpawnRotation, sky: null, geometryId: 0);
    }


    #endregion
}
