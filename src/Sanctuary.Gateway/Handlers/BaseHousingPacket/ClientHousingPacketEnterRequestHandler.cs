using System;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;
using Sanctuary.UdpLibrary.Enumerations;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketEnterRequestHandler
{
    private static readonly TimeSpan[] ZoningCompletionRetryDelays =
    [
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(2500),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(10)
    ];

    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketEnterRequestHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketEnterRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketEnterRequest));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(ClientHousingPacketEnterRequest), packet);

        TryEnterHouse(
            connection,
            packet.HouseInstanceGuid,
            allowDefaultFallback: true,
            reason: "housing-enter");

        return true;
    }

    public static bool TryEnterHouse(
        GatewayConnection connection,
        ulong houseInstanceGuid,
        bool allowDefaultFallback,
        string reason)
    {

        using var dbContext = _dbContextFactory.CreateDbContext();

        var playerId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var house = HouseOwnershipService.TryGetHouse(dbContext, houseInstanceGuid);

        if (house is null && allowDefaultFallback)
            house = HouseOwnershipService.GetOrCreateDefaultHouse(dbContext, connection.Player, _resourceManager);

        if (house is null)
            return false;

        var targetHouseGuid = GuidHelper.GetHouseGuid((ulong)house.Id);
        var isOwner = house.CharacterId == playerId;
        var isSameHouse = connection.Player.CurrentHouseGuid == targetHouseGuid;

        if (isSameHouse)
        {
            if (!connection.Player.Visible)
            {
                HouseOwnershipService.CompleteHouseZoning(connection);
            }

            _logger.LogDebug(
                "Ignored duplicate housing enter request for player {PlayerGuid} and house {HouseGuid} (reason={Reason}, visible={Visible}).",
                connection.Player.Guid,
                targetHouseGuid,
                reason,
                connection.Player.Visible);
            return true;
        }

        if (connection.Player.CurrentHouseGuid != 0)
        {
            HousingPlacementSession.TakeAll(connection.Player.Guid);
            HousingFixtureActorService.RemoveAllForPlayer(connection.Player);
        }

        house.LastVisited = DateTimeOffset.UtcNow;
        dbContext.SaveChanges();

        if (connection.Player.Mount is not null)
            PacketDismountRequestHandler.HandlePacket(connection);

        var zoningInfo = HouseOwnershipService.TeleportToHouse(connection, house, _resourceManager);
        _logger.LogInformation(
            "Entering house {HouseId} owned by character {OwnerCharacterId} for {PlayerName} (owner={IsOwner}, reason={Reason}). Definition={HouseDefinitionId}, Zone={RequestedZoneId}->{PacketZoneId}, ZoneName={ZoneName}, Position={Position}.",
            house.Id,
            house.CharacterId,
            connection.Player.Name.FullName,
            isOwner,
            reason,
            zoningInfo.HouseDefinitionId,
            zoningInfo.RequestedZoneId,
            zoningInfo.PacketZoneId,
            zoningInfo.ZoneName,
            zoningInfo.Position);

        HouseOwnershipService.SendHouseData(connection, house, _resourceManager);
        HouseOwnershipService.CompleteHouseZoning(connection);
        QueueHouseZoningCompletionRetry(connection);

        return true;
    }

    private static void QueueHouseZoningCompletionRetry(GatewayConnection connection)
    {
        var houseGuid = connection.Player.CurrentHouseGuid;

        if (houseGuid == 0)
            return;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                foreach (var delay in ZoningCompletionRetryDelays)
                {
                    await System.Threading.Tasks.Task.Delay(delay);
                    if (connection.Status != Status.Connected ||
                        connection.Player.CurrentHouseGuid != houseGuid ||
                        connection.Player.Visible)
                        return;

                    HouseOwnershipService.CompleteHouseZoning(connection);
                }

                if (connection.Status == Status.Connected &&
                    connection.Player.CurrentHouseGuid == houseGuid &&
                    !connection.Player.Visible)
                {
                    connection.Player.Visible = true;
                    connection.Player.UpdatePosition(connection.Player.Position, connection.Player.Rotation);

                    _logger.LogWarning(
                        "Client did not finish loading house {HouseGuid} for {PlayerName} after the zoning completion retry window. Restored player visibility.",
                        houseGuid,
                        connection.Player.Name.FullName);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "House zoning completion retry stopped for house {HouseGuid}.", houseGuid);
            }
        });
    }
}
