using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Pathfinding;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Scripting;
using Sanctuary.UdpLibrary;

namespace Sanctuary.Game.Zones;

[DebuggerDisplay("{Name} ({Id})")]
public abstract class BaseZone : IZone, IDisposable
{
    protected readonly ILogger _logger;
    protected readonly IResourceManager _resourceManager;
    private readonly BaseZoneDefinition _zoneDefinition;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private const int VisibleTileRadius = 2;
    private readonly Dictionary<int, ZoneTile> _tiles;

    private static ulong _uniqueGuid = 100_000_000_000u;

    private readonly ConcurrentDictionary<ulong, Npc> _npcs = new();
    private readonly ConcurrentDictionary<ulong, Player> _players = new();
    private readonly ConcurrentDictionary<ulong, IEntity> _entities = new();

    private const int FrameRate = 10;
    private const float TickRate = 1000f / FrameRate;

    public float TickDeltaSeconds => 1f / FrameRate;

    private readonly PeriodicTimer _updateEveryTickTimer = new(TimeSpan.FromMilliseconds(TickRate));
    private readonly PeriodicTimer _updateEverySecondTimer = new(TimeSpan.FromSeconds(1));

    public int Id { get; init; }
    public string Name => _zoneDefinition.Name;
    public ILogger Logger => _logger;

    public Vector4 SpawnPosition => _zoneDefinition.SpawnPosition;
    public Quaternion SpawnRotation => _zoneDefinition.SpawnRotation;

    // ── Navigation (shared) ───────────────────────────────────────────────────────────────────────────
    // Every consumer - "Take Me There" routing, dungeon mob chase, and the overworld enemy AI - goes
    // through TryFindPath/IsLineWalkable below, so a given zone routes one way for all of them.
    //
    // There are TWO routing sources, in preference order:
    //
    //  1. Pathfinder - bi-directional A* over the zone's native ".map" waypoint graph (see
    //     MapGraphLoader). This is REAL shipped navigation data and is by far the better source:
    //     FabledRealms.map is 2019 nodes in a SINGLE fully-connected component, so any two points on it
    //     can route to each other.
    //  2. NavGraph - the hand-rolled WaypointGraph fallback for zones with no .map file (every dungeon
    //     today). Seeded from sampled walkable ground / curated points, so it's inherently patchier.
    //
    // NavObstacles is orthogonal to both: real .gcnk prop + .gzne wall geometry, used for the cheap
    // "is the straight line already clear?" test so we only pay for A* when something is actually in the
    // way. The .map graph carries no obstacle information of its own (its own header notes it can't tell
    // whether something blocks the hop between a position and its nearest node), so keeping this is what
    // stops a mover from walking into a prop that sits between it and the graph.
    //
    // All three stay null for a zone with no data, and every consumer treats that as "straight lines are
    // fine" rather than an error.
    public Pathfinder<MapNode>? Pathfinder { get; private set; }
    public ObstacleMap? NavObstacles { get; protected set; }
    public WaypointGraph? NavGraph { get; protected set; }

    // REAL per-model collision geometry (see MeshObstacleMap), when the zone has built it. Preferred over
    // NavObstacles for line-of-walk tests because NavObstacles only ever approximates each prop as a
    // name-matched circle - measured on Bixie Hive, 15.6% of chase lines cross a wall that the circle
    // approximation misses completely, which is precisely how a mob ends up walking through one.
    //
    // Only dungeon-sized worlds build this: it costs ~15-80ms and a few MB there, but the overworld has
    // 39k placements / 4.2M triangles, where both would be prohibitive - it stays on NavObstacles.
    public MeshObstacleMap? NavMesh { get; protected set; }

    // How close a graph node has to be to the real start/destination before we skip anchoring that end
    // (see TryFindPath) - just far enough to avoid emitting a duplicate point on top of a node.
    private const float PathAnchorTolerance = 1f;

    // True when the straight segment a->b doesn't cross real geometry. Prefers the real collision mesh
    // and falls back to the circle approximation. No data at all => "clear" (unchanged straight-line
    // behavior), never a false "blocked" that would freeze a mover.
    public bool IsLineWalkable(Vector4 a, Vector4 b)
    {
        if (NavMesh is not null)
            return NavMesh.IsLineWalkable(a, b);

        return NavObstacles is null || NavObstacles.IsLineWalkable(a, b);
    }

    // Real route between two points, or null when nothing can route them - callers fall back to a
    // straight line. Prefers the native .map graph and only drops to the hand-rolled WaypointGraph when
    // this zone has no .map.
    public List<Vector4>? TryFindPath(Vector4 start, Vector4 destination)
    {
        if (Pathfinder is not null)
        {
            var nodes = Pathfinder.FindPath(
                new Vector3(start.X, start.Y, start.Z),
                new Vector3(destination.X, destination.Y, destination.Z));

            // An empty list means "no route" (see Pathfinder.FindPath); surface that as null so callers
            // treat it the same as a missing graph rather than as a zero-length path.
            if (nodes.Count == 0)
                return null;

            var path = new List<Vector4>(nodes.Count + 2);

            // Anchor the real start. The route begins at the graph node NEAREST the start, which can sit
            // behind or beside the mover - for "Take Me There" that would draw the green trail starting
            // off to one side of the player instead of at their feet.
            if (Vector3.Distance(new Vector3(start.X, start.Y, start.Z), nodes[0].Position) > PathAnchorTolerance)
                path.Add(start);

            foreach (var node in nodes)
                path.Add(new Vector4(node.Position, 1f));

            // Anchor the real destination, for the same reason at the far end: without it the path stops
            // at the nearest node to the target rather than AT the target, so an auto-walk parks the
            // player short of the NPC they asked to be taken to.
            if (Vector3.Distance(new Vector3(destination.X, destination.Y, destination.Z), nodes[^1].Position) > PathAnchorTolerance)
                path.Add(destination);

            return path;
        }

        return NavGraph?.FindPath(start, destination);
    }

    public IEnumerable<Npc> Npcs => _npcs.Values;
    public IEnumerable<Player> Players => _players.Values;

    private readonly IScriptManager _scriptManager;
    private ScriptContext? _scriptContext;

    protected BaseZone(BaseZoneDefinition zoneDefinition, IServiceProvider serviceProvider)
    {
        _zoneDefinition = zoneDefinition;
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();

        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        _logger = loggerFactory.CreateLogger($"Zone {Name} ({Id})");

        _scriptManager = serviceProvider.GetRequiredService<IScriptManager>();

        _scriptContext = _scriptManager.GetContextForZone(this);

        _tiles = GenerateTiles();

        foreach (var tile in _tiles)
        {
            ArgumentNullException.ThrowIfNull(tile.Value.Entities);
            ArgumentNullException.ThrowIfNull(tile.Value.VisibleTiles);
        }

        Task.Factory.StartNew(UpdateEveryTickAsync, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task.Factory.StartNew(UpdateEverySecondAsync, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Native ".map" waypoint graph for this zone, if one shipped for it (keyed by zone name, so
        // "FabledRealms" <- Resources/Maps/FabledRealms.map). Zones without one fall back to NavGraph.
        if (_resourceManager.Maps.TryGetValue(Name, out var mapGraph))
        {
            Pathfinder = new Pathfinder<MapNode>(mapGraph.Nodes, _logger);
            _logger.LogInformation("Using native .map navigation graph ({nodes} nodes).", mapGraph.Nodes.Count);
        }
    }

    #region Events

    // BLOCKS until the script's onStart finishes (CallFunctionAsync never throws — script errors are
    // logged and swallowed, not propagated). This must complete before the zone is usable: a zone's
    // script is responsible for its NPC roster (see StartingZone.TrySpawnNpc), and callers like the
    // quest system, vendor lookups, and the encounter-entry placer all assume those NPCs already exist.
    public virtual void OnStart()
    {
        _scriptContext?.CallFunctionAsync("onStart", this).AsTask().GetAwaiter().GetResult();
    }

    // Dev/live-reload: re-reads the zone's .lua file from disk and re-runs ITS onStart function only —
    // deliberately NOT the full OnStart() override chain, since zone subclasses can layer one-time-only
    // setup (e.g. StartingZone.OnStart also places dungeon entrances/encounter entries with AUTO-assigned
    // guids, which would duplicate on every re-run). A script's onStart is safe to call repeatedly ONLY
    // if every spawn call in it uses an explicit guid (spawnNpcWithGuid) — those no-op on a guid that
    // already exists. A script using auto-guid spawnNpc(...) calls WILL duplicate NPCs on reload.
    // Returns false (keeping the previous script live) if the file is missing or fails to load.
    public bool ReloadScript()
    {
        var newContext = _scriptManager.GetContextForZone(this);
        if (newContext is null)
        {
            _logger.LogWarning("Script reload failed for zone '{Name}': keeping the previously loaded script.", Name);
            return false;
        }

        _scriptContext = newContext;
        _scriptContext.CallFunctionAsync("onStart", this).AsTask().GetAwaiter().GetResult();
        return true;
    }

    public virtual void OnClientIsReady(Player player)
    {
    }

    public virtual void OnClientFinishedLoading(Player player)
    {
    }

    public virtual void RefreshPlayerCustomizations(Player player)
    {
    }

    // COMBAT: an NPC in this zone was killed — zones override to decide the consequence.
    public virtual void OnNpcKilled(Player killer, Npc npc)
    {
    }

    // COMBAT: an NPC took a non-fatal hit — zones override to react to HP thresholds.
    public virtual void OnNpcDamaged(Player attacker, Npc npc)
    {
    }

    // COMBAT: generic "summon N clone NPCs that fight alongside the caster, then despawn" engine — see
    // CombatCloneConfig's header comment for why this lives here (zone-agnostic) instead of on one specific
    // zone. Each clone independently (re)acquires the nearest live hostile within config.LeashRange of the
    // SUMMONER every tick, chases it, and attacks on a cooldown once in range; on kill/hit it credits the
    // SUMMONER via OnNpcKilled/OnNpcDamaged, same as any other player-dealt damage.
    public void SummonCombatClones(Player summoner, int count, int lifetimeSeconds, Sanctuary.Game.Combat.CombatCloneConfig config)
    {
        // small arc around the caster
        (float dx, float dz)[] offsets = [(-2f, -2f), (2f, -2f), (0f, -3f), (-3f, 1f), (3f, 1f)];

        var clones = new List<Npc>(count);

        for (var i = 0; i < count; i++)
        {
            if (!TryCreateNpc(out var clone))
                break;

            var (dx, dz) = offsets[i % offsets.Length];
            var pos = new Vector4(summoner.Position.X + dx, summoner.Position.Y, summoner.Position.Z + dz, summoner.Position.W);

            clone.ModelId = config.ModelId;
            clone.Name = config.Name;
            clone.NameId = 0;
            clone.HideNamePlate = false;
            clone.Disposition = 2; // Ally
            clone.Scale = 1f;
            clone.IsInteractable = false;
            clone.CursorId = 0;
            clone.CompositeEffectId = 0;
            clone.RunAnimId = config.RunAnim;
            clone.WalkAnimId = config.WalkAnim;
            clone.StandAnimId = config.StandAnim;
            clone.Visible = true;
            clone.UpdatePosition(pos, summoner.Rotation);

            summoner.OnAddVisibleNpcs(clone);
            clone.OnAddVisiblePlayers(summoner); // track the caster so Dispose() removes it from their client

            summoner.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = clone.Guid,
                CompositeEffectId = config.SpawnPoofFx,
                Position = pos,
            });

            clones.Add(clone);
        }

        if (clones.Count == 0)
            return;

        _logger.LogInformation("SummonCombatClones: summoned {n} '{name}' clones for {sec}s.",
            clones.Count, config.Name, lifetimeSeconds);

        // despawn after the lifetime (off-thread, mirrors the damage-resolve pattern)
        _ = Task.Run(async () =>
        {
            try
            {
                var totalMs = lifetimeSeconds * 1000;
                var nextAttackMs = new int[clones.Count];
                var currentTargets = new Npc?[clones.Count];
                var leashSq = config.LeashRange * config.LeashRange;

                for (var elapsed = 0; elapsed < totalMs; elapsed += config.TickMs)
                {
                    await Task.Delay(config.TickMs);

                    for (var i = 0; i < clones.Count; i++)
                    {
                        var clone = clones[i];

                        // (Re)acquire target: nearest live hostile within leash range of the SUMMONER (not the
                        // clone) - same nearby-hostile pattern SplashShockPaddles uses. Drops a target that
                        // died OR wandered out of leash range, so clones don't chase forever across the zone.
                        var target = currentTargets[i];
                        if (target is not null && (!target.IsAlive || DistanceSq2D(summoner.Position, target.Position) > leashSq))
                            target = null;

                        if (target is null)
                        {
                            target = Npcs
                                .Where(n => n.IsHostile && n.IsDamageable && n.IsAlive)
                                .Select(n => (npc: n, d2: DistanceSq2D(summoner.Position, n.Position)))
                                .Where(t => t.d2 <= leashSq)
                                .OrderBy(t => t.d2)
                                .Select(t => t.npc)
                                .FirstOrDefault();
                            currentTargets[i] = target;
                        }

                        if (target is null)
                            continue; // no hostile nearby - hold position

                        var here = new Vector3(clone.Position.X, clone.Position.Y, clone.Position.Z);
                        var targetPos = new Vector3(target.Position.X, target.Position.Y, target.Position.Z);
                        var toTarget = targetPos - here;
                        var dist = toTarget.Length();

                        var yaw = (float)Math.Atan2(toTarget.X, toTarget.Z);
                        var rot = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);

                        if (dist > config.AttackRange)
                        {
                            var step = Math.Min(config.MoveSpeed * (config.TickMs / 1000f), dist - config.AttackRange);
                            var dir = toTarget / dist;
                            var np = here + dir * step;
                            var newPos = new Vector4(np.X, np.Y, np.Z, clone.Position.W);

                            clone.UpdatePosition(newPos, rot);
                            summoner.SendTunneled(new PlayerUpdatePacketUpdatePosition
                            {
                                Guid = clone.Guid, Position = newPos, Rotation = rot, State = 1, Unknown = 0,
                            });
                        }
                        else
                        {
                            clone.UpdatePosition(clone.Position, rot);
                            summoner.SendTunneled(new PlayerUpdatePacketUpdatePosition
                            {
                                Guid = clone.Guid, Position = clone.Position, Rotation = rot, State = 0, Unknown = 0,
                            });

                            if (elapsed >= nextAttackMs[i])
                            {
                                nextAttackMs[i] = elapsed + config.AttackCooldownMs;

                                summoner.SendTunneled(new AbilityPacketStartCasting
                                {
                                    Unknown = clone.Guid, Unknown2 = target.Guid, CompositeEffectId = 0,
                                    Animation = config.AttackAnim, AbilityId = 0, ActionTime = 0.3f, HasActionProgress = false,
                                });

                                var killed = target.ApplyDamage(config.AttackDamage);
                                summoner.SendTunneled(new CombatPacketAttackProcessed
                                {
                                    AttackerGuid = clone.Guid,
                                    TargetGuid = target.Guid,
                                    Damage = config.AttackDamage,
                                    MaxHealth = target.MaxHealth,
                                    CompositeEffectId = config.HitFx,
                                    CurrentHealth = target.Health,
                                });

                                if (killed)
                                {
                                    OnNpcKilled(summoner, target);
                                    currentTargets[i] = null; // pick a new target next tick
                                }
                                else
                                {
                                    OnNpcDamaged(summoner, target);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SummonCombatClones AI failed.");
            }
            finally
            {
                // poof out + remove every clone
                foreach (var clone in clones)
                {
                    summoner.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
                    {
                        Guid = clone.Guid,
                        CompositeEffectId = config.SpawnPoofFx,
                        Position = clone.Position,
                    });

                    clone.Dispose(); // RemovePlayer to the caster + clears zone tile + zone registration
                }
            }
        });
    }

    private static float DistanceSq2D(Vector4 a, Vector4 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    // The "Revive here" coin cost sent on the overworld respawn window, RAW (client shows it as
    // value/1000, so 100000 -> "100 coins").
    protected const int ReviveHereCostRaw = 100000;

    // Fallback auto-revive delay. OVERWORLD = long: the player is expected to press a button on
    // the pay/safe respawn window; this only backstops someone who never does. Combat instances override
    // it short (they auto-revive via the knockout-counter flow, no window).
    protected virtual int ReviveCooldownMs => 20000;

    // DEATH: the player's HP just hit 0. OVERWORLD behavior = pop the client's pay/safe respawn
    // window ("Revive here: 100 coins" / "Revive at safe location: Free"); the Revive buttons (sub122)
    // drive revival. Combat instances override for the knockout-counter / fail flow (no window).
    public virtual void OnPlayerKnockedOut(Player player)
    {
        // The client's respawn window (DisplayRespawn) only renders the pay/safe buttons while it's in a
        // combat/encounter state; otherwise sub125 shows only the knockout banner/counter. Put the player
        // into the world-combat state first so the full window (with buttons) appears out in the overworld.
        player.SendTunneled(new Sanctuary.Packet.EncounterOverworldCombatPacket { Unknown3 = true });
        player.SendTunneled(new Sanctuary.Packet.EncounterPacketIsFighting { InWorldCombat = true });
        player.SendTunneled(new Sanctuary.Packet.EncounterShowRespawnWindowPacket(0, 0, reviveHereCostRaw: ReviveHereCostRaw));
        ScheduleAutoRevive(player);
    }

    // Per-player knockout generation. Each knockout bumps it; the auto-revive task captures the value it was
    // scheduled under and only fires if it's STILL current. Without this, a stale auto-revive from an earlier
    // knockout would fire during a LATER knockout and revive the player instantly, skipping the countdown.
    private readonly ConcurrentDictionary<ulong, int> _reviveGeneration = new();

    // How long the "TRY AGAIN!" fail card sits before the encounter tears down and teleports the
    // player home — otherwise the state-remove + teleport wipe the card instantly. It's a timer because the
    // client never reports the card being closed (ClosedMinigameEndScreen never arrives for these).
    protected const int FailCardHoldMs = 4000;

    // Show the persistent "TRY AGAIN!" fail end-screen. Three things are needed and were missing:
    // (1) clear the knockdown UI — Player.Knockout leaves the client in IsKnockedOut|IsRooted, which wipes
    // the card instantly; (2) set Won=0 via GameOver so the end screen reads as the failure variant;
    // (3) send the SCORE end-screen (op39/47) — that's the actual persistent card the WIN shows while the
    // player stands, whereas GameOver alone is just a state flag. Server-side IsDead is intentionally left
    // set so the mobs don't re-engage; the real revive happens on the trip home.
    protected static void SendFailEndScreen(Player player)
    {
        // Stand the player up on the client (out of the knockdown/rooted state) so the card isn't fought off.
        player.SendTunneledToVisible(new PlayerUpdatePacketUpdateCharacterState
        {
            Guid = player.Guid,
            Status = CharacterStatus.None,
        }, sendToSelf: true);

        player.SendTunneled(new MiniGameGameOverPacket(won: false)); // Won=0 -> failure variant
        var score = new MiniGameGameEndScorePacket();
        score.Rows.Add(new MiniGameScoreRow { Name = "scoreTotalScore", Order = 4, Points = 0 });
        player.SendTunneled(score); // the persistent end-screen card
    }

    // Revive the player automatically once the knockout cooldown elapses (as long as they're
    // still down, still in this zone, and no NEWER knockout has occurred). This drives the client back to
    // life in sync with its own revive-cooldown countdown — the FALLBACK for someone who never presses the
    // Revive button.
    protected void ScheduleAutoRevive(Player player)
    {
        int generation = _reviveGeneration.AddOrUpdate(player.Guid, 1, (_, g) => g + 1);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(ReviveCooldownMs);
                // Only revive if this is STILL the same knockout — a later knockout bumps the generation, so a
                // stale task from an earlier one won't revive the player mid-countdown ("instant revive" bug).
                if (player.IsDead && player.Zone == this
                    && _reviveGeneration.TryGetValue(player.Guid, out var current) && current == generation)
                    OnPlayerRespawn(player);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Auto-revive failed.");
            }
        });
    }

    // DEATH: revive the player. Base (overworld) behavior = revive where they fell with full HP.
    // Dungeons override to revive at the dungeon spawn.
    public virtual void OnPlayerRespawn(Player player)
    {
        player.Respawn();
    }

    #endregion

    #region Combat helpers

    // COMBAT: tell the client this NPC has a cursor (attack/talk) so it is selectable as a target.
    public void SendNpcRelevance(Player player, Npc npc)
    {
        if (npc.CursorId == 0)
            return;

        var relevance = new PlayerUpdatePacketNpcRelevance();

        relevance.Entries.Add(new PlayerUpdatePacketNpcRelevance.Entry
        {
            Guid = npc.Guid,
            Unknown = true,        // "has cursor" (provisional)
            CursorId = npc.CursorId,
        });

        player.SendTunneled(relevance);
    }

    // COMBAT: push an NPC's current/max health to a player so its nameplate health bar renders.
    public void SendNpcHealth(Player player, Npc npc)
    {
        if (!npc.IsDamageable)
            return;

        // Pushing health stats is what MAKES the client draw a bar, so an npc that opts out of one must not
        // receive them from ANY caller - guarding only at the call sites let the bar back in through
        // whichever path was overlooked.
        if (!npc.ShowHealthBar)
            return;

        var updateStat = new ClientUpdatePacketUpdateStat { Guid = npc.Guid };
        updateStat.Stats.Add(new CharacterStat(CharacterStatId.MaxHealth, npc.MaxHealth));
        player.SendTunneled(updateStat);

        var updateHitpoints = new PlayerUpdatePacketUpdateHitpoints
        {
            Guid = npc.Guid,
            Hitpoints = npc.Health,
            MaxHitpoints = npc.MaxHealth
        };
        player.SendTunneled(updateHitpoints);
    }

    #endregion

    #region Entities

    public bool TryGetNpc(ulong guid, [MaybeNullWhen(false)] out Npc npc)
    {
        return _npcs.TryGetValue(guid, out npc);
    }

    public bool TryGetPlayer(ulong guid, [MaybeNullWhen(false)] out Player player)
    {
        return _players.TryGetValue(guid, out player);
    }

    public bool TryGetEntity(ulong guid, [MaybeNullWhen(false)] out IEntity entity)
    {
        return _entities.TryGetValue(guid, out entity);
    }

    public bool TryAddMount(Mount mount)
    {
        return _npcs.TryAdd(mount.Guid, mount) && _entities.TryAdd(mount.Guid, mount);
    }

    public bool TryAddPet(Pet pet)
    {
        return _npcs.TryAdd(pet.Guid, pet) && _entities.TryAdd(pet.Guid, pet);
    }

    public bool TryAddPlayer(Player player)
    {
        return _players.TryAdd(player.Guid, player) && _entities.TryAdd(player.Guid, player);
    }

    public bool TryCreateNpc([MaybeNullWhen(false)] out Npc npc)
    {
        npc = new Npc(this)
        {
            Guid = _uniqueGuid++
        };

        return _npcs.TryAdd(npc.Guid, npc) && _entities.TryAdd(npc.Guid, npc);
    }

    public bool TryCreateNpc(ulong guid, [MaybeNullWhen(false)] out Npc npc)
    {
        npc = new Npc(this)
        {
            Guid = guid
        };

        // Update _uniqueGuid to prevent conflicts
        if (guid >= _uniqueGuid)
            _uniqueGuid = guid + 1;

        return _npcs.TryAdd(npc.Guid, npc) && _entities.TryAdd(npc.Guid, npc);
    }

    #region Scripting

    // Lua-facing spawn API (ScriptableZone.spawnNpc/spawnNpcWithGuid). Looks up the NPC's resource
    // definition for its model/name/texture, but position/heading are supplied by the script, not the
    // definition. This is the base (no-frills) implementation; zones with extra spawn rules (vendors,
    // quests, world enemies — see StartingZone.TrySpawnNpc) override it.
    public virtual bool TrySpawnNpc(int npcId, ulong? npcGuid, float x, float y, float z, float heading)
    {
        if (npcGuid.HasValue && _npcs.ContainsKey(npcGuid.Value))
        {
            _logger.LogWarning("Failed to spawn NPC {NpcId} with GUID {NpcGuid}: GUID already exists.", npcId, npcGuid.Value);
            return false;
        }

        var definition = _resourceManager.Npcs.Values.FirstOrDefault(n => n.Id == npcId);
        if (definition is null)
        {
            _logger.LogWarning("Failed to spawn NPC {NpcId}: No definition found.", npcId);
            return false;
        }

        var created = npcGuid.HasValue
            ? TryCreateNpc(npcGuid.Value, out Npc? npc)
            : TryCreateNpc(out npc);

        if (!created || npc is null)
        {
            _logger.LogWarning("Failed to spawn NPC {NpcId}: Could not create NPC instance.", npcId);
            return false;
        }

        npc.ModelId = definition.ModelId;
        npc.NameId = definition.NameId;
        npc.Name = definition.Name;
        npc.TextureAlias = definition.TextureAlias;
        npc.Static = definition.Static;
        npc.Scale = _resourceManager.Models.TryGetValue(definition.ModelId, out var model) && model.Scale != 0f
            ? model.Scale
            : 1f;
        npc.Visible = true;

        var position = new Vector4(x, y, z, 1f);
        var rotation = new Quaternion(MathF.Sin(heading), 0f, MathF.Cos(heading), 0f);

        npc.UpdatePosition(position, rotation);

        return true;
    }

    // World props (ScriptableZone.spawnGatheringNode / spawnSnowballPile). These only exist in the
    // overworld, so the base zone refuses them rather than half-spawning a prop with no system behind it —
    // a dungeon script calling one is a mistake worth seeing in the log. StartingZone overrides both.
    public virtual bool TrySpawnGatheringNode(int modelId, int itemDefinitionId, string name, float x, float y, float z)
    {
        _logger.LogWarning("Zone \"{zone}\" does not support gathering nodes (model {model}).", Name, modelId);
        return false;
    }

    public virtual bool TrySpawnSnowballPile(float x, float y, float z, float heading)
    {
        _logger.LogWarning("Zone \"{zone}\" does not support snowball piles.", Name);
        return false;
    }

    public virtual bool TrySpawnQuestCollectible(ulong guid, float x, float y, float z)
    {
        _logger.LogWarning("Zone \"{zone}\" does not support quest collectibles (guid {guid}).", Name, guid);
        return false;
    }

    public virtual bool TrySpawnDungeonEntrance(int poiId, float x, float y, float z, float heading)
    {
        _logger.LogWarning("Zone \"{zone}\" does not support dungeon entrances (POI {poi}).", Name, poiId);
        return false;
    }

    // Scratch space for CollectScriptSpawnPoints below — a script reports points into here via
    // zone.addSpawnPoint(x, y, z) while its collector function runs, instead of spawning anything itself.
    private readonly List<Vector4> _scriptSpawnPoints = [];

    public void AddSpawnPoint(float x, float y, float z)
    {
        _scriptSpawnPoints.Add(new Vector4(x, y, z, 1f));
    }

    // A pack marker, not an individual: (x, y, z) is a real captured "there's a group here" point (e.g. a
    // coordinate sheet's "Pack of 10 cray" row), and `count` scatters that many points around it instead of
    // the script having to hand-plot every individual's own coordinate. count<=1 just uses the marker itself.
    //
    // CORRECTED (live feedback): evenly-spaced angles at a fixed radius put every individual on the exact
    // same circle - it read as an artificial "circle formation" rather than a natural pack. Random angle +
    // random radius (sqrt-scaled so density is uniform across the disc, not bunched near the center) gives
    // an organic scatter instead, over a wider area so packs read as more spread out too.
    //
    // CORRECTED AGAIN (live feedback, 2026-07-26): the scatter offset was never checked against real wall/
    // boundary data at all - same root-cause pattern as JitteredWalkablePos and the corner-hug nodes before
    // it (see CombatEncounterZone.BuildMobPathfinding) - so a marker close to a cave wall or the dungeon's
    // edge could scatter individuals straight through the wall or off the playable map. Retry each point
    // against IsScriptSpawnPositionValid (a no-op here on the base zone; CombatEncounterZone overrides it
    // with the real obstacle/boundary check), falling back to the raw candidate if every attempt is blocked
    // rather than dropping the enemy.
    //
    // CORRECTED A THIRD TIME (live feedback, 2026-07-26): checking only the CANDIDATE point still wasn't
    // enough - a large scatter jump (up to 9u for a pack of 10) can leap clean OVER a thin real wall strip
    // in one step without either endpoint ever landing inside the wall's own margin, the same "teleport
    // past a wall" gap CombatEncounterZone.ChaseStep already had to guard against for mob movement (that's
    // exactly what ObstacleMap.IsLineWalkable's sampled-segment check is for). Pass the real marker point
    // through as the "from" side so the validity check can walk the whole jump, not just its landing spot.
    public void AddSpawnArea(float x, float y, float z, int count)
    {
        if (count <= 1)
        {
            _scriptSpawnPoints.Add(new Vector4(x, y, z, 1f));
            return;
        }

        var origin = new Vector4(x, y, z, 1f);
        // CAPPED 2026-07-27, TIGHTENED AGAIN same day (live feedback: "still spawning underground" after
        // the first 6u cap) - every scattered individual already reuses the marker's OWN real Y unchanged
        // (only X/Z are randomized), so this was never about scatter drifting into a DIFFERENT floor tier -
        // it's that real cave floors aren't flat even within one "tier", and with no floor-height data at
        // all (only wall obstacles from .gzne), ANY X/Z offset from the marker risks landing on ground that
        // sits higher than the marker's fixed Y, reading as the mob spawning "into" the terrain from below.
        // Cut the cap hard (6u -> 3u) to keep every individual close enough to the marker's own real,
        // known-good spot that local unevenness is unlikely to matter - trades some pack "spread out"
        // variety for actually landing on real floor, which matters more.
        var maxRadius = MathF.Min(3f, MathF.Max(2f, 2f + count * 0.2f));
        // MIN-SEPARATION added (live feedback: "some enemies are on eachother or too close... give them a
        // bit of space between eachother but keep them close") - the sqrt-scaled random disc placement never
        // checked candidates against EACH OTHER, only against walls, so nothing stopped two individuals from
        // landing almost on top of one another. Reject a candidate that's too close to an already-placed
        // packmate (this SAME call's points only - doesn't touch other packs) and retry; if every attempt
        // this individual gets stays too close, keep the least-crowded one tried rather than dropping the
        // enemy. Small enough that it still fits inside the tight maxRadius above for a full pack.
        const float minSeparation = 1.4f;
        var minSeparationSq = minSeparation * minSeparation;
        var placedThisCall = new List<Vector4>(count);
        for (var i = 0; i < count; i++)
        {
            Vector4 candidate = default;
            var found = false;
            var bestCandidate = origin;
            var bestNearestSq = float.MinValue;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var angle = (float)(Random.Shared.NextDouble() * Math.Tau);
                var r = MathF.Sqrt((float)Random.Shared.NextDouble()) * maxRadius;
                candidate = new Vector4(x + MathF.Sin(angle) * r, y, z + MathF.Cos(angle) * r, 1f);
                if (!IsScriptSpawnPositionValid(origin, candidate))
                    continue;

                var nearestSq = float.MaxValue;
                foreach (var placed in placedThisCall)
                {
                    var dx = candidate.X - placed.X;
                    var dz = candidate.Z - placed.Z;
                    var d2 = dx * dx + dz * dz;
                    if (d2 < nearestSq)
                        nearestSq = d2;
                }
                if (nearestSq > bestNearestSq)
                {
                    bestNearestSq = nearestSq;
                    bestCandidate = candidate;
                }
                if (nearestSq >= minSeparationSq)
                {
                    found = true;
                    break;
                }
            }
            var placed2 = found ? candidate : bestCandidate;
            placedThisCall.Add(placed2);
            _scriptSpawnPoints.Add(placed2);
        }
    }

    // Hook for AddSpawnArea's scatter retry - default "anything goes" (no obstacle/pathfinding data on the
    // base zone). CombatEncounterZone overrides this with a real wall/boundary check once it has built its
    // dungeon's ObstacleMap. `from` is the real captured marker the scatter is jumping away from - the
    // override validates the WHOLE hop, not just where it lands (see the header comment above).
    protected virtual bool IsScriptSpawnPositionValid(Vector4 from, Vector4 pos) => true;

    // Calls a named script function (if the zone has a script AND that function exists) and returns
    // whatever points it reported via zone.addSpawnPoint(...), in call order. Empty if there's no script,
    // the function isn't defined, or it didn't report anything — callers must treat empty as "no scripted
    // data" and fall back to their own placement (see EncounterArenaZone.BuildDungeonSpawns).
    protected List<Vector4> CollectScriptSpawnPoints(string functionName)
    {
        if (_scriptContext is null)
            return [];

        _scriptSpawnPoints.Clear();
        _scriptContext.CallFunctionAsync(functionName, this).AsTask().GetAwaiter().GetResult();
        return [.. _scriptSpawnPoints];
    }

    #endregion

    public bool TryCreateMount(Player rider, MountDefinition definition, [MaybeNullWhen(false)] out Mount mount)
    {
        mount = new Mount(this, rider, definition)
        {
            Guid = _uniqueGuid++
        };

        return _npcs.TryAdd(mount.Guid, mount) && _entities.TryAdd(mount.Guid, mount);
    }

    public bool TryCreatePet(Player owner, Resources.Definitions.PetDefinition definition, [MaybeNullWhen(false)] out Pet pet)
    {
        pet = new Pet(this, owner, definition)
        {
            Guid = _uniqueGuid++
        };

        return _npcs.TryAdd(pet.Guid, pet) && _entities.TryAdd(pet.Guid, pet);
    }

    public bool TryCreateCombatNpc([MaybeNullWhen(false)] out CombatNpc combatNpc)
    {
        combatNpc = new CombatNpc(this)
        {
            Guid = _uniqueGuid++
        };

        return _npcs.TryAdd(combatNpc.Guid, combatNpc) && _entities.TryAdd(combatNpc.Guid, combatNpc);
    }

    public bool TryCreateEncounterEntryNpc([MaybeNullWhen(false)] out EncounterEntryNpc entryNpc)
    {
        entryNpc = new EncounterEntryNpc(this)
        {
            Guid = _uniqueGuid++
        };

        return _npcs.TryAdd(entryNpc.Guid, entryNpc) && _entities.TryAdd(entryNpc.Guid, entryNpc);
    }

    public bool TryCreateProjectileNpc([MaybeNullWhen(false)] out ProjectileNpc projectileNpc)
    {
        projectileNpc = new ProjectileNpc(this)
        {
            Guid = _uniqueGuid++
        };

        return _npcs.TryAdd(projectileNpc.Guid, projectileNpc) && _entities.TryAdd(projectileNpc.Guid, projectileNpc);
    }

    public bool TryCreatePlayer(ulong guid, UdpConnection connection, [MaybeNullWhen(false)] out Player player)
    {
        player = new Player(this, connection, _resourceManager)
        {
            Guid = guid
        };

        return _players.TryAdd(player.Guid, player) && _entities.TryAdd(player.Guid, player);
    }

    public bool TryRemoveNpc(ulong guid)
    {
        return _npcs.TryRemove(guid, out _) && _entities.TryRemove(guid, out _);
    }

    public bool TryRemovePlayer(ulong guid)
    {
        return _players.TryRemove(guid, out _) && _entities.TryRemove(guid, out _);
    }

    #endregion

    #region Zone System

    private Dictionary<int, ZoneTile> GenerateTiles()
    {
        var tiles = new Dictionary<int, ZoneTile>();

        // Generate all tiles
        for (var longitude = _zoneDefinition.StartLongitude; longitude < _zoneDefinition.EndLongitude; longitude++)
        {
            for (var latitude = _zoneDefinition.StartLatitude; latitude < _zoneDefinition.EndLatitude; latitude++)
            {
                var tileHash = ZoneTile.GetHash(longitude, latitude);

                tiles.Add(tileHash, new ZoneTile(longitude, latitude));
            }
        }

        // Calcualte visible tiles
        for (var rootLongitude = _zoneDefinition.StartLongitude; rootLongitude < _zoneDefinition.EndLongitude; rootLongitude++)
        {
            for (var rootLatitude = _zoneDefinition.StartLatitude; rootLatitude < _zoneDefinition.EndLatitude; rootLatitude++)
            {
                var rootTileHash = ZoneTile.GetHash(rootLongitude, rootLatitude);

                var rootTile = tiles[rootTileHash];

                for (var visibleLongitude = rootTile.Longitude - VisibleTileRadius; visibleLongitude <= rootTile.Longitude + VisibleTileRadius; visibleLongitude++)
                {
                    for (var visibleLatitude = rootTile.Latitude - VisibleTileRadius; visibleLatitude <= rootTile.Latitude + VisibleTileRadius; visibleLatitude++)
                    {
                        var visibleTileHash = ZoneTile.GetHash(visibleLongitude, visibleLatitude);

                        if (tiles.TryGetValue(visibleTileHash, out var visibleTile))
                            rootTile.VisibleTiles.Add(visibleTile);
                    }
                }
            }
        }

        return tiles;
    }

    public ZoneTile GetTileFromPosition(Vector4 position)
    {
        var tileLatitude = (int)Math.Floor(position.X / _zoneDefinition.TileSize);
        var tileLongitude = (int)Math.Floor(position.Z / _zoneDefinition.TileSize);

        return GetTileFromCoordinate(tileLongitude, tileLatitude);
    }

    private ZoneTile GetTileFromCoordinate(int longitude, int latitude)
    {
        if (longitude < _zoneDefinition.StartLongitude ||
            longitude >= _zoneDefinition.EndLongitude)
            return ZoneTile.Empty;

        if (latitude < _zoneDefinition.StartLatitude ||
            latitude >= _zoneDefinition.EndLatitude)
            return ZoneTile.Empty;

        var tileHash = ZoneTile.GetHash(longitude, latitude);

        if (!_tiles.TryGetValue(tileHash, out var zoneTile))
            return ZoneTile.Empty;

        return zoneTile;
    }

    public void UpdateEntityZoneTile(IEntity entity, ZoneTile from, ZoneTile to)
    {
        from.Entities.TryRemove(entity.Guid, out _);

        var oldVisibleTiles = from.VisibleTiles;
        var newVisibleTiles = to.VisibleTiles;

        var tilesToAdd = newVisibleTiles.Except(oldVisibleTiles);
        var tilesToRemove = oldVisibleTiles.Except(newVisibleTiles);

        AddEntityToZoneTiles(entity, tilesToAdd);
        RemoveEntityFromZoneTiles(entity, tilesToRemove);

        to.Entities.TryAdd(entity.Guid, entity);
    }

    private void AddEntityToZoneTiles(IEntity entity, IEnumerable<ZoneTile> zoneTiles)
    {
        var npcsToAdd = new List<Npc>();
        var playersToAdd = new List<Player>();

        foreach (var zoneTile in zoneTiles)
        {
            foreach (var zoneTileEntity in zoneTile.Entities)
            {
                if (!zoneTileEntity.Value.Visible || entity == zoneTileEntity.Value)
                    continue;

                switch (zoneTileEntity.Value)
                {
                    case Npc zoneTileNpc:
                        {
                            npcsToAdd.Add(zoneTileNpc);

                            if (entity.Visible)
                            {
                                switch (entity)
                                {
                                    case Npc npc:
                                        break;

                                    case Player player:
                                        zoneTileNpc.OnAddVisiblePlayers(player);
                                        break;
                                }
                            }
                        }
                        break;

                    case Player zoneTilePlayer:
                        {
                            playersToAdd.Add(zoneTilePlayer);

                            if (entity.Visible)
                            {
                                switch (entity)
                                {
                                    case Npc npc:
                                        {
                                            zoneTilePlayer.OnAddVisibleNpcs(npc);
                                            if (npc.ShowCombatBadge)
                                                SendCombatBadge(zoneTilePlayer, npc);
                                        }
                                        break;

                                    case Player player:
                                        zoneTilePlayer.OnAddVisiblePlayers(player);
                                        break;
                                }
                            }
                        }
                        break;
                }
            }
        }

        entity.OnAddVisibleNpcs(npcsToAdd);
        entity.OnAddVisiblePlayers(playersToAdd);

        if (entity is Player arrivingPlayer)
        {
            foreach (var npc in npcsToAdd)
            {
                if (npc.ShowCombatBadge)
                    SendCombatBadge(arrivingPlayer, npc);
            }
        }
    }

    // The red crossed-swords combat-encounter badge (img-24) above an NPC's head + a red minimap dot -
    // RE'd 2026-07-02 for the Frostfang Growler wolf (op35/sub10 AddNotifications, byte-exact vs a real
    // 2014 capture): ImageId 24 in NotificationImages.txt = tint-circle + circle + crossed-swords icon
    // 1345. Type = 3 (the "combat" category, confirmed against every red minimap-dot notification in both
    // 2014 captures) drives the red tint - NOT the NPC's Disposition, so the name itself can stay neutral.
    // Generalized from the Growler's own one-off SendGrowlerBadge into this shared helper so any NPC can
    // opt in via Npc.ShowCombatBadge (dungeon entrances, "Battle Starter" encounter NPCs, etc.) instead of
    // needing its own bespoke badge method.
    private static void SendCombatBadge(Player player, Npc npc)
    {
        var badge = new PlayerUpdatePacketAddNotifications();
        badge.Notifications.Add(new NotificationInfo
        {
            Guid = npc.Guid,
            ImageId = 24,
            Type = 3,
            Unknown3 = 7,
            Unknown10 = true,
        });
        player.SendTunneled(badge);
    }

    private void RemoveEntityFromZoneTiles(IEntity entity, IEnumerable<ZoneTile> zoneTiles)
    {
        var npcsToRemove = new List<Npc>();
        var playersToRemove = new List<Player>();

        foreach (var zoneTile in zoneTiles)
        {
            foreach (var zoneTileEntity in zoneTile.Entities)
            {
                if (!zoneTileEntity.Value.Visible || entity == zoneTileEntity.Value)
                    continue;

                switch (zoneTileEntity.Value)
                {
                    case Npc zoneTileNpc:
                        {
                            npcsToRemove.Add(zoneTileNpc);

                            if (entity.Visible)
                            {
                                switch (entity)
                                {
                                    case Npc npc:
                                        break;

                                    case Player player:
                                        zoneTileNpc.OnRemoveVisiblePlayers(player);
                                        break;
                                }
                            }
                        }
                        break;

                    case Player zoneTilePlayer:
                        {
                            playersToRemove.Add(zoneTilePlayer);

                            if (entity.Visible)
                            {
                                switch (entity)
                                {
                                    case Npc npc:
                                        {
                                            if (zoneTilePlayer.Mount is not null && zoneTilePlayer.Mount == npc)
                                                continue;

                                            zoneTilePlayer.OnRemoveVisibleNpcs(npc);
                                        }
                                        break;

                                    case Player player:
                                        zoneTilePlayer.OnRemoveVisiblePlayers(player);
                                        break;
                                }
                            }
                        }
                        break;
                }
            }
        }

        entity.OnRemoveVisibleNpcs(npcsToRemove);
        entity.OnRemoveVisiblePlayers(playersToRemove);
    }

    #endregion

    #region Update

    private async Task UpdateEveryTickAsync()
    {
        while (await _updateEveryTickTimer.WaitForNextTickAsync() && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                foreach (var entity in _entities)
                {
                    if (entity.Value is Npc { Static: true })
                        continue;

                    entity.Value.UpdateEveryTick();
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, $"{Name} ({Id}) - Zone Exception");
            }
        }
    }

    private async Task UpdateEverySecondAsync()
    {
        while (await _updateEverySecondTimer.WaitForNextTickAsync() && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                foreach (var entity in _entities)
                {
                    if (entity.Value is Npc { Static: true })
                        continue;

                    entity.Value.UpdateEverySecond();
                }

                UpdateAmbientChatter();
                UpdateEverySecondZone();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, $"{Name} ({Id}) - Zone Exception");
            }
        }
    }

    // Per-second hook for a zone's own scheduled content (timed world events and the like). Runs on the same
    // single thread as the entity sweep above, so an override can touch zone state without locking. Anything
    // that throws here is caught by the loop's handler and logged, same as the rest of the tick.
    protected virtual void UpdateEverySecondZone()
    {
    }

    // NPC greeting bubbles. Driven from the PLAYER side on purpose: the loop above skips Static NPCs (which
    // is most talkers), and the visibility hook is tile-based (~64-192 units) and only fires once on entry —
    // far too coarse for "a player walked up to me". Sweeping each player's already-known visible NPCs keeps
    // this cheap while letting us apply a real proximity gate.
    private void UpdateAmbientChatter()
    {
        foreach (var player in _players.Values)
        {
            if (player.Zone != this)
                continue;

            foreach (var npc in player.VisibleNpcs.Values)
            {
                if (npc.AmbientLineIds is null || npc.AmbientLineIds.Length == 0)
                    continue;

                var dx = npc.Position.X - player.Position.X;
                var dy = npc.Position.Y - player.Position.Y;
                var dz = npc.Position.Z - player.Position.Z;
                if (dx * dx + dy * dy + dz * dz > Npc.AmbientGreetRangeSquared)
                    continue;

                npc.TryAmbientGreet();
            }
        }
    }

    #endregion

    public virtual List<ClaimCodeInfo> GetClaimCodes()
    {
        return [];
    }

    public virtual List<int> GetClaimCodeItemIds(string code)
    {
        var info = GetClaimCodes().FirstOrDefault(x =>
            string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        return info is null ? [] : [info.IconId];
    }

    public virtual int GetClaimCodeItemCount(string code, int itemId) => 1;

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();

        _tiles.Clear();

        _npcs.Clear();
        _players.Clear();
    }
}