using System;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;

using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;

namespace Sanctuary.Game.Zones;

// DEBUG world loader: a generic instanced zone that loads ANY client world by name at a caller-supplied
// spawn. Used by the "!map <world> <x> <y> <z>" command so worlds can be browsed/identified in-game
// (e.g. finding the exact combat-tutorial castle) without hardcoding each one. Not part of normal play.
public sealed class DebugWorldZone : BaseZone
{
    private sealed class DebugWorldDefinition : BaseZoneDefinition
    {
    }

    private readonly IResourceManager _resourceManager;

    public DebugWorldZone(string worldName, Vector4 spawn, IServiceProvider serviceProvider)
        : base(CreateDefinition(worldName, spawn), serviceProvider)
    {
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    private static BaseZoneDefinition CreateDefinition(string worldName, Vector4 spawn) => new DebugWorldDefinition
    {
        Name = worldName,
        TileSize = 64,
        // Wide tile bounds so any world's playable area falls inside the tile grid.
        StartLongitude = -32,
        EndLongitude = 32,
        StartLatitude = -32,
        EndLatitude = 32,
        Sky = null,
        SpawnPosition = spawn,
        SpawnRotation = Quaternion.Identity,
    };

    public override void OnClientIsReady(Player player)
    {
        player.RecalculateStats(refill: true);
        player.SendTunneled(new PacketZoneDoneSendingInitialData());
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());
        JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager);
    }
}
