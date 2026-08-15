using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketRemoveCustomizationFromFixtureGroupAndTypeHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketRemoveCustomizationFromFixtureGroupAndTypeHandler));
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketRemoveCustomizationFromFixtureGroupAndType.TryDeserialize(data, out var packet))
        {
            _logger.LogError(
                "Failed to deserialize {Packet}. Length={Length} Data={Data}",
                nameof(ClientHousingPacketRemoveCustomizationFromFixtureGroupAndType),
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouseId(connection, out var houseId))
            return true;

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var house = dbContext.Houses.FirstOrDefault(candidate =>
            candidate.Id == houseId && candidate.CharacterId == characterId);
        if (house is null)
            return true;

        if (HouseOwnershipService.RemoveHouseCustomization(
            house,
            packet.FixtureGroup,
            packet.FixtureType))
        {
            dbContext.SaveChanges();
        }

        _logger.LogInformation(
            "Player {PlayerGuid} requested housing customization remove. HouseId={HouseId} Group='{FixtureGroup}' Type='{FixtureType}'.",
            connection.Player.Guid,
            houseId,
            packet.FixtureGroup,
            packet.FixtureType);

        return true;
    }

    private static bool TryGetActiveHouseId(GatewayConnection connection, out int houseId)
    {
        houseId = 0;

        if (connection.Player.CurrentHouseGuid == 0)
            return false;

        try
        {
            var id = GuidHelper.GetHouseId(connection.Player.CurrentHouseGuid);
            if (id > int.MaxValue)
                return false;

            houseId = (int)id;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
