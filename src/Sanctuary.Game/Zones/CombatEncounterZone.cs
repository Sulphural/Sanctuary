using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Pathfinding;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Zones;

// Shared per-mob combat state for the encounter AI (chase / attack / plant / idle / return-home).
// Subclasses extend it with their own extras (Frostfang's roamer + charge-delay + howl).
// The A* route cache + stuck detection live in the shared PathChaseState base (see ChaseNavigator) so the
// overworld enemy AI gets exactly the same routing behavior from the same code.
public class EncounterMobState : PathChaseState
{
    public bool Charging;
    public float SlotAngle;
    public long NextAttackTicks;
    public Vector4 Home;    // spawn post — mobs walk back here and idle while the player is knocked down
    public bool Idling;     // true once parked at Home (broadcast the idle stop only once)
    public bool Planted;    // true once stopped in attack range — stop re-broadcasting position (attack jitter)
    public ulong TargetGuid; // who this mob is currently pursuing — see NearestLivePlayerSticky
}

// Shared base for the combat-encounter zones — the generic data-driven EncounterArenaZone
// plus the bespoke FrostfangArenaZone and TormentedSpiritsArenaZone. It owns the
// parts every combat encounter shares so a fix lands once instead of three times: the knockout-limit / fail /
// revive lifecycle. Subclasses supply the encounter id and the zone-specific ReturnHome (teardown
// + teleport), and keep their bespoke bits (Frostfang waves/Alpha, Spirits tombstones) as their own code.
// (First extraction step — the enemy AI, exit door, and win/reward flow still live in the subclasses and are
// candidates to migrate here next.)
public abstract class CombatEncounterZone : BaseZone
{
    protected CombatEncounterZone(BaseZoneDefinition zoneDefinition, IServiceProvider serviceProvider)
        : base(zoneDefinition, serviceProvider)
    {
    }

    // ── Power-ups (user-supplied real in-game tooltip, 2026-07-27) ─────────────────────────────────────
    // "Power-ups are items that drop off enemies during combat. They can be used by pressing the number 3
    // key. You can only have one at a time..." - see Sanctuary.Game.Combat.PowerupSystem for the 5 real
    // kinds + their effects/FX. Shared here (not per-zone) so every CombatEncounterZone subclass gets real
    // drops automatically - the generic EncounterArenaZone (all data-driven dungeons) previously had NONE
    // of this at all; FrostfangArenaZone/TormentedSpiritsArenaZone already had their own separate Health-
    // only version (SpawnHeart/CollectHearts) which is untouched and still fires alongside this.
    private const float PowerupPickupRange = 2.6f; // matches the proven Health pickup radius
    private const int PowerupPickupTimeoutMs = 120_000;

    // TIGHTENED 2026-07-29 (live feedback: "i cannot pick up powerup sometimes") - was 250ms. At a normal
    // run speed a player can cross the whole 5.2-unit pickup diameter inside a single 250ms gap without a
    // poll ever landing while they're in range, so a pickup grabbed "on the way through" instead of walked
    // up to and stood on could be missed outright. 100ms (matching this file's own mob-AI TickMs elsewhere)
    // quarters that miss window. This does NOT touch the OTHER real cause of "can't pick up" - already
    // holding a held-type power-up (Flame Wave/Earth Shard/Super Shield) correctly blocks picking up
    // ANOTHER held-type one per the tooltip's own "only one at a time" rule (PowerupSystem.Grant's default
    // case) - that's by design, not a bug, and is already explicitly messaged to the player.
    private const int PowerupPollMs = 100;

    // Rolls the drop (PowerupSystem.DropPercent chance) and, on a hit, spawns a walk-over pickup at pos
    // that grants a random real power-up kind on proximity. No-ops silently on a miss - callers don't need
    // their own gating, just call this from OnNpcKilled.
    protected void TryDropPowerup(Vector4 pos)
    {
        if (Random.Shared.Next(100) >= Sanctuary.Game.Combat.PowerupSystem.DropPercent)
            return;

        SpawnPowerupPickup(pos, Sanctuary.Game.Combat.PowerupSystem.RollDropKind());
    }

    // GUARANTEED spawn of a SPECIFIC power-up kind at pos, no drop-chance roll - the core of TryDropPowerup,
    // extracted so a scripted "gift" moment (e.g. Bixie Hive's Queen: "I'll create some power ups for you to
    // use!") can place exact kinds at exact positions instead of relying on random combat drops.
    protected void SpawnPowerupPickup(Vector4 pos, Sanctuary.Game.Combat.PowerupKind kind)
    {
        var modelId = kind switch
        {
            Sanctuary.Game.Combat.PowerupKind.Health => Sanctuary.Game.Combat.PowerupSystem.HealthPickupModelId,
            Sanctuary.Game.Combat.PowerupKind.Energy => Sanctuary.Game.Combat.PowerupSystem.EnergyPickupModelId,
            Sanctuary.Game.Combat.PowerupKind.FlameWave => Sanctuary.Game.Combat.PowerupSystem.FlameWavePickupModelId,
            Sanctuary.Game.Combat.PowerupKind.EarthShard => Sanctuary.Game.Combat.PowerupSystem.EarthShardPickupModelId,
            _ => Sanctuary.Game.Combat.PowerupSystem.SuperShieldPickupModelId,
        };

        if (!TryCreateNpc(out var pickup))
            return;

        pickup.ModelId = modelId;
        pickup.Name = null;
        pickup.NameId = Sanctuary.Game.Combat.PowerupSystem.PowerupNameId;
        pickup.Disposition = 1; // neutral, not a combat target
        pickup.Scale = 1f;
        pickup.IsInteractable = false; // auto-collected by walking over it
        pickup.InteractRange = 0;
        pickup.Visible = true;
        pickup.MaxHealth = 0;
        pickup.ShowHealthBar = false;
        pickup.HideNamePlate = true;
        pickup.ActiveProfile = 8; // matches the proven Health pickup's AddNpc value
        pickup.WalkAnimId = -1;
        pickup.RunAnimId = -1;
        pickup.StandAnimId = -1;
        pickup.MovementType = 2;
        pickup.RiderGuid = ulong.MaxValue;
        pickup.UpdatePosition(pos, Quaternion.Identity);

        foreach (var p in ActivePlayersForPowerups())
        {
            p.OnAddVisibleNpcs(pickup);
            pickup.OnAddVisiblePlayers(p);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (var elapsed = 0; elapsed < PowerupPickupTimeoutMs; elapsed += PowerupPollMs)
                {
                    await Task.Delay(PowerupPollMs);
                    if (pickup.Zone != this)
                        return; // already collected/disposed elsewhere

                    foreach (var p in ActivePlayersForPowerups())
                    {
                        if (p.IsDead)
                            continue; // a knocked-out player can't walk over anything - don't hand it to them
                        var dx = p.Position.X - pickup.Position.X;
                        var dz = p.Position.Z - pickup.Position.Z;
                        if (dx * dx + dz * dz > PowerupPickupRange * PowerupPickupRange)
                            continue;

                        // CORRECTED 2026-07-28 (live feedback: "not allowing me to pick up powerups...
                        // some types work, others don't") - this used to pre-check IsHolding itself and
                        // silently `continue`, which meant PowerupSystem.Grant's own real "already holding"
                        // rejection message never got a chance to fire - the pickup just sat there with no
                        // feedback. Grant is now the single source of truth (returns whether it actually
                        // granted); a false result means it already sent its own explanation, so just try
                        // the next nearby player instead of disposing the pickup out from under everyone.
                        //
                        // Grant BEFORE disposing the pickup, and in its own try/catch: if Grant throws for
                        // any reason, the old code would skip straight to the outer catch and never reach
                        // Dispose() below - leaving this pickup permanently stuck (still spawned, no longer
                        // being polled by this loop since it already returned) with zero error logged. Live
                        // feedback (2026-07-27): "sometimes im not receiving the powerup... some of them
                        // dont do anything" - a silently-swallowed exception here is a real candidate.
                        bool granted;
                        try
                        {
                            granted = Sanctuary.Game.Combat.PowerupSystem.Grant(p, kind, ResourceManagerForPowerups);
                        }
                        catch (Exception grantEx)
                        {
                            _logger.LogError(grantEx, "{label}: power-up grant failed (kind={kind}, player={name}).",
                                EncounterLogName, kind, p.Name);
                            granted = false;
                        }

                        if (!granted)
                            continue;

                        pickup.GracefulRemoval = (false, 0, 5000, Sanctuary.Game.Combat.PowerupSystem.PickupFxId, 1000);
                        pickup.Dispose();
                        return;
                    }
                }

                pickup.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{label}: power-up pickup loop failed (kind={kind}).", EncounterLogName, kind);
                pickup.Dispose();
            }
        });
    }

    // Small seams so TryDropPowerup doesn't need every subclass's own player-list/IResourceManager naming -
    // ActivePlayers() already exists (differently named/typed) on the data-driven zone; the two hand-built
    // zones use their own player collections too. Falls back to the zone tile system's Players if a subclass
    // doesn't override it.
    protected virtual IEnumerable<Player> ActivePlayersForPowerups() => Players;
    protected abstract IResourceManager ResourceManagerForPowerups { get; }

    // Knockouts before the encounter fails. CORRECTED 2026-07-29 (real source: legacy.fanbyte.com/wiki/Combat_(FR)
    // - "Wandering battle instances are allowed 10 knockouts while dungeons are allowed 15 knockouts.") - was a
    // flat 5 for every CombatEncounterZone subclass, with a comment claiming "retail = 5" that had no actual
    // citation behind it. Defaults to the DUNGEON figure (15) here since EncounterArenaZone - the data-driven
    // DungeonCatalog zones this codebase already calls "dungeons" everywhere - is the primary/most numerous
    // subclass; the two bespoke single-arena zones (Frostfang/Tormented Spirits) override down to the
    // "wandering battle instance" figure (10) instead, see their own KnockoutLimit overrides.
    protected virtual int KnockoutLimit => 15;

    private readonly object _knockoutLock = new();
    private readonly Dictionary<ulong, int> _knockouts = [];

    // Encounter/activity id + instance for the client encounter packets (respawn window etc.).
    protected abstract int FailEncounterId { get; }
    protected virtual int FailInstanceId => 1;

    // Short label for the knockout log line (e.g. the dungeon name).
    protected virtual string EncounterLogName => GetType().Name;

    // Combat instances give a long auto-revive FALLBACK — the client's own knockout window runs the real ~10s
    // countdown to the Revive button; this only backstops someone who never presses it.
    protected override int ReviveCooldownMs => 30000;

    // Tear the encounter down for this player and teleport them back to the overworld (zone-specific).
    // `immediate`: true for an explicit player-initiated exit (the minigame UI's own Leave/dismiss button,
    // or a mid-run bail) - always teleport right away. false for the victory door specifically, where a
    // zone MAY choose to hold the teleport back briefly so a result card has time to render first (see
    // EncounterArenaZone.WinCardDelayMs) - the player's own explicit "get me out" input should never be
    // stuck behind that, even if the door was clicked earlier and its delay is still pending.
    protected abstract void ReturnHome(Player player, bool immediate);

    // Tear the encounter's client UI down for this player (ReturnHome calls this before the teleport,
    // and the "leave" chat/exit paths call it directly): mark won/lost, remove the minigame state, reset the
    // encounter data + fighting flags, clear the goals window. On a WIN, GameOver(Won=true) goes FIRST so the
    // end card the teardown triggers reads as a win; a mid-run bail keeps won=false ("TRY AGAIN!").
    public void EndEncounterForPlayer(Player player) => EndEncounterForPlayer(player, won: false);

    public void EndEncounterForPlayer(Player player, bool won)
    {
        if (won)
            player.SendTunneled(new MiniGameGameOverPacket(won: true));
        player.SendTunneled(new MiniGameStateRemovePacket());
        player.SendTunneled(PacketEncounterDataCommon.CreateDefault());
        player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = false });
        player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = false });
        player.SendTunneled(new UiObjectiveClearPacket()); // empty + hide the Goals window (op47/sub5)
        _logger.LogInformation("{label}: encounter released for {name}.", EncounterLogName, player.Name);
    }

    // ── Result cards ─────────────────────────────────────────────────────────────────────────────────
    // DO NOT gate the exit on the player closing the card. The client does NOT report the dismissal: we tried
    // routing BaseCommandPacket sub-op 42 (CommandPacketClosedMinigameEndScreen) and it never arrives for these
    // encounter cards (nor does Leave/RequestExit) — the player just sat in the instance clicking the exit door
    // with nothing happening. The card is raised as part of the teardown instead (EndEncounterForPlayer sends
    // GameOver, and the score card comes from the zone's win flow / SendFailEndScreen), and the teleport goes
    // out with it — which is the behavior that actually works in-game.

    // Forget a player's knockout tally (call on encounter start/complete so a fresh run starts at 0).
    protected void ResetKnockouts(ulong guid)
    {
        lock (_knockoutLock)
            _knockouts.Remove(guid);
    }

    // How many times this player has been knocked out this run (for the win-screen score).
    protected int KnockoutsUsed(ulong guid)
    {
        lock (_knockoutLock)
            return _knockouts.TryGetValue(guid, out var k) ? k : 0;
    }

    // Enter the encounter at full REAL max HP + mana (Stats[MaxHealth]) so the bar matches the
    // real-damage claw/bite — a fixed 2500 made it jump on the first hit. Call from OnClientIsReady.
    protected static void EnterAtFullVitals(Player player)
    {
        var startHp = player.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 2500;
        player.CurrentHitpoints = startHp;
        player.SendTunneled(new ClientUpdatePacketHitpoints { CurrentHitpoints = startHp, MaxHitpoints = startHp });
        player.SendTunneled(new ClientUpdatePacketMana { CurrentMana = 100, MaxMana = 100 });
    }

    // The victory exit door (each zone spawns it at its own spot via SpawnExitDoor, then registers it here).
    // Clicking it (routed from CommandPacketInteractRequestHandler) leaves the encounter.
    private readonly object _exitDoorLock = new();
    private Npc? _exitDoor;

    // The live victory door, or null. Subclasses read it for the visibility sweep + cleanup.
    protected Npc? ExitDoor
    {
        get { lock (_exitDoorLock) return _exitDoor; }
    }

    // Register the spawned victory door (or null to clear it on a re-run) so IsExitDoor/UseExitDoor
    // recognise clicks on it.
    protected void SetExitDoor(Npc? door)
    {
        lock (_exitDoorLock)
            _exitDoor = door;
    }

    public bool IsExitDoor(ulong guid)
    {
        lock (_exitDoorLock)
            return _exitDoor is { } door && door.Guid == guid;
    }

    public void UseExitDoor(Player player)
    {
        // WIN: the victory door tears down + teleports home. EndEncounterForPlayer(won: true) raises the
        // "You Win!" card on the way out. The client never reports the card's dismissal (see the note
        // above), so a zone MAY hold the teleport back a bit to give the card time to render — but if the
        // player presses the UI's own Leave button before that elapses, LeaveEncounter below (immediate)
        // takes over instead.
        _logger.LogInformation("{label}: {name} used the exit door.", EncounterLogName, player.Name);
        ReturnHome(player, immediate: false);
    }

    // The minigame UI's LEAVE button (op39/sub6) and RequestExit (op41/sub109): bail out of the instance
    // back to the overworld. Same teardown + teleport as the exit door, but ALWAYS immediate — this is
    // also how the result card's own Leave/dismiss action works (see MiniGameEndPacketHandler), so it must
    // never be stuck behind a delay UseExitDoor may have started.
    public void LeaveEncounter(Player player)
    {
        if (player.Zone != this)
            return;

        _logger.LogInformation("{label}: {name} left the encounter.", EncounterLogName, player.Name);
        ReturnHome(player, immediate: true);
    }

    // The client reported the post-win reward wheel actually finished spinning (op39/sub46
    // LootWheelOnRotationStopped, see BaseMiniGamePacketHandler). Default no-op — only EncounterArenaZone
    // currently holds the teleport back waiting for this; the other bespoke zones always return immediately.
    public virtual void NotifyRewardWheelStopped(Player player)
    {
    }

    // ── Mob pathfinding ──────────────────────────────────────────────────────────────────────────────
    // Mob chase movement used to be a pure straight-line vector toward the player's slot, with no wall/
    // geometry awareness at all — mobs cut straight through cave walls to reach the player. This builds
    // the same real ObstacleMap/WaypointGraph machinery the overworld uses for "Take Me There" and its own
    // enemy AI (see WaypointGraph.cs's own header comment: no client-facing navmesh exists in the
    // extracted assets, so this is a hand-rolled proximity-linked graph over real placement/wall data),
    // but seeded from an auto-generated grid instead of curated NPC points (dungeons have no equivalent
    // curated point set) — see BuildMobPathfinding. The results land in the zone's shared
    // NavObstacles/NavGraph, so mob chase and any other consumer read one source of truth.
    //
    // Chasing only consults the graph when the straight line is actually blocked, so a dungeon with no
    // geometry data (NavObstacles null) or genuinely open geometry behaves exactly as before — zero
    // regression risk for the common case.
    protected ObstacleMap? MobObstacleMap => NavObstacles;
    protected WaypointGraph? MobWaypointGraph => NavGraph;
    private Vector4 _mobPathCenter;
    private float _mobPathRadius;

    // Real wall/boundary check for AddSpawnArea's scripted-pack scatter (see BaseZone.IsScriptSpawnPositionValid) -
    // same bounds+obstacle test BuildMobPathfinding itself uses when grid/corner-sampling, and JitteredWalkablePos
    // uses for prop jitter. Only meaningful once BuildMobPathfinding has actually run (MobObstacleMap != null);
    // before/without that, defers to the base "anything goes" default. Checks the WHOLE hop from the real marker
    // (IsLineWalkable, sampled every 2u) rather than just the candidate's own endpoint - a lone IsBlocked(pos)
    // check let a big scatter jump land clean on the far side of a thin wall strip without ever registering as
    // blocked, since neither the marker nor the landing spot sat within the wall's own proximity margin.
    protected override bool IsScriptSpawnPositionValid(Vector4 from, Vector4 pos)
    {
        if (MobObstacleMap is null)
            return true;
        var dx = pos.X - _mobPathCenter.X;
        var dz = pos.Z - _mobPathCenter.Z;
        if (dx * dx + dz * dz > _mobPathRadius * _mobPathRadius)
            return false;
        return MobObstacleMap.IsLineWalkable(from, pos);
    }

    // Loads the world's real geometry - props (.gcnk) AND the cave/terrain wall boundary (.gzne) - via
    // the shared ObstacleMapLoader, then grid-samples the dungeon's playable circle (center/radius) for
    // walkable points, adds wall-hug corner nodes, and links them into a WaypointGraph. Call once from the
    // subclass constructor/definition with the dungeon's own world name + center + radius. No-ops (leaves
    // the zone's NavObstacles/NavGraph null, unchanged straight-line behavior) if the world has neither
    // kind of data on disk.
    protected void BuildMobPathfinding(string world, Vector4 center, float radius)
    {
        _mobPathCenter = center;
        _mobPathRadius = radius;

        var obstacles = ObstacleMapLoader.TryLoad(world, _logger, out var wallStrips);
        if (obstacles is null)
            return; // no real geometry data for this world - stay null, straight-line fallback

        var points = WaypointGraphBuilder.SampleWalkableGrid(center, radius, obstacles, out var spacing);
        var gridCount = points.Count;

        // Wall strips aren't generated relative to this dungeon's bounds, so a strip near the edge can
        // produce a hug candidate OUTSIDE the playable circle - such a node could get chosen mid-route and
        // send a chasing mob genuinely off the map. Flatten to the arena's floor height (unlike the
        // overworld, a dungeon is one tier).
        WaypointGraphBuilder.AddWallHugPoints(points, wallStrips, obstacles, flattenY: center.Y, inBounds: p =>
        {
            var dx = p.X - center.X;
            var dz = p.Z - center.Z;
            return dx * dx + dz * dz <= radius * radius;
        });

        NavObstacles = obstacles;
        NavGraph = WaypointGraph.BuildFromPoints(points, maxEdgeDistance: spacing * 2.2f, maxNeighborsPerNode: 10, obstacles: obstacles);
        _logger.LogInformation("{label}: built mob waypoint graph for {world} ({obstacles} props, {walls} wall segments, {nodes} nodes = {grid} grid + {hug} wall-hug).",
            EncounterLogName, world, obstacles.ObstacleCount, obstacles.WallSegmentCount, NavGraph.NodeCount, gridCount, points.Count - gridCount);

        BuildMobCollisionMesh(world, center.Y);
    }

    // Real per-model collision geometry for this dungeon (see MeshObstacleMap), replacing the circle
    // approximation for line-of-walk tests. Dungeon-sized worlds only - this costs ~15-80ms and a few MB
    // here, but would be prohibitive on the overworld's 39k placements / 4.2M triangles.
    //
    // This is what actually stops a mob walking through a wall: the circle approximation genuinely does
    // not know most walls exist. Measured on Bixie Hive - whose 228 placements ALL have real collision
    // meshes, 170k triangles between them - 15.6% of chase lines cross a wall that the circles miss
    // entirely, and the hive reads as almost-open space without this.
    private void BuildMobCollisionMesh(string world, float groundY)
    {
        _collisionLibrary ??= new ModelCollisionLibrary(ObstacleMapLoader.AssetsDirectory);
        if (!_collisionLibrary.Available)
            return;

        var placements = new List<GcnkParser.Placement>();
        foreach (var file in System.IO.Directory.EnumerateFiles(ObstacleMapLoader.AssetsDirectory, $"{world}_*.gcnk*", System.IO.SearchOption.AllDirectories))
        {
            try { placements.AddRange(GcnkParser.ParseFile(file)); }
            catch { /* already reported by the obstacle-map load above */ }
        }

        if (placements.Count == 0)
            return;

        var mesh = MeshObstacleMap.Build(placements, _collisionLibrary, groundY);
        if (mesh.WallEdgeCount == 0)
            return; // nothing wall-like near this floor - keep the circle approximation

        NavMesh = mesh;
        _logger.LogInformation("{label}: built real collision mesh for {world} ({edges} wall edges from {resolved}/{total} placements).",
            EncounterLogName, world, mesh.WallEdgeCount, _collisionLibrary.Resolved, placements.Count);
    }

    // Shared across every dungeon zone - the asset filename index behind it takes ~185ms to build and is
    // identical for all of them.
    private static ModelCollisionLibrary? _collisionLibrary;

    // ── Shared enemy AI ───────────────────────────────────────────────────────────────────────────────
    // Chase to an owned slot around the player, plant + attack in range, disengage to the spawn post and idle
    // while the player is knocked down. Subclasses run their own aggro/charge gating (and Frostfang its
    // roamer/waves), then call TickMobCombat for engaged mobs / TickMobReturnHome while the player is down.

    // Was 300ms (3.3Hz) - 3x coarser than the overworld CombatNpc's 10Hz tick (GatewayServer.cs), and unlike
    // that path this loop broadcasts a position update every single tick unconditionally (no distance
    // throttle), so mobs in dungeons moved in visibly larger ~1.5-1.8 unit jumps (MobChaseSpeed * old dt)
    // instead of the smaller, more frequent steps the overworld uses - the main source of dungeon-specific
    // rubber-banding/steppiness. Matching the overworld's cadence directly shrinks each step proportionally.
    protected const int TickMs = 100;
    protected const float MobYSpeed = 12f;
    protected const float MobAttackRange = 2.6f;
    protected const float MobEngageRadius = 1.9f;
    protected const int MobAttackCooldownMs = 4000;   // per-mob
    protected const int MobAttackGlobalGapMs = 1200;  // pack-wide minimum spacing
    protected const int MobAttackDamage = 150;
    protected const int MobAttackCritDamage = 300;
    protected const int MobAttackCritPercent = 10;
    protected const int MobAttackFxId = 5409;         // live melee-hit composite
    protected const int MobAttackCritFxId = 5622;

    // Chase/return speed. The pre-spawned zones drift at 5; Frostfang wolves charge at 6.
    protected virtual float MobChaseSpeed => 5f;

    // Max vertical distance a mob can perceive/reach a player across when picking a target. Targeting was
    // 2D (X/Z) only, with Y purely cosmetic (TickMobCombat just drifts the mob's own Y toward the target's
    // over MobYSpeed, no floor/ceiling awareness at all) - live feedback: in a multi-level cave, mobs on a
    // floor below the player would still lock onto and chase them, visually "climbing" straight up through
    // solid rock since nothing ever stopped the Y drift or the targeting that fed it. This doesn't give
    // mobs real 3D navigation (no data source for that - see the .gzne wall-geometry notes elsewhere), it
    // just stops them from targeting a player who's clearly on a different level in the first place; a
    // mob with no valid same-floor target falls through to TickMobReturnHome like the "no live target"
    // case, which is the correct visible behavior.
    // TIGHTENED 2026-07-26: 12 was still too generous - live-confirmed still clipping. Real data from
    // Cracked Claw Caverns' own coordinate sheet shows the main cave floor's real Y values cluster within
    // ~5 units of each other, while the Frog Log positions (a real lower sub-area) sit ~10-11 units below
    // that - a 12-unit threshold let that genuine floor gap through. 6 sits comfortably below the real
    // gap while still tolerating ordinary uneven-but-connected terrain on the same level.
    protected const float MaxFloorYDelta = 6f;

    // Attack spacing is per-TARGET, not pack-wide: the pack won't all bite the same player at once, but two
    // players being attacked by different mobs each get their own cadence (a single pack-wide gate made a group
    // share one bite budget so each player barely got hit). Solo (one target) is identical to the old behavior.
    // Touched only from the single per-zone AI loop, so a plain dictionary is safe.
    private readonly Dictionary<ulong, long> _lastAttackTicksByTarget = [];

    // Send a packet to every player currently in this encounter instance (per-zone one-liner).
    protected abstract void Broadcast(ISerializablePacket packet);

    protected static float MoveToward(float current, float goal, float maxDelta)
    {
        var delta = goal - current;
        if (MathF.Abs(delta) <= maxDelta)
            return goal;
        return current + MathF.Sign(delta) * maxDelta;
    }

    // The nearest player to pos that ISN'T knocked out, or null if every player
    // is down. Mobs pick their target with this each tick so the pack spreads across a group and re-targets
    // when a player falls — instead of the whole pack fixating on one player (and going home the moment that
    // one dies, ignoring everyone else still fighting).
    protected static Player? NearestLivePlayer(Vector3 pos, IReadOnlyList<Player> players)
    {
        Player? best = null;
        var best2 = float.MaxValue;
        foreach (var p in players)
        {
            if (p.IsDead || MathF.Abs(p.Position.Y - pos.Y) > MaxFloorYDelta)
                continue;
            var dx = p.Position.X - pos.X;
            var dz = p.Position.Z - pos.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 < best2)
            {
                best2 = d2;
                best = p;
            }
        }
        return best;
    }

    // Buffer (in units) a currently-pursued player must be beaten by before a mob switches target. Without
    // this, re-picking the literal nearest player every tick made a mob's target - and therefore its pursued
    // slot position - flip between two similarly-distant players in group combat, a visible jump each flip.
    protected const float StickyTargetSwitchBuffer = 3f;

    // Same as NearestLivePlayer, but sticks with the mob's current target (state.TargetGuid) unless it's
    // dead/gone or a genuinely closer player has shown up (more than StickyTargetSwitchBuffer units nearer).
    // Updates state.TargetGuid as a side effect. Use this for actual combat targeting; plain NearestLivePlayer
    // is still fine for non-targeting lookups (e.g. TickFleeingAlpha just needs any live player to run from).
    protected static Player? NearestLivePlayerSticky(Vector3 pos, IReadOnlyList<Player> players, EncounterMobState state)
    {
        Player? nearest = null;
        var nearestD2 = float.MaxValue;
        Player? current = null;
        var currentD2 = float.MaxValue;

        foreach (var p in players)
        {
            // A player who walked up/down to a different floor since this mob last checked stops being a
            // valid sticky target too, not just a candidate for a NEW one - otherwise a mob could keep
            // "remembering" and chasing someone it can no longer actually reach floor-wise.
            if (p.IsDead || MathF.Abs(p.Position.Y - pos.Y) > MaxFloorYDelta)
                continue;
            var dx = p.Position.X - pos.X;
            var dz = p.Position.Z - pos.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 < nearestD2)
            {
                nearestD2 = d2;
                nearest = p;
            }
            if (p.Guid == state.TargetGuid)
            {
                current = p;
                currentD2 = d2;
            }
        }

        if (current is not null)
        {
            var buffered = MathF.Sqrt(nearestD2) + StickyTargetSwitchBuffer;
            if (buffered * buffered >= currentD2)
                return current; // nothing meaningfully closer - keep pursuing the current target
        }

        state.TargetGuid = nearest?.Guid ?? 0;
        return nearest;
    }

    // Player is knocked down: disengage — amble back to the spawn post and idle there. Call this
    // (instead of TickMobCombat) for every mob while the player is down; resets Charging/Planted so the mob
    // re-engages cleanly on revive.
    protected void TickMobReturnHome(Npc mob, EncounterMobState state, float dt, long now)
    {
        state.Charging = false;
        state.Planted = false;
        var here = new Vector3(mob.Position.X, mob.Position.Y, mob.Position.Z);
        var toHome = new Vector2(state.Home.X - here.X, state.Home.Z - here.Z);
        var distHome = toHome.Length();
        if (distHome > 0.6f)
        {
            state.Idling = false;
            var (dir, dist) = ChaseStep(here, new Vector3(state.Home.X, state.Home.Y, state.Home.Z), state, now);
            var step = MathF.Min(MobChaseSpeed * dt, dist);
            var ny = MoveToward(here.Y, state.Home.Y, MobYSpeed * dt);
            var np = new Vector4(here.X + dir.X * step, ny, here.Z + dir.Y * step, mob.Position.W);
            var frot = dir != Vector2.Zero ? new Quaternion(dir.X, 0f, dir.Y, 0f) : mob.Rotation;
            mob.UpdatePosition(np, frot);
            Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = np, Rotation = frot, State = 0, Unknown = 0 });
        }
        else if (!state.Idling)
        {
            state.Idling = true; // arrived — plant idle once (State 1 = standing)
            Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = mob.Position, Rotation = mob.Rotation, State = 1, Unknown = 0 });
        }
    }

    // Chase step toward `slot`, delegated to the shared ChaseNavigator so the dungeon AI, the overworld
    // enemy AI and "Take Me There" all steer over the same obstacle/graph machinery instead of separately
    // drifting copies. Behavior is unchanged from when this logic lived here: straight line when there's
    // no geometry data or the direct line is clear, otherwise a step along a cached A* route, with
    // stuck-detection dropping a route that stops making progress.
    private (Vector2 Dir, float Dist) ChaseStep(Vector3 here, Vector3 slot, EncounterMobState state, long now)
        => ChaseNavigator.Step(here, slot, state, NavObstacles, TryFindPath, now);

    // Engaged-mob combat tick (player alive): converge on an owned slot around the player, plant
    // once in attack range (re-broadcasting every tick bobbed the model + fought the swing = jitter), and
    // attack on the per-mob cooldown gated by the pack-wide spacing.
    protected void TickMobCombat(Npc mob, EncounterMobState state, Player player, Vector3 playerPos, long now, float dt)
    {
        var here = new Vector3(mob.Position.X, mob.Position.Y, mob.Position.Z);
        var slot = playerPos + new Vector3(MathF.Sin(state.SlotAngle), 0f, MathF.Cos(state.SlotAngle)) * MobEngageRadius;
        var toPlayerH = new Vector2(playerPos.X - here.X, playerPos.Z - here.Z);
        var distToPlayerH = toPlayerH.Length();
        var face = distToPlayerH > 0.01f ? toPlayerH / distToPlayerH : new Vector2(0f, 1f);
        var rot = new Quaternion(face.X, 0f, face.Y, 0f);
        var newY = MoveToward(here.Y, playerPos.Y, MobYSpeed * dt);

        if (distToPlayerH > MobAttackRange)
        {
            state.Planted = false;
            var (dir, dist) = ChaseStep(here, slot, state, now);
            // Face the way it's actually walking, not straight at the player — matters when a blocked
            // route steps sideways/around a corner instead of directly toward them. Identical to the old
            // `rot` (toward the player) whenever dir == the straight-line case, so unobstructed mobs look
            // unchanged.
            var moveRot = dir != Vector2.Zero ? new Quaternion(dir.X, 0f, dir.Y, 0f) : rot;
            var step = MathF.Min(MobChaseSpeed * dt, dist);
            var np = new Vector4(here.X + dir.X * step, newY, here.Z + dir.Y * step, mob.Position.W);
            mob.UpdatePosition(np, moveRot);
            Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = np, Rotation = moveRot, State = 0, Unknown = 0 });
        }
        else
        {
            if (!state.Planted)
            {
                state.Planted = true;
                var np = new Vector4(here.X, newY, here.Z, mob.Position.W);
                mob.UpdatePosition(np, rot);
                Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = np, Rotation = rot, State = 1, Unknown = 0 });
            }

            _lastAttackTicksByTarget.TryGetValue(player.Guid, out var lastAttackOnTarget);
            if (now >= state.NextAttackTicks && now - lastAttackOnTarget >= MobAttackGlobalGapMs && !player.IsDead)
            {
                state.NextAttackTicks = now + MobAttackCooldownMs;
                _lastAttackTicksByTarget[player.Guid] = now;
                PerformMobAttack(mob, player);
            }
        }
    }

    // One mob attack: real damage (knocks the player out at 0 -> the fail flow), the floating
    // number/bar + hit FX, and an explicit swing clip for boss models whose default contact event doesn't
    // animate (the Abominable Snowman).
    protected void PerformMobAttack(Npc attacker, Player player)
    {
        var maxHp = player.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 2500;

        // DODGE (base avoidance + Archer's Reflexes): the player evades — the AttackTargetDodged packet renders the
        // "Dodge" text + plays the attacker's swing, and the com_dodge sidestep layers on top; no damage is dealt.
        // Boss models whose default contact event doesn't animate still need their explicit swing clip.
        if (player.TryDodgeIncomingAttack(attacker.Guid))
        {
            if (CombatNpc.ExplicitAttackAnimByModel.TryGetValue(attacker.ModelId, out var missAnim))
                Broadcast(new PlayerUpdatePacketSetAnimation { Guid = attacker.Guid, AnimationId = missAnim });
            return;
        }

        var crit = Random.Shared.Next(100) < MobAttackCritPercent;
        var dmg = player.ReduceIncomingDamage(crit ? MobAttackCritDamage : MobAttackDamage); // Ninja Shrouded Armor
        player.TakeDamage(dmg);
        Broadcast(new CombatPacketAttackProcessed
        {
            AttackerGuid = attacker.Guid,
            TargetGuid = player.Guid,
            Damage = dmg,
            MaxHealth = maxHp,
            CompositeEffectId = crit ? MobAttackCritFxId : MobAttackFxId,
            CurrentHealth = player.CurrentHitpoints,
        });
        if (CombatNpc.ExplicitAttackAnimByModel.TryGetValue(attacker.ModelId, out var swingAnimId))
            Broadcast(new PlayerUpdatePacketSetAnimation { Guid = attacker.Guid, AnimationId = swingAnimId });
    }

    public override void OnPlayerKnockedOut(Player player)
    {
        if (player.Zone != this)
            return;

        int kos;
        lock (_knockoutLock)
        {
            _knockouts.TryGetValue(player.Guid, out kos);
            kos++;
            _knockouts[player.Guid] = kos;
        }

        _logger.LogInformation("{label}: {name} knocked out ({kos}/{limit}).", EncounterLogName, player.Name, kos, KnockoutLimit);

        // Drop the fighting flags either way (so sub125 shows the auto-recover version, not the overworld
        // pay/safe one).
        player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = false });
        player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = false });

        if (kos >= KnockoutLimit)
        {
            // Out of lives — FAIL. Persistent "TRY AGAIN!" end-screen (SendFailEndScreen: clears the knockdown
            // UI + Won=0 + score card), HOLD it, THEN tear down + teleport home and REVIVE so the player arrives
            // ALIVE (a fail used to strand them knocked out, which blocked firing even through a job-swap).
            // The hold is a timer because the client never reports the card being closed — see the note on
            // the result cards above.
            SendFailEndScreen(player);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(FailCardHoldMs);
                    ReturnHome(player, immediate: true); // already held for FailCardHoldMs above
                    player.Respawn();
                }
                catch (Exception ex) { _logger.LogError(ex, "Fail-return failed."); }
            });
            return;
        }

        // Non-fatal knockout — show the recover window + counter; auto-revive is the fallback.
        player.SendTunneled(new MiniGameKnockOutPacket(kos, KnockoutLimit));
        player.SendTunneled(new EncounterShowRespawnWindowPacket(FailEncounterId, FailInstanceId));
        ScheduleAutoRevive(player);
    }

    public override void OnPlayerRespawn(Player player)
    {
        // Revive with full HP + FX at the death spot (the window's Revive button revives you where you fell).
        var pos = player.DeathPosition;
        player.Respawn();

        if (player.Zone == this)
        {
            player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = true });
            player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = true });
            player.UpdatePosition(pos, player.Rotation);
            player.SendTunneled(new ClientUpdatePacketUpdateLocation
            {
                Position = pos,
                Rotation = player.Rotation,
                Teleport = true,
            });
        }
    }
}
