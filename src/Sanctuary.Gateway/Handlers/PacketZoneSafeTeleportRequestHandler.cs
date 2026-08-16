using System;
using System.Linq;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketZoneSafeTeleportRequestHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IZoneManager _zoneManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketZoneSafeTeleportRequestHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, Span<byte> data)
    {
        if (!PacketZoneSafeTeleportRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketZoneSafeTeleportRequest));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PacketZoneSafeTeleportRequest), packet);

        // "Nearest hub" only means anything measured from an OVERWORLD spot: inside a house or an instance
        // the live position is in that world's own coordinate space, so use the overworld point the player
        // left from instead of whatever the instance happens to call (0,0).
        var safeOrigin = connection.Player.CurrentHouseGuid == 0 && connection.Player.Zone == _zoneManager.StartingZone
            ? connection.Player.Position
            : connection.Player.StartingZonePosition;
        var pointOfInterest = FindNearestSafePointOfInterest(safeOrigin);

        if (pointOfInterest is null)
        {
            _logger.LogWarning("No safe teleport destination found for player {guid}.", connection.Player.Guid);
            return true;
        }

        var rotationX = MathF.Cos(pointOfInterest.Heading);
        var rotationZ = MathF.Sin(pointOfInterest.Heading);

        var position = pointOfInterest.SpawnPosition;
        var rotation = new Quaternion(rotationZ, 0f, rotationX, 0f);

        HousingPlacementSession.TakeAll(connection.Player.Guid);
        HousingFixtureActorService.RemoveAllForPlayer(connection.Player);

        // ★ THIS IS THE CLIENT'S "I'm stuck" BUTTON, i.e. the last way out of anywhere - so it has to be
        // able to leave an INSTANCE, not just move within one. It used to change zones only for a house,
        // and the plain UpdateLocation path below cannot: a player stranded in an arena/dungeon/tutorial
        // instance got shuffled to an overworld POI's coordinates while still inside the instanced world,
        // which rescues nobody (and those coords are meaningless there). Anywhere that is not the starting
        // zone now teleports properly back to it.
        if (connection.Player.CurrentHouseGuid != 0 || connection.Player.Zone != _zoneManager.StartingZone)
        {
            connection.Player.TeleportToZone(_zoneManager.StartingZone, position, rotation);
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

    private static PointOfInterestDefinition? FindNearestSafePointOfInterest(Vector4 playerPosition)
    {
        var hubPointsOfInterest = _resourceManager.PointOfInterests.Values
            .Where(x => x.NotificationType == PointOfInterestNotificationType.ZoneHub)
            .ToList();

        var candidates = hubPointsOfInterest.Count > 0
            ? hubPointsOfInterest
            : _resourceManager.PointOfInterests.Values.ToList();

        PointOfInterestDefinition? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var pointOfInterest in candidates)
        {
            var dx = playerPosition.X - pointOfInterest.Position.X;
            var dz = playerPosition.Z - pointOfInterest.Position.Z;
            var distance = dx * dx + dz * dz;

            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = pointOfInterest;
        }

        return nearest;
    }
}
