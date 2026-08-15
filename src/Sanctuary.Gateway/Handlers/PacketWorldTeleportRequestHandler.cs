using System;
using System.Linq;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketWorldTeleportRequestHandler
{
    private static ILogger _logger = null!;
    private static IZoneManager _zoneManager = null!;
    private static IResourceManager _resourceManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketWorldTeleportRequestHandler));

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    internal static bool IsHouseTeleportTargetReady(Player player)
    {
        return player.CurrentHouseGuid != 0 && player.Visible;
    }

    // op58 carries a guid that is EITHER another player (the friends-list "teleport to friend" flow) or a
    // point-of-interest id (clicking a marker on the atlas map). They are disjoint id spaces, so resolve a
    // player first and fall through to the POI lookup when that misses - the POI case used to land on the
    // player-lookup miss and silently no-op, which is why the atlas appeared dead.
    public static bool HandlePacket(GatewayConnection connection, Span<byte> data)
    {
        if (!PacketWorldTeleportRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}. ( Raw: {raw} )",
                nameof(PacketWorldTeleportRequest), Convert.ToHexString(data));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PacketWorldTeleportRequest), packet);

        if (_zoneManager.TryGetPlayer(packet.Guid, out var player))
            return TeleportToPlayer(connection, player);

        return TeleportToPointOfInterest(connection, packet.Guid, data);
    }

    private static bool TeleportToPlayer(GatewayConnection connection, Player player)
    {
        var position = player.Position;
        var rotation = player.Rotation;

        if (player.CurrentHouseGuid != 0)
        {
            // The house GUID is assigned before the target finishes loading.
            // Following during that window starts a second transition against
            // an instance that is not ready and can crash the joining client.
            if (!IsHouseTeleportTargetReady(player))
            {
                _logger.LogWarning(
                    "Rejected friend teleport to {TargetName} because the target is still loading house {HouseGuid}.",
                    player.Name.FullName,
                    player.CurrentHouseGuid);
                return true;
            }

            if (ClientHousingPacketEnterRequestHandler.TryEnterHouse(
                    connection,
                    player.CurrentHouseGuid,
                    allowDefaultFallback: false,
                    reason: "friend-teleport"))
            {
                _logger.LogInformation(
                    "Player {PlayerGuid} entered house {HouseGuid} by teleporting to {TargetName}.",
                    connection.Player.Guid,
                    player.CurrentHouseGuid,
                    player.Name.FullName);
                return true;
            }

            _logger.LogWarning(
                "Rejected friend teleport to {TargetName} because house {HouseGuid} could not be resolved.",
                player.Name.FullName,
                player.CurrentHouseGuid);
            return true;
        }

        HousingPlacementSession.TakeAll(connection.Player.Guid);
        HousingFixtureActorService.RemoveAllForPlayer(connection.Player);

        if (connection.Player.CurrentHouseGuid != 0 || connection.Player.Zone != player.Zone)
        {
            connection.Player.TeleportToZone(player.Zone, position, rotation);
            connection.Player.CurrentHouseGuid = 0;
        }
        else
        {
            connection.Player.UpdatePosition(position, rotation, updateZoneArea: false);
            connection.SendTunneled(new ClientUpdatePacketUpdateLocation
            {
                Position = position,
                Rotation = rotation,
                Teleport = true
            });
        }

        return true;
    }

    // ATLAS FAST-TRAVEL: clicking a marker on the atlas map (town waypoint = NotificationType 7,
    // dungeon = 3) sends this op58 with the POI's id. The id the client sends can be the POI's LocationId
    // or its TeleportLocationId depending on marker type, so match against both (then the row Id as a last
    // resort). Log the raw id so we can confirm the exact field the atlas uses.
    private static bool TeleportToPointOfInterest(GatewayConnection connection, ulong id, Span<byte> data)
    {
        var player = connection.Player;

        _logger.LogInformation("WorldTeleportRequest: id={id} raw={raw}", id, Convert.ToHexString(data));

        var poi = _resourceManager.PointOfInterests.Values.FirstOrDefault(p =>
            (ulong)p.LocationId == id || (ulong)p.TeleportLocationId == id || (ulong)p.Id == id);

        if (poi is null)
        {
            _logger.LogWarning("WorldTeleportRequest: no POI matched id {id} — no teleport.", id);
            return true;
        }

        var target = poi.SpawnPosition != default ? poi.SpawnPosition : poi.Position;
        var rotation = new Quaternion(MathF.Sin(poi.Heading), 0f, MathF.Cos(poi.Heading), 0f);

        // Fast-travelling out of a house has to tear the instance down first, exactly as the atlas exit
        // and the zone-teleport handler do, or the fixtures follow the player into the overworld.
        HousingPlacementSession.TakeAll(player.Guid);
        HousingFixtureActorService.RemoveAllForPlayer(player);

        // Fast-travel across the streamed overworld with a PROPER same-world re-entry (the exact recipe the
        // arena exit door uses to drop players back into the overworld: TeleportToZone with sky=null,
        // geometryId=0). A bare UpdateLocation teleport left the client stuck in an incomplete load state
        // (frozen, no HUD, can't move — masked by the atlas until it was closed); the BeginZoning re-entry
        // runs the full load handshake, which streams the destination and restores the HUD/input. The
        // dungeon entrance we placed on this exact POI spot is right where the player lands.
        player.TeleportToZone(player.Zone, target, rotation, sky: null, geometryId: 0);
        player.CurrentHouseGuid = 0;

        _logger.LogInformation("WorldTeleportRequest: teleported {player} to POI id={id} (name {name}, atlas {atlas}) at {pos}.",
            player.Guid, poi.Id, poi.NameId, poi.AtlasName, target);

        return true;
    }
}
