using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketApplyCustomizationToFixtureGroupAndTypeHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketApplyCustomizationToFixtureGroupAndTypeHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketApplyCustomizationToFixtureGroupAndType.TryDeserialize(data, out var packet))
        {
            _logger.LogError(
                "Failed to deserialize {Packet}. Length={Length} Data={Data}",
                nameof(ClientHousingPacketApplyCustomizationToFixtureGroupAndType),
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouseId(connection, out var houseId))
            return true;

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var ownedItem = dbContext.Items.FirstOrDefault(item =>
            item.CharacterId == characterId &&
            item.Id == packet.ItemDefinitionId &&
            item.Count > 0);

        // Retail sends the owned inventory record/coupling id here. Keep a
        // definition-id fallback for older clients and captured test packets.
        ownedItem ??= dbContext.Items.FirstOrDefault(item =>
            item.CharacterId == characterId &&
            item.Definition == packet.ItemDefinitionId &&
            item.Count > 0);

        if (ownedItem is null ||
            !_resourceManager.ClientItemDefinitions.TryGetValue(ownedItem.Definition, out var itemDefinition) ||
            !HousingPlacementCatalog.IsFixtureCustomization(itemDefinition))
        {
            _logger.LogWarning(
                "Player {PlayerGuid} tried to apply invalid or unowned customization record {ItemRecordId} to group '{FixtureGroup}' type '{FixtureType}' in house {HouseId}.",
                connection.Player.Guid,
                packet.ItemDefinitionId,
                packet.FixtureGroup,
                packet.FixtureType,
                houseId);
            return true;
        }

        var house = dbContext.Houses
            .Include(candidate => candidate.Fixtures)
            .FirstOrDefault(candidate =>
                candidate.Id == houseId && candidate.CharacterId == characterId);
        if (house is null ||
            !HouseOwnershipService.ApplyHouseCustomization(
                house,
                packet.FixtureGroup,
                packet.FixtureType,
                ownedItem.Definition,
                ownedItem.Tint))
        {
            _logger.LogWarning(
                "Player {PlayerGuid} could not apply housing customization record {ItemRecordId} in house {HouseId}.",
                connection.Player.Guid,
                packet.ItemDefinitionId,
                houseId);
            return true;
        }

        var sourceItemId = ownedItem.Id;
        var sourceItemCount = ownedItem.Count - 1;
        var itemDefinitionId = ownedItem.Definition;
        var tintId = ownedItem.Tint;

        if (sourceItemCount <= 0)
            dbContext.Items.Remove(ownedItem);
        else
            ownedItem.Count = sourceItemCount;

        dbContext.SaveChanges();
        UpdateConsumedInventoryItem(connection, sourceItemId, sourceItemCount);

        foreach (var recipient in HousingFixtureActorService.GetHouseOccupants(connection.Player))
        {
            HouseOwnershipService.SendHouseCustomization(
                recipient,
                house,
                packet.FixtureGroup,
                packet.FixtureType,
                itemDefinitionId,
                tintId,
                _resourceManager);
        }

        HouseOwnershipService.SendFixtureItemList(connection, house, _resourceManager);

        _logger.LogInformation(
            "Player {PlayerGuid} requested housing customization apply. HouseId={HouseId} ItemDefinitionId={ItemDefinitionId} ItemRecordId={ItemRecordId} CategoryId={CategoryId} Group='{FixtureGroup}' Type='{FixtureType}'.",
            connection.Player.Guid,
            houseId,
            itemDefinitionId,
            sourceItemId,
            itemDefinition.CategoryId,
            packet.FixtureGroup,
            packet.FixtureType);

        return true;
    }

    private static void UpdateConsumedInventoryItem(
        GatewayConnection connection,
        int itemRecordId,
        int newCount)
    {
        var clientItem = connection.Player.Items.SingleOrDefault(item => item.Id == itemRecordId);

        if (newCount <= 0)
        {
            if (clientItem is not null)
                connection.Player.Items.Remove(clientItem);

            connection.SendTunneled(new ClientUpdatePacketItemDelete
            {
                ItemGuid = itemRecordId
            });
            return;
        }

        if (clientItem is not null)
            clientItem.Count = newCount;

        connection.SendTunneled(new ClientUpdatePacketItemUpdate
        {
            ItemGuid = itemRecordId,
            Count = newCount
        });
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
