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
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.UdpLibrary;

namespace Sanctuary.Game.Zones;

[DebuggerDisplay("{Name} ({Id})")]
public abstract class BaseZone : IZone, IDisposable
{
    protected readonly ILogger _logger;
    private readonly IResourceManager _resourceManager;
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

    private readonly PeriodicTimer _updateEveryTickTimer = new(TimeSpan.FromMilliseconds(TickRate));
    private readonly PeriodicTimer _updateEverySecondTimer = new(TimeSpan.FromSeconds(1));

    public int Id { get; init; }
    public string Name => _zoneDefinition.Name;

    public Vector4 SpawnPosition => _zoneDefinition.SpawnPosition;
    public Quaternion SpawnRotation => _zoneDefinition.SpawnRotation;

    public IEnumerable<Npc> Npcs => _npcs.Values;
    public IEnumerable<Player> Players => _players.Values;

    protected BaseZone(BaseZoneDefinition zoneDefinition, IServiceProvider serviceProvider)
    {
        _zoneDefinition = zoneDefinition;
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();

        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        _logger = loggerFactory.CreateLogger($"Zone {Name} ({Id})");

        _tiles = GenerateTiles();

        foreach (var tile in _tiles)
        {
            ArgumentNullException.ThrowIfNull(tile.Value.Entities);
            ArgumentNullException.ThrowIfNull(tile.Value.VisibleTiles);
        }

        Task.Factory.StartNew(UpdateEveryTickAsync, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task.Factory.StartNew(UpdateEverySecondAsync, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    #region Events

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
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, $"{Name} ({Id}) - Zone Exception");
            }
        }
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