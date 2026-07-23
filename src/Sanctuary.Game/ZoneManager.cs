using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Game.Zones;

namespace Sanctuary.Game;

public class ZoneManager : IZoneManager
{
    private readonly ILogger _logger;
    private readonly IResourceManager _resourceManager;
    private readonly IServiceProvider _serviceProvider;

    private static int _uniqueId = 1;

    private readonly ConcurrentDictionary<int, IZone> _zones = new();

    private const int StartingZoneDefinitionId = 1;
    public StartingZone StartingZone { get; private set; } = null!;

    // INSTANCE (Frostfang Fury): lazily-created shared arena zone (per-party instancing later).
    private FrostfangArenaZone? _frostfangArena;
    private readonly object _frostfangArenaLock = new();

    // INSTANCE (Tormented Spirits!): the Blackspore graveyard arena, same lazy pattern.
    private TormentedSpiritsArenaZone? _spiritArena;
    private readonly object _spiritArenaLock = new();

    private CombatTutorialZone? _combatTutorial;
    private readonly object _combatTutorialLock = new();

    // Debug world browser (!map): one cached instance per world name so repeated jumps reuse the zone.
    private readonly Dictionary<string, DebugWorldZone> _debugWorlds = [];
    private readonly object _debugWorldLock = new();

    // Data-driven combat dungeons (DungeonCatalog): one cached EncounterArenaZone instance per activity id.
    private readonly Dictionary<int, EncounterArenaZone> _encounterArenas = [];
    private readonly object _encounterArenaLock = new();

    public ZoneManager(
        ILoggerFactory loggerFactory,
        IResourceManager resourceManager,
        IServiceProvider serviceProvider)
    {
        _logger = loggerFactory.CreateLogger<ZoneManager>();

        _resourceManager = resourceManager;
        _serviceProvider = serviceProvider;
    }

    public bool Load()
    {
        if (!TryCreateStartingZone(StartingZoneDefinitionId, out var startingZone))
            return false;

        StartingZone = startingZone;

        return true;
    }

    public bool TryGetPlayer(ulong guid, [MaybeNullWhen(false)] out Player player)
    {
        player = default;

        foreach (var zone in _zones)
        {
            if (zone.Value.TryGetPlayer(guid, out player))
                return true;
        }

        return false;
    }

    public bool TryGetPlayer(string name, [MaybeNullWhen(false)] out Player player)
    {
        player = default;

        foreach (var zone in _zones)
        {
            foreach (var zonePlayer in zone.Value.Players)
            {
                if (zonePlayer.Name.FullName == name)
                {
                    player = zonePlayer;
                    return true;
                }
            }
        }

        return false;
    }

    public FrostfangArenaZone GetOrCreateFrostfangArena()
    {
        lock (_frostfangArenaLock)
        {
            if (_frostfangArena is null)
            {
                _frostfangArena = new FrostfangArenaZone(_serviceProvider)
                {
                    Id = _uniqueId++
                };

                _zones.TryAdd(_frostfangArena.Id, _frostfangArena);

                _frostfangArena.OnStart();

                _logger.LogInformation("Created Frostfang arena zone {name} ({id}).", _frostfangArena.Name, _frostfangArena.Id);
            }

            return _frostfangArena;
        }
    }

    public EncounterArenaZone GetOrCreateEncounterArena(int activityId)
    {
        lock (_encounterArenaLock)
        {
            if (!_encounterArenas.TryGetValue(activityId, out var arena))
            {
                var def = Sanctuary.Game.Dungeons.DungeonCatalog.ByActivity[activityId];
                arena = new EncounterArenaZone(def, _serviceProvider) { Id = _uniqueId++ };
                _encounterArenas[activityId] = arena;
                _zones.TryAdd(arena.Id, arena);
                arena.OnStart();
                _logger.LogInformation("Created encounter arena '{comment}' ({name}, id {id}) for activity {a}.",
                    def.Comment, arena.Name, arena.Id, activityId);
            }
            return arena;
        }
    }

    public DebugWorldZone GetOrCreateDebugWorld(string worldName, System.Numerics.Vector4 spawn)
    {
        lock (_debugWorldLock)
        {
            // Re-create when the spawn changes so callers can re-probe a world at different coords.
            if (_debugWorlds.TryGetValue(worldName, out var existing))
            {
                if (existing.SpawnPosition == spawn)
                    return existing;
                _zones.TryRemove(existing.Id, out _);
                _debugWorlds.Remove(worldName);
            }

            var zone = new DebugWorldZone(worldName, spawn, _serviceProvider) { Id = _uniqueId++ };
            _debugWorlds[worldName] = zone;
            _zones.TryAdd(zone.Id, zone);
            zone.OnStart();
            _logger.LogInformation("Created debug world zone {name} ({id}) spawn {spawn}.", zone.Name, zone.Id, spawn);
            return zone;
        }
    }

    public CombatTutorialZone GetOrCreateCombatTutorial()
    {
        lock (_combatTutorialLock)
        {
            if (_combatTutorial is null)
            {
                _combatTutorial = new CombatTutorialZone(_serviceProvider)
                {
                    Id = _uniqueId++
                };

                _zones.TryAdd(_combatTutorial.Id, _combatTutorial);

                _combatTutorial.OnStart();

                _logger.LogInformation("Created combat tutorial zone {name} ({id}).", _combatTutorial.Name, _combatTutorial.Id);
            }

            return _combatTutorial;
        }
    }

    public TormentedSpiritsArenaZone GetOrCreateSpiritArena()
    {
        lock (_spiritArenaLock)
        {
            if (_spiritArena is null)
            {
                _spiritArena = new TormentedSpiritsArenaZone(_serviceProvider)
                {
                    Id = _uniqueId++
                };

                _zones.TryAdd(_spiritArena.Id, _spiritArena);

                _spiritArena.OnStart();

                _logger.LogInformation("Created Tormented Spirits arena zone {name} ({id}).", _spiritArena.Name, _spiritArena.Id);
            }

            return _spiritArena;
        }
    }

    private bool TryCreateStartingZone(int definitionId, [MaybeNullWhen(false)] out StartingZone zone)
    {
        zone = default;

        if (!_resourceManager.Zones.TryGetValue(definitionId, out var zoneDefinition))
            return false;

        if (zoneDefinition is not StartingZoneDefinition startingZoneDefinition)
            return false;

        zone = new StartingZone(startingZoneDefinition, _serviceProvider)
        {
            Id = _uniqueId++
        };

        zone.OnStart();

        return _zones.TryAdd(zone.Id, zone);
    }
}