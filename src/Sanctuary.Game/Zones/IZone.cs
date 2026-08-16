using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Scripting;
using Sanctuary.UdpLibrary;

namespace Sanctuary.Game.Zones;

public interface IZone : IScriptZone
{
    int DefinitionId { get; }

    Vector4 SpawnPosition { get; }
    Quaternion SpawnRotation { get; }

    // Seconds of simulated time per zone tick - the delta movement code integrates against.
    float TickDeltaSeconds { get; }

    // Shared navigation data for this zone - see BaseZone's "Navigation (shared)" region. All null on a
    // zone with no data, which every consumer treats as "straight lines are fine".
    // Pathfinder is the native ".map" graph (preferred); NavGraph is the hand-rolled fallback.
    Pathfinding.Pathfinder<Pathfinding.MapNode>? Pathfinder { get; }
    Pathfinding.ObstacleMap? NavObstacles { get; }
    Pathfinding.WaypointGraph? NavGraph { get; }

    // True when the straight segment a->b doesn't cross real geometry (no data => true).
    bool IsLineWalkable(Vector4 a, Vector4 b);

    // Real A* route, or null when there's no graph / the endpoints are disconnected.
    List<Vector4>? TryFindPath(Vector4 start, Vector4 destination);

    #region Events

    // Fired once, after the zone has finished constructing — runs the zone's Lua onStart(zone), if any.
    void OnStart();

    // Dev/live-reload: re-reads the zone's .lua file and re-runs its onStart. See BaseZone.ReloadScript
    // for the idempotency caveat (explicit-guid spawns only) before wiring this up to anything automatic.
    bool ReloadScript();

    void OnClientIsReady(Player entity);
    void OnClientFinishedLoading(Player entity);
    void RefreshPlayerCustomizations(Player player);

    // COMBAT: called when an NPC in this zone is killed, so the zone decides the consequence
    // (training dummy resets; encounter wolves despawn and advance the encounter; etc.).
    void OnNpcKilled(Player killer, Npc npc);

    // COMBAT: called after every non-fatal hit lands on an NPC (post-ApplyDamage, when it did
    // NOT die), so the zone can react to HP thresholds — e.g. the Frostfang Alpha flees at low health
    // instead of dying. Default: no-op.
    void OnNpcDamaged(Player attacker, Npc npc);

    // COMBAT: summon N combat-capable clone NPCs that fight alongside the summoner, then despawn - see
    // CombatCloneConfig's header comment and BaseZone.SummonCombatClones for the generalized engine.
    void SummonCombatClones(Player summoner, int count, int lifetimeSeconds, CombatCloneConfig config);

    // DEATH: the player's HP hit 0 (knocked out). Overworld = client shows its KO UI + revive in
    // place; combat instances count the knockout and fail the encounter at the limit.
    void OnPlayerKnockedOut(Player player);

    // DEATH: the player pressed respawn — revive them (in place for the overworld, at the dungeon
    // spawn for instances).
    void OnPlayerRespawn(Player player);

    // COMBAT: push an NPC's cursor (attack/talk) to a player so it is selectable as a target.
    void SendNpcRelevance(Player player, Npc npc);

    // COMBAT: push an NPC's current/max health to a player so its nameplate health bar renders.
    void SendNpcHealth(Player player, Npc npc);

    #endregion

    #region Entities

    IEnumerable<Npc> Npcs { get; }
    IEnumerable<Player> Players { get; }

    bool TryGetNpc(ulong guid, [MaybeNullWhen(false)] out Npc npc);
    bool TryGetPlayer(ulong guid, [MaybeNullWhen(false)] out Player player);
    bool TryGetEntity(ulong guid, [MaybeNullWhen(false)] out IEntity entity);

    bool TryAddMount(Mount mount);
    bool TryAddPet(Pet pet);
    bool TryAddPlayer(Player player);

    bool TryCreateNpc([MaybeNullWhen(false)] out Npc npc);
    bool TryCreateNpc(ulong guid, [MaybeNullWhen(false)] out Npc npc);

    #region Collection nodes

    IReadOnlyList<CollectionNodePoolStatus> GetCollectionNodePoolStatuses();
    IReadOnlyList<CollectionNodeSpawnStatus> GetCollectionNodeSpawnStatuses(string? poolKey = null);
    bool TryPlaceCollectionNodeSpawn(string poolKey, Vector4 position, float heading,
        [MaybeNullWhen(false)] out CollectionNodeSpawnDefinition spawn, out bool activated);
    bool TryConfigureCollectionNodePool(string poolKey, int maxActiveNodes, int respawnSeconds,
        out int activeCount, out int targetActiveCount);
    bool TryRemoveCollectionNodeSpawn(int id,
        [MaybeNullWhen(false)] out CollectionNodeSpawnDefinition removedSpawn);
    bool TryRemoveNearestCollectionNodeSpawn(Vector4 position, float radius,
        [MaybeNullWhen(false)] out CollectionNodeSpawnDefinition removedSpawn);
    void CompleteCollectionNode(CollectionNode node);

    #endregion

    bool TryCreateMount(Player rider, MountDefinition definition, [MaybeNullWhen(false)] out Mount mount);
    bool TryCreatePet(Player owner, Resources.Definitions.PetDefinition definition, [MaybeNullWhen(false)] out Pet pet);
    bool TryCreateCombatNpc([MaybeNullWhen(false)] out CombatNpc combatNpc);
    bool TryCreateEncounterEntryNpc([MaybeNullWhen(false)] out EncounterEntryNpc entryNpc);
    bool TryCreateProjectileNpc([MaybeNullWhen(false)] out ProjectileNpc projectileNpc);
    bool TryCreatePlayer(ulong guid, UdpConnection connection, [MaybeNullWhen(false)] out Player player);

    bool TryRemoveNpc(ulong guid);
    bool TryRemovePlayer(ulong guid);

    #endregion

    #region Zone System

    ZoneTile GetTileFromPosition(Vector4 position);
    void UpdateEntityZoneTile(IEntity entity, ZoneTile from, ZoneTile to);

    #endregion
}
