using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketLeaveHouseHandler
{
    public const short OpCode = 10;

    private static ILogger _logger = null!;
    private static IZoneManager _zoneManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketLeaveHouseHandler));
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!TryDeserialize(data))
        {
            _logger.LogError(
                "Failed to deserialize housing leave request. Length={Length} Data={Data}",
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (connection.Player.CurrentHouseGuid == 0)
            return true;

        var houseGuid = connection.Player.CurrentHouseGuid;
        var position = connection.Player.StartingZonePosition == default
            ? _zoneManager.StartingZone.SpawnPosition
            : connection.Player.StartingZonePosition;
        var rotation = connection.Player.StartingZoneRotation == default
            ? _zoneManager.StartingZone.SpawnRotation
            : connection.Player.StartingZoneRotation;
        HousingPlacementSession.TakeAll(connection.Player.Guid);
        HousingFixtureActorService.RemoveAllForPlayer(connection.Player);

        connection.Player.TeleportToZone(_zoneManager.StartingZone, position, rotation);
        connection.Player.CurrentHouseGuid = 0;

        _logger.LogInformation(
            "Player {PlayerGuid} left house {HouseGuid} and zoned back to the world at {Position}.",
            connection.Player.Guid,
            houseGuid,
            position);

        return true;
    }

    private static bool TryDeserialize(ReadOnlySpan<byte> data)
    {
        var reader = new PacketReader(data);
        return reader.TryRead(out short baseOpCode) &&
            baseOpCode == BaseHousingPacket.OpCode &&
            reader.TryRead(out short subOpCode) &&
            subOpCode == OpCode &&
            reader.RemainingLength == 0;
    }
}
