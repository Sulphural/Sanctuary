using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Zones;

// INSTANCE (Tormented Spirits!): the Blackspore graveyard combat encounter — activity id 146, the
// direct sibling of the Frostfang Growler (174): same Category 99 "wandering combat encounter",
// same launch pipeline, Difficulty 2. Entered by clicking a Tormented Spirit wandering near the
// sinking graveyard south of Blackspore (the ninja job quest 31442 "That's the Spirit" targets it).
//
// The world comes from the CLIENT'S OWN DATA (pack extract 2026-07-10):
//   world  = bs_random_encounter_01 (the graveyard clearing; fog/bats/fireflies + its own combat
//            music are baked into the world's own Areas.xml)
//   center = (141, y≈2, 160) radius 100, from bs_random_encounter_01Areas.xml ("Bed" AreaDefinition)
//
// ENCOUNTER SPEC (no live capture exists for this one — reference videos 0yGtjJBzmGw + zyWxEY1AcmY
// and the user's frame audit 2026-07-10):
//   * the spirits are PRE-SPAWNED — no waves. The player fights the whole graveyard.
//   * 3 destroyable TOMBSTONES are scattered among the graves; all 3 must be destroyed. Destroying
//     one materializes an extra spirit — the client string for the beat is 139366 "The bones crumble
//     and a tormented spirit materializes!".
//   * win when every tombstone is destroyed and every spirit is defeated; then the same win flow as
//     Frostfang: goal complete + loot wheel + score card + exit door (user-confirmed from the videos).
//
// TEXT IDS (reversed from en_us_data via the Jenkins lookup2 CID map, 2026-07-10 — the tight id
// cluster 75999/76190/76354/76363/76373 validates them as this encounter's own block):
//   75999  "Tormented Spirits!"                                              (activity title)
//   76190  "Tormented Spirit"                                                (the enemy NPC name)
//   76354  "Evil spirits are haunting this swamp, banish them from the land!" (the Goals-pane row)
//   76363  "Tormented spirits are attacking travelers! Go in and put them to rest!" (description)
//   76373  "Tombstone"                                                       (the destroyable's name)
//   139366 "The bones crumble and a tormented spirit materializes!"          (tombstone-destroyed)
public sealed class TormentedSpiritsArenaZone : CombatEncounterZone
{
    private sealed class TormentedSpiritsArenaDefinition : BaseZoneDefinition
    {
    }

    // Bed area y1=2 in the world's Areas.xml — WRONG (first live run 2026-07-10: the player spawned
    // slightly below the mesh, so the real ground is above 2). The zone SELF-CALIBRATES: the player
    // spawns high (the client settle-drops onto the real terrain — Frostfang-proven), and ~3s later
    // the measured player Y is adopted as the ground level for every idle spirit/tombstone/door
    // (see the ground-adoption task in StartEncounter). GroundY stays the initial-guess constant.
    private const float GroundY = 2f;
    private const float CenterX = 141f;
    private const float CenterZ = 160f;

    // The adopted real ground height (starts at the GroundY guess; overwritten by the
    // ground-adoption measurement each run).
    private float _groundY = GroundY;

    public const int EncounterId = 146;   // ClientActivityDefinitions "Tormented Spirits!"
    public const int EncounterInstanceId = 1;

    public const int TitleNameId = 75999;
    public const int DescriptionId = 76363;
    public const int Difficulty = 2;      // matches the activity definition
    public const int IconId = 1345;       // the combat-encounter swords emblem (live-proven on 174)

    // World NPCs with this NameId ("Tormented Spirit") are the wandering encounter
    // entries — clicking one in the overworld opens the offer popup (the Growler-wolf pattern).
    public const int EntryNpcNameId = 76190;

    private const int CombatMiniGameType = 4; // client MINI_GAME_TYPE_COMBAT — the goals-pane gate

    // ── Enemy identities ─────────────────────────────────────────────────────────────────────────────
    private const int SpiritModelId = 10;        // ghostdwarf_m_miner_01.adr (the world spirits' model)
    private const int SpiritNameId = EntryNpcNameId;
    private const int SpiritActiveProfile = 151; // non-zero -> the client re-runs the red-name resolver
                                                 // (same value our Frostfang pack uses)

    private const int SpiritHealth = 1500;       // Difficulty 2: ~2 ninja basic hits (Frostfang wolves = 1)
    private const float SpiritAggroRange = 14f;  // pre-spawned mobs engage on approach (no charge-at-spawn)

    private const int SpawnPoofFxId = 46;        // the live wave-wolf spawn poof — reused for materializing
    private const int DeathPoofFxId = 5017;      // the standard death poof
    private const int SpiritDeathHoldMs = 2000;  // death clip plays before the poof

    private const int CharState_Baseline = 0x1;
    private const int CharState_Charging = 0x8001; // spirits have no overhead plates, so bit15 is safe

    // ── Tombstones (the 3 destroyables) ─────────────────────────────────────────────────────────────
    private const int TombstoneNameId = 76373;   // "Tombstone"
    private const int TombstoneHealth = 1500;
    private static readonly int[] TombstoneModelIds = [893, 894, 896]; // bs_gravestone_01/02/04.adr

    // Chase-and-claw AI tuning lives in CombatEncounterZone now (shared by all encounter zones).

    // Pre-spawned spirit positions — scattered through the graveyard around center (141, 160).
    // Hand-placed (no capture): a loose ring through the graves, none on the player's south approach.
    private static readonly Vector3[] SpiritSpawns =
    [
        new(120f, GroundY, 148f), new(158f, GroundY, 145f), new(130f, GroundY, 175f),
        new(152f, GroundY, 178f), new(115f, GroundY, 165f), new(165f, GroundY, 162f),
        new(138f, GroundY, 190f), new(125f, GroundY, 133f), new(155f, GroundY, 133f),
        new(170f, GroundY, 180f), new(112f, GroundY, 182f), new(147f, GroundY, 161f),
    ];

    // The 3 tombstones — spread across the graveyard so the player sweeps the whole field.
    private static readonly Vector3[] TombstoneSpawns =
    [
        new(125f, GroundY, 155f), new(155f, GroundY, 170f), new(140f, GroundY, 185f),
    ];

    // ── Exit door — same live-decoded recipe as the Frostfang arena's ───────────────────────────────
    private const int DoorModelId = 846;         // sg_exit_door_01.adr
    private const int DoorNameId = 4826;
    private const float DoorScale = 1.2f;
    private const int DoorInteractRange = 125;
    private const int DoorActiveProfile = 28;
    private const int DoorCursorId = 17;
    private const int DoorMinimapImageId = 186;
    private const int DoorBadgeType = 7;
    private const int DoorBadgeUnknown3 = 102;
    private static readonly Vector4 DoorSpawn = new(141f, GroundY, 148f, 1f); // near the south approach

    // Coin pop + hearts — the shared combat-encounter pickups (see FrostfangArenaZone for the
    // heart's full decode; params verbatim from that work).
    private const int CoinsModelId = 841;
    private const int CoinsNameId = 139649;
    private const int CoinsPopFxId = 5192;
    private const float CoinsKnockMagnitude = 0.0712f;
    private const int HeartModelId = 736;
    private const int HeartHeal = 125;
    private const float HeartPickupRange = 2.6f;
    private const int HeartDropPercent = 12;
    private const int HeartPickupFxId = 15032;
    private const int HealShowerFxId = 15921;
    private const int HealShowerMs = 15000;
    private int _healTagCounter = 300;
    private readonly List<Npc> _hearts = [];

    private const int WolfMovementTypePhysics = 2; // client op125 gate: PHYSICS auto-plays locomotion

    // THE Goals-window goal. NO live capture for this encounter, so the OBJECTIVE ID is ours (any
    // unique int works — the client uses it purely as a row key); the TEXT is the client's own
    // 76354 "Evil spirits are haunting this swamp, banish them from the land!".
    private const int GoalBanishSpirits = 12646;
    private const int GoalBanishSpiritsNameId = 76354;

    // KnockoutLimit + the knockout/fail/revive lifecycle now live in CombatEncounterZone.

    // Job XP at the win. No capture; Frostfang (Difficulty 1) grants 10, so Difficulty 2
    // grants a bit more.
    public const int EncounterXp = 15;

    // Per-kill XP (added 2026-07-29, live feedback: "dungeon/encounter enemies should give a small amount
    // of exp when killing them") - see EncounterArenaZone.PerKillXp's header comment for the full reasoning;
    // same small-trickle convention, scaled to this encounter's own EncounterXp (15). Tombstones don't get
    // this - they're a destructible prop, not an enemy (the spirit THEY spawn does, once it's actually killed).
    private const int PerKillXp = 2;

    private sealed class SpiritState : EncounterMobState { }

    private readonly object _stateLock = new();
    private readonly List<Npc> _spirits = [];
    private readonly Dictionary<ulong, SpiritState> _spiritStates = [];
    private readonly List<Npc> _tombstones = [];
    private int _killedSpirits;
    private bool _won;
    private int _encounterRun;

    // PARTY CO-OP (mirrors FrostfangArenaZone): the players currently in this arena instance. The
    // encounter runs ONCE (started by the first entrant = the party leader who pressed GO!); co-entrants
    // join the running fight rather than resetting it. Every shared encounter packet is Broadcast to all
    // of them, so a solo player (party of one) behaves exactly as before. The AI anchors on the first
    // entrant (_anchor) for spirit targeting + ground adoption.
    private readonly List<Player> _activePlayers = [];
    private Player? _anchor;

    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;
    private readonly Sanctuary.Game.Quests.IQuestManager _questManager;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext> _dbContextFactory;
    private readonly Random _rng = new();

    public TormentedSpiritsArenaZone(IServiceProvider serviceProvider)
        : base(CreateDefinition(), serviceProvider)
    {
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _questManager = serviceProvider.GetRequiredService<Sanctuary.Game.Quests.IQuestManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext>>();

        // The spirits run the shared TickMobCombat/TickMobReturnHome chase, which routes through
        // ChaseStep - but without this call NavObstacles/NavGraph stayed null, so every one of those
        // lookups fell straight back to a plain straight line and the spirits had no wall awareness at
        // all. The generic EncounterArenaZone has always made this call; the two bespoke arenas never did.
        // Uses the GroundY guess rather than the runtime-adopted _groundY: the graph is built once here,
        // before StartEncounter's ground-adoption task runs, and the grid only needs a representative
        // floor height to sample the arena's walkable circle at.
        BuildMobPathfinding("bs_random_encounter_01", new Vector4(CenterX, GroundY, CenterZ, 1f), 100f);
    }

    private static BaseZoneDefinition CreateDefinition() => new TormentedSpiritsArenaDefinition
    {
        Id = EncounterId, // traceability; the runtime zone Id is assigned by the manager
        Name = "bs_random_encounter_01",
        TileSize = 64,
        StartLongitude = -2,
        EndLongitude = 8,
        StartLatitude = -2,
        EndLatitude = 8,
        Sky = null, // the world's own gloomy Areas.xml ambience (fog/bats) does the mood
        // South edge of the graveyard clearing, walking north into the fog (no capture — mirrors the
        // Frostfang long-approach feel; center is (141, 160) r100). Spawn HIGH: the GroundY guess put
        // the player under the mesh (live 2026-07-10); dropping from above lets the client settle
        // onto the real terrain, and the ground-adoption pass then reads the settled Y.
        SpawnPosition = new Vector4(141f, GroundY + 12f, 118f, 1f),
        SpawnRotation = Quaternion.Identity,
    };

    #region Zone lifecycle

    public override void OnClientIsReady(Player player)
    {
        // Same zone-in tail as the Frostfang arena (see that class for the full derivation).
        EnterAtFullVitals(player); // real max HP + mana so the bar doesn't jump on the first claw

        player.SendTunneled(new PacketZoneDoneSendingInitialData());
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());

        // Any kit job (ninja/archer) gets its weapon toolbar + FX cache warm-up.
        JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager);
    }

    // The load screen has dropped — the client accepts AddNpc from here (Frostfang LIVE TESTS 8+9).
    public override void OnClientFinishedLoading(Player player)
    {
        // Prune anyone who has already left (so a solo re-entry resets a stale instance cleanly).
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
            // First entrant (the party leader who pressed GO!) — spawn the encounter + start the AI.
            _anchor = player;
            StartEncounter(player);
        }
        else
        {
            // A party member joining the running fight: don't reset it — deliver the combat gate +
            // goals to THEM, and push the currently-alive spirits/tombstones so they see the fight.
            _logger.LogInformation("Spirit arena: {name} joined the party fight (member #{n}).",
                player.Name, _activePlayers.Count);
            DeliverEntrySequence(player, _encounterRun);
            PushLiveEncounterTo(player);
        }
    }

    // Broadcast a shared encounter packet to every player currently in this arena instance.
    // For a solo player this is exactly the old per-player send; for a party it drives everyone.
    protected override void Broadcast(ISerializablePacket packet)
    {
        foreach (var p in ActivePlayers())
            p.SendTunneled(packet);
    }

    // Snapshot of the players currently in this arena instance (co-op recipients). Prunes any
    // who have left (teleported away) so a departed member never receives encounter packets and the
    // instance can reset once it truly empties.
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

    // Push the currently-alive encounter NPCs (spirits/tombstones/hearts/door) to a player
    // who just joined mid-fight, so the running encounter is visible to them.
    private void PushLiveEncounterTo(Player player)
    {
        List<Npc> live = [];
        lock (_stateLock)
        {
            live.AddRange(_spirits);
            live.AddRange(_tombstones);
            live.AddRange(_hearts);
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
        lock (_stateLock)
        {
            foreach (var old in _spirits)
                old.Dispose();
            _spirits.Clear();
            _spiritStates.Clear();
            foreach (var t in _tombstones)
                t.Dispose();
            _tombstones.Clear();
            foreach (var h in _hearts)
                h.Dispose();
            _hearts.Clear();
            ExitDoor?.Dispose();
            SetExitDoor(null);
            _killedSpirits = 0;
            _won = false;
            _groundY = GroundY; // re-measured by this run's ground-adoption pass
            _encounterRun++;

            // PRE-SPAWNED: the whole graveyard is up before the player takes a step (the videos show
            // spirits already wandering as the player loads in — no waves).
            var guids = new List<ulong>(SpiritSpawns.Length + TombstoneSpawns.Length);
            foreach (var pt in SpiritSpawns)
            {
                var spirit = CreateSpirit(player, new Vector4(pt.X, pt.Y, pt.Z, 1f), spawnFx: 0);
                if (spirit is null)
                    continue;
                _spirits.Add(spirit);
                _spiritStates[spirit.Guid] = new SpiritState { SlotAngle = (float)(_rng.NextDouble() * Math.Tau), Home = spirit.Position };
                guids.Add(spirit.Guid);
            }

            for (var i = 0; i < TombstoneSpawns.Length; i++)
            {
                var tomb = CreateTombstone(player, TombstoneModelIds[i % TombstoneModelIds.Length], TombstoneSpawns[i]);
                if (tomb is null)
                    continue;
                _tombstones.Add(tomb);
                guids.Add(tomb.Guid);
            }

            SendCombatMinimapMarkers(player, guids);
        }

        // The combat gate + goals — delivered per-player (each member needs their own MiniGameState).
        DeliverEntrySequence(player, _encounterRun);

        _logger.LogInformation(
            "Spirit arena: encounter start for {name} — {spirits} spirits + {tombs} tombstones pre-spawned.",
            player.Name, SpiritSpawns.Length, TombstoneSpawns.Length);

        // GROUND ADOPTION: no capture ground-truths this world's terrain height, so measure it. The
        // player spawns ~12u up and the client settles them onto the real mesh; ~3s later their Y IS
        // the ground. Adopt it and snap every idle actor (spirits/tombstones) onto it — the AddNpc
        // heights used the GroundY guess and may be under/over the terrain. (Charging spirits already
        // converge to the player's Y in the AI loop; the exit door spawns later using _groundY.)
        var groundRun = _encounterRun;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);

                if (player.Zone != this || groundRun != _encounterRun)
                    return;

                var measured = player.Position.Y;
                _logger.LogInformation("Spirit arena: ground adoption — player settled at y={y} (guess was {guess}).",
                    measured, GroundY);

                if (MathF.Abs(measured - GroundY) < 0.75f)
                    return; // the guess was good enough; leave the actors where they are

                Npc[] spirits;
                Npc[] tombstones;
                lock (_stateLock)
                {
                    _groundY = measured;
                    spirits = [.. _spirits];
                    tombstones = [.. _tombstones];
                }

                foreach (var actor in spirits)
                {
                    bool idle;
                    lock (_stateLock)
                        idle = _spiritStates.TryGetValue(actor.Guid, out var s) && !s.Charging;
                    if (!idle)
                        continue; // chasers converge to the player's Y on their own

                    var p = actor.Position;
                    var lifted = new Vector4(p.X, measured, p.Z, p.W);
                    actor.UpdatePosition(lifted, actor.Rotation);
                    Broadcast(new PlayerUpdatePacketUpdatePosition
                    {
                        Guid = actor.Guid, Position = lifted, Rotation = actor.Rotation, State = 1, Unknown = 0,
                    });
                }

                foreach (var tomb in tombstones)
                {
                    var p = tomb.Position;
                    var lifted = new Vector4(p.X, measured, p.Z, p.W);
                    tomb.UpdatePosition(lifted, tomb.Rotation);
                    Broadcast(new PlayerUpdatePacketUpdatePosition
                    {
                        Guid = tomb.Guid, Position = lifted, Rotation = tomb.Rotation, State = 1, Unknown = 0,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spirit arena: ground adoption failed.");
            }
        });

        StartSpiritAi(player, _encounterRun);
    }

    // The per-player combat gate + goals burst, sent a beat after the load settles. Called for
    // the anchor at StartEncounter AND for every party member who joins the running fight, so each gets
    // their own MiniGameState (without which op45 goal packets are dropped and the goals pane never shows).
    // Structure is the exact live Frostfang entry sequence (launch twice with a PlayerEnter between).
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
                    NameId = TitleNameId,
                    DescriptionId = DescriptionId,
                    Difficulty = Difficulty,
                    IconId = IconId,
                    MiniGameType = CombatMiniGameType,
                    MembersOnly = true, // gates the win screen's "Members Only Bonus" Coins box
                    Launch = true,
                    Objectives =
                    [
                        new EncounterObjective
                        {
                            ObjectiveId = GoalBanishSpirits, NameId = GoalBanishSpiritsNameId,
                            DescriptionId = GoalBanishSpiritsNameId,
                            Status = 1, Count = 0, Total = 1, Unknown8 = 0,
                        },
                    ],
                    PreviewRewards = FrostfangArenaZone.GetPrizePreviewFor(player),
                    PreviewCoins = FrostfangArenaZone.PrizeCoins,
                    PreviewXp = FrostfangArenaZone.PrizeXp,
                    RewardXp = EncounterXp,
                    MemberCoins = FrostfangArenaZone.PrizeCoins,
                    ProfileType = FrostfangArenaZone.CombatProfileType,
                    ActivityId = EncounterId,
                };

                EncounterPacketPlayerEnter MakeEnter(ulong guid) => new()
                {
                    EncounterId = EncounterId,
                    InstanceId = EncounterInstanceId,
                    PlayerGuid = guid,
                };

                UiObjectiveAddPacket BanishRow() => new()
                {
                    ObjectiveId = GoalBanishSpirits,
                    NameId = GoalBanishSpiritsNameId,
                };

                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit));
                player.SendTunneled(new ObjectiveActivatePacket { ObjectiveId = GoalBanishSpirits, Total = 1 });
                player.SendTunneled(BanishRow());
                player.SendTunneled(MakeLaunch());
                player.SendTunneled(MakeEnter(0));
                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit));
                player.SendTunneled(MakeLaunch());
                player.SendTunneled(BanishRow());
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

                _logger.LogInformation("Spirit arena: entry sequence delivered to {name} (run {run}).",
                    player.Name, run);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spirit arena: delayed encounter-state delivery failed.");
            }
        });
    }

    // ── Spawning ─────────────────────────────────────────────────────────────────────────────────────

    private Npc? CreateSpirit(Player player, Vector4 pos, int spawnFx)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        // The Frostfang pack-wolf recipe (no overhead plates, red minimap dot, clickable attack
        // target); the model/name are the world spirits' own.
        npc.ModelId = SpiritModelId;
        // NAMELESS plate so the HEALTH BAR renders (the bar is a nameplate element — a hidden plate meant no
        // bar, only a flash-on-hit = "health bars sometimes pop up, sometimes not").
        npc.NameId = 0;
        npc.Name = null;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = true;
        npc.Scale = 1f;
        npc.Disposition = 0;             // hostile
        npc.ActiveProfile = SpiritActiveProfile;
        npc.CompositeEffectId = spawnFx; // 46 = materialize poof (tombstone spawns); 0 pre-spawned
        npc.MaxHealth = SpiritHealth;
        npc.Health = SpiritHealth;
        // A combat target, NOT an NPC: no "Press X to talk" prompt (spirits have no InteractAction — the
        // prompt was dead UI that just made enemies look clickable). Attackable via the swords cursor.
        npc.IsInteractable = false;
        npc.InteractRange = 100;
        npc.Visible = true;
        npc.CursorId = 11;               // crossed-swords attack cursor

        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = WolfMovementTypePhysics;
        npc.Speed = 0f;
        npc.RiderGuid = ulong.MaxValue;

        npc.UpdatePosition(pos, Quaternion.Identity);

        // Push to EVERY party member so all see the spirit spawn (solo = one recipient = old behavior).
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

    private Npc? CreateTombstone(Player player, int modelId, Vector3 pos)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        npc.ModelId = modelId;           // bs_gravestone variant
        npc.NameId = TombstoneNameId;    // "Tombstone"
        npc.Name = null;
        npc.HideNamePlate = false;       // named + health-barred: it must read as a destroyable
        npc.ShowHealthBar = true;
        npc.Scale = 1f;
        npc.Disposition = 0;             // hostile = attackable
        npc.ActiveProfile = 1;           // non-default -> red name resolve (the quest-hostile recipe)
        npc.MaxHealth = TombstoneHealth;
        npc.Health = TombstoneHealth;
        // DESTROYABLE, not clickable: the tombstone is broken by ATTACKING it (1500 HP), so it gets the same
        // combat-target recipe — no "Press X to talk" prompt, swords cursor, still damageable.
        npc.IsInteractable = false;
        npc.InteractRange = 100;
        npc.Visible = true;
        npc.CursorId = 11;
        npc.Static = true;               // it's a grave — nothing should try to move it

        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = WolfMovementTypePhysics;
        npc.RiderGuid = ulong.MaxValue;

        npc.UpdatePosition(new Vector4(pos.X, pos.Y, pos.Z, 1f), Quaternion.Identity);

        // Push to EVERY party member so all see + can destroy the tombstone.
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
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = 0 });
            SendNpcHealth(p, npc);
        }

        return npc;
    }

    // Red enemy dots on the minimap — one combat notification per encounter actor. Broadcast
    // so every party member's minimap shows the pack (the player arg kept for
    // call-site symmetry).
    private void SendCombatMinimapMarkers(Player player, IReadOnlyList<ulong> guids)
    {
        if (guids.Count == 0)
            return;

        var badge = new PlayerUpdatePacketAddNotifications();
        foreach (var guid in guids)
            badge.Notifications.Add(new NotificationInfo { Guid = guid, Combat = true, Type = 3, Unknown10 = true });
        Broadcast(badge);
    }

    // ── AI ───────────────────────────────────────────────────────────────────────────────────────────

    private void StartSpiritAi(Player player, int run)
    {
        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Spirit arena: AI loop started (run {run}).", run);

            try
            {

                for (var elapsed = 0; elapsed < 15 * 60 * 1000; elapsed += TickMs)
                {
                    await Task.Delay(TickMs);

                    if (run != _encounterRun)
                    {
                        _logger.LogInformation("Spirit arena: AI loop exit — superseded by a new run (run {run}).", run);
                        return;
                    }

                    // Target the whole GROUP: each spirit picks its nearest live player every tick, so the pack
                    // spreads across the party and re-targets when a player falls. Loop lifetime is the run + any
                    // players remaining (not one anchor leaving).
                    var players = ActivePlayers();
                    if (players.Length == 0)
                    {
                        _logger.LogInformation("Spirit arena: AI loop exit — all players left the zone (run {run}).", run);
                        return;
                    }

                    foreach (var p in players)
                        CollectHearts(p);

                    Npc[] pack;
                    lock (_stateLock)
                        pack = [.. _spirits];

                    if (pack.Length == 0)
                        continue;

                    var now = Environment.TickCount64;
                    var dt = TickMs / 1000f;

                    foreach (var spirit in pack)
                    {
                        if (!spirit.IsAlive)
                            continue;

                        SpiritState? state;
                        lock (_stateLock)
                            _spiritStates.TryGetValue(spirit.Guid, out state);
                        if (state is null)
                            continue;

                        var here = new Vector3(spirit.Position.X, spirit.Position.Y, spirit.Position.Z);

                        // Whole party down: disengage to the spawn post + idle (shared). Otherwise chase the
                        // nearest player still standing (sticky - see NearestLivePlayerSticky).
                        var tgt = NearestLivePlayerSticky(here, players, state);
                        if (tgt is null)
                        {
                            TickMobReturnHome(spirit, state, dt, now);
                            continue;
                        }

                        var target = new Vector3(tgt.Position.X, tgt.Position.Y, tgt.Position.Z);

                        // Pre-spawned mobs engage on APPROACH (or when damaged), then run the shared combat tick.
                        if (!state.Charging)
                        {
                            var dx = target.X - here.X;
                            var dz = target.Z - here.Z;
                            if (dx * dx + dz * dz > SpiritAggroRange * SpiritAggroRange)
                                continue;
                            BeginCharge(tgt, spirit, state);
                        }

                        TickMobCombat(spirit, state, tgt, target, now, dt);
                    }
                }

                _logger.LogInformation("Spirit arena: AI loop exit — 15min safety timeout (run {run}).", run);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spirit arena AI failed (run {run}).", run);
            }
        });
    }

    // The aggro burst (Frostfang live order): ExpectedSpeed low -> high -> charging state.
    private void BeginCharge(Player player, Npc spirit, SpiritState state)
    {
        state.Charging = true;
        state.NextAttackTicks = Environment.TickCount64 + 1000 + _rng.Next(1500);

        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = spirit.Guid, ExpectedSpeed = 3f });
        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = spirit.Guid, ExpectedSpeed = MobChaseSpeed });
        Broadcast(new PlayerUpdatePacketUpdateCharacterState
        {
            Guid = spirit.Guid,
            Status = (CharacterStatus)CharState_Charging,
        });
    }

    // Damaging an idle spirit provokes it even outside aggro range.
    public override void OnNpcDamaged(Player player, Npc npc)
    {
        lock (_stateLock)
        {
            if (_spiritStates.TryGetValue(npc.Guid, out var state) && !state.Charging)
                BeginCharge(player, npc, state);
        }
    }

    // ── Hearts (shared combat pickup — decode lives in FrostfangArenaZone) ──────────────────────────

    private void SpawnHeart(Player player, Vector4 pos)
    {
        if (!TryCreateNpc(out var heart))
            return;

        heart.ModelId = HeartModelId;
        heart.Name = null;
        heart.NameId = 5102381;
        heart.Disposition = 1;
        heart.Scale = 1f;
        heart.IsInteractable = false;
        heart.InteractRange = 0;
        heart.Visible = true;
        heart.MaxHealth = 0;
        heart.ShowHealthBar = false;
        heart.HideNamePlate = true;
        heart.ActiveProfile = 8;
        heart.WalkAnimId = -1;
        heart.RunAnimId = -1;
        heart.StandAnimId = -1;
        heart.MovementType = WolfMovementTypePhysics;
        heart.RiderGuid = ulong.MaxValue;
        heart.UpdatePosition(pos, Quaternion.Identity);

        player.OnAddVisibleNpcs(heart);
        heart.OnAddVisiblePlayers(player);

        lock (_stateLock)
            _hearts.Add(heart);
    }

    private void CollectHearts(Player player)
    {
        List<Npc>? collected = null;
        lock (_stateLock)
        {
            for (var i = _hearts.Count - 1; i >= 0; i--)
            {
                var h = _hearts[i];
                var dx = player.Position.X - h.Position.X;
                var dz = player.Position.Z - h.Position.Z;
                if (dx * dx + dz * dz > HeartPickupRange * HeartPickupRange)
                    continue;
                _hearts.RemoveAt(i);
                (collected ??= []).Add(h);
            }
        }

        if (collected is null)
            return;

        foreach (var h in collected)
        {
            // Real heal (2026-07-27 fix - was cosmetic-only, same bug class reported for potions/power-ups
            // once passive regen was turned off in dungeons/encounters).
            var healedAmount = player.Heal(HeartHeal);
            var maxHpStat = player.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 0;
            player.SendTunneled(new PlayerUpdatePacketHitPointModification
            {
                Guid = player.Guid,
                Guid2 = player.Guid,
                Unknown = true,
                Unknown2 = maxHpStat,
                Unknown3 = player.CurrentHitpoints,
                Unknown4 = healedAmount,
            });

            var tagId = ++_healTagCounter;
            player.SendTunneled(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = player.Guid,
                TagId = tagId,
                CompositeEffectId = HealShowerFxId,
                SourceGuid = h.Guid,
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(HealShowerMs);
                    player.SendTunneled(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                    {
                        Guid = player.Guid,
                        TagId = tagId,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Spirit arena: heal-shower stop failed.");
                }
            });

            h.GracefulRemoval = (false, 0, 5000, HeartPickupFxId, 1000);
            h.Dispose();
        }
    }

    // ── Kills / victory ─────────────────────────────────────────────────────────────────────────────

    public override void OnNpcKilled(Player killer, Npc npc)
    {
        bool wasTombstone;
        bool allClear;

        lock (_stateLock)
        {
            if (_tombstones.Remove(npc))
            {
                wasTombstone = true;
            }
            else if (_spirits.Remove(npc))
            {
                _spiritStates.Remove(npc.Guid);
                _killedSpirits++;
                wasTombstone = false;
            }
            else
            {
                return; // not an encounter NPC
            }

            allClear = !_won && _tombstones.Count == 0 && _spirits.Count == 0;
        }

        Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } });
        var deathPos = npc.Position;

        if (wasTombstone)
        {
            // "The bones crumble and a tormented spirit materializes!" (client string 139366): the
            // grave breaks apart (no death clip — it's a prop) and an extra spirit poofs in on the
            // spot, already provoked. (TODO: find the packet that shows 139366 as an on-screen
            // announce — the materialize poof carries the beat visually for now.)
            npc.GracefulRemoval = (false, 0, 0, DeathPoofFxId, 1000);
            npc.Dispose();

            if (!_won)
            {
                var spirit = CreateSpirit(killer, deathPos, SpawnPoofFxId);
                if (spirit is not null)
                {
                    lock (_stateLock)
                    {
                        _spirits.Add(spirit);
                        var state = new SpiritState { SlotAngle = (float)(_rng.NextDouble() * Math.Tau), Home = spirit.Position };
                        _spiritStates[spirit.Guid] = state;
                        BeginCharge(killer, spirit, state);
                        allClear = false; // the materialized spirit keeps the fight alive
                    }
                    SendCombatMinimapMarkers(killer, [spirit.Guid]);
                    _logger.LogInformation("Spirit arena: tombstone destroyed -> a spirit materializes ({left} tombs left).",
                        _tombstones.Count);
                }
            }
        }
        else
        {
            // Spirits die with the standard death flow: clip + poof.
            npc.GracefulRemoval = (true, SpiritDeathHoldMs, 0, DeathPoofFxId, 1000);
            npc.Dispose();

            // Folded into the shared 5-kind roll (CombatEncounterZone.TryDropPowerup) instead of a
            // heart-only one - see the identical change in FrostfangArenaZone for why.
            TryDropPowerup(deathPos);
            killer.AwardXp(PerKillXp);
        }

        if (allClear)
        {
            // Boss coin drop (ported from EncounterArenaZone.GrantKillCoins, 2026-07-26) - this dungeon has
            // no distinct boss-tier spirit (every tormented spirit is functionally identical), so the
            // final kill that clears the encounter stands in for it, same as the Alpha-kill-triggers-win
            // pattern in FrostfangArenaZone.
            GrantKillCoins(killer);
            WinEncounter(killer, deathPos);
        }
    }

    private const int BossCoinsMin = 3;
    private const int BossCoinsMax = 12;

    private void GrantKillCoins(Player killer)
    {
        var coins = _rng.Next(BossCoinsMin, BossCoinsMax + 1);

        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == Sanctuary.Core.Helpers.GuidHelper.GetPlayerId(killer.Guid));
        if (dbCharacter is null)
            return;

        dbCharacter.Coins += coins;
        dbContext.SaveChanges();
        killer.Coins = dbCharacter.Coins;

        killer.SendTunneled(new ClientUpdatePacketCoinCount { Coins = killer.Coins });
        killer.SendTunneled(new RewardBundlePacket { RewardBundle = { Coins = coins, Trailing = 957 } });
        killer.SendTunneled(new ChatPacketDebugChat
        {
            Message = $"<font color='#0000FF'>You receive {coins} coins.</font>",
            PrintToChat = true,
        });
    }

    // The win moment — the Frostfang burst minus the alpha theater: parting drops, goal
    // complete, XP + quest credit, loot wheel + score, exit door. NO auto-return.
    private void WinEncounter(Player player, Vector4 lastKillPos)
    {
        lock (_stateLock)
            _won = true;

        // Parting drops at the final kill's spot (heart + coin pop — the shared victory beat).
        SpawnHeart(player, lastKillPos);
        SpawnCoinPop(player, lastKillPos);

        // ★ CO-OP: award the win to EVERY party member (each gets their own goal complete, XP, quest
        // credit, loot-wheel prize, and score). For a solo player this loops once.
        var enemies = _killedSpirits;
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
            member.SendTunneled(new ObjectiveCompletePacket { ObjectiveId = GoalBanishSpirits });
            member.SendTunneled(new UiObjectiveCompletePacket { ObjectiveId = GoalBanishSpirits });

            // Grant XP now; hold the banner for the wheel-stop moment so it lands in ONE combined popup
            // with the coins/item (see BaseMiniGamePacketHandler.HandleLootWheelStopped).
            member.AwardXp(EncounterXp);
            member.PendingWheelXp = EncounterXp;

            // Credit any quest whose active goal is "win THIS encounter" — e.g. Ninja: That's the Spirit.
            _questManager.OnEncounterComplete(member, EncounterId);

            // Loot wheel — each member spins their OWN prize (server picks it; the spin is theater).
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

        _logger.LogInformation("Spirit arena: encounter WON — wheel armed, exit door out ({kills} spirits).", enemies);
    }

    private void SpawnCoinPop(Player player, Vector4 pos)
    {
        if (!TryCreateNpc(out var coins))
            return;

        coins.ModelId = CoinsModelId;
        coins.NameId = CoinsNameId;
        coins.Name = null;
        coins.Disposition = 1;
        coins.Scale = 1f;
        coins.IsInteractable = false;
        coins.InteractRange = 0;
        coins.Visible = true;
        coins.MaxHealth = 0;
        coins.HideNamePlate = true;
        coins.ActiveProfile = 28;
        coins.WalkAnimId = -1;
        coins.RunAnimId = -1;
        coins.StandAnimId = -1;
        coins.MovementType = WolfMovementTypePhysics;
        coins.RiderGuid = ulong.MaxValue;
        coins.UpdatePosition(new Vector4(pos.X, pos.Y + 1.5f, pos.Z, 1f), Quaternion.Identity);

        var angle = (float)(_rng.NextDouble() * Math.Tau);
        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(coins);
            coins.OnAddVisiblePlayers(p);
            p.SendTunneled(new PlayerUpdatePacketKnockback
            {
                Guid = coins.Guid,
                Position = coins.Position,
                Direction = new Vector4(MathF.Sin(angle), 0f, MathF.Cos(angle), 0f),
                Magnitude = CoinsKnockMagnitude,
            });
            p.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = coins.Guid,
                CompositeEffectId = CoinsPopFxId,
                Position = coins.Position,
            });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150);
                coins.GracefulRemoval = (false, 0, 0, 0, 1000);
                coins.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spirit arena: coin-pop removal failed.");
            }
        });
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
        door.MovementType = WolfMovementTypePhysics;
        door.RiderGuid = ulong.MaxValue;
        // The door spawns at the win, well after ground adoption — use the measured height.
        door.UpdatePosition(new Vector4(DoorSpawn.X, _groundY, DoorSpawn.Z, 1f), Quaternion.Identity);

        var badge = new PlayerUpdatePacketAddNotifications();
        badge.Notifications.Add(new NotificationInfo
        {
            Guid = door.Guid,
            Combat = false,
            Type = DoorBadgeType,
            Unknown3 = DoorBadgeUnknown3,
            ImageId = DoorMinimapImageId,
            DescriptionId = 0,
            NameId = DoorNameId,
            SubTextId = -1,
            Unknown8 = true,
            CompositeEffectId = 0,
            Unknown10 = true
        });

        // CO-OP: the door must be visible + clickable to EVERY party member so each can leave.
        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(door);
            door.OnAddVisiblePlayers(p);

            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = door.Guid, Disposition = 1 });
            // NO vitals for the door (it renders an overhead bar regardless of value — Frostfang finding).
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

    // Knockout / fail / revive lifecycle lives in CombatEncounterZone — supply the encounter id + log label.
    protected override int FailEncounterId => EncounterId;
    protected override int FailInstanceId => EncounterInstanceId;
    protected override string EncounterLogName => "Spirit arena";
    protected override IResourceManager ResourceManagerForPowerups => _resourceManager;

    // A bespoke single-arena fight, not a DungeonCatalog "dungeon" - real source (legacy.fanbyte.com/wiki/
    // Combat_(FR)): "Wandering battle instances are allowed 10 knockouts" (vs. 15 for dungeons - see
    // CombatEncounterZone.KnockoutLimit's own comment for the dungeon default this overrides).
    protected override int KnockoutLimit => 10;

    protected override void ReturnHome(Player player, bool immediate)
    {
        if (player.Zone != this)
            return;

        bool won;
        lock (_stateLock)
            won = _won;

        EndEncounterForPlayer(player, won);

        var home = _zoneManager.StartingZone;

        // Back to where the player clicked the entry spirit (the Blackspore graveyard), not the
        // world spawn — the GO! handler stashes the pre-teleport position.
        var returnPos = player.EncounterReturnPosition ?? home.SpawnPosition;
        player.EncounterReturnPosition = null;

        player.TeleportToZone(home, returnPos, home.SpawnRotation, sky: null, geometryId: 0);
    }


    #endregion
}
