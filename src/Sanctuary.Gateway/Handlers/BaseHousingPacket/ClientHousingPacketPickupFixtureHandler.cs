using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketPickupFixtureHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketPickupFixtureHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!TryParseFixtureGuid(data, ClientHousingPacketPickupFixture.OpCode, out var fixtureGuid))
        {
            _logger.LogError(
                "Failed to deserialize {packet}. Length={length} Data={data}",
                nameof(ClientHousingPacketPickupFixture),
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouse(connection, out var houseId))
            return true;

        var requestedGuid = fixtureGuid;
        fixtureGuid = HousingFixtureActorService.ResolveClientFixtureGuid(
            connection.Player.Guid,
            houseId,
            requestedGuid);

        if (HousingPlacementSession.TryTake(connection.Player.Guid, houseId, fixtureGuid, out _))
        {
            HousingFixtureActorService.Remove(connection.Player, houseId, fixtureGuid);
            connection.SendTunneled(new HousingPacketRemoveFixture
            {
                FixtureGuid = fixtureGuid
            });

            _logger.LogInformation(
                "Player {PlayerGuid} cancelled pending fixture {FixtureGuid} selected as {RequestedGuid} in house {HouseId}.",
                connection.Player.Guid,
                fixtureGuid,
                requestedGuid,
                houseId);
            return true;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var fixtureId = HousingFixtureActorService.ResolveDatabaseFixtureId(
            connection.Player.Guid,
            houseId,
            fixtureGuid);

        var dbFixture = dbContext.HouseFixtures
            .Include(f => f.House)
            .FirstOrDefault(f =>
                f.Id == fixtureId &&
                f.HouseId == houseId &&
                f.House.CharacterId == characterId);

        if (dbFixture is null)
        {
            _logger.LogWarning("Fixture {FixtureGuid} not found for player {PlayerGuid} in house {HouseId}.", fixtureGuid, connection.Player.Guid, houseId);
            return true;
        }

        var itemDefinitionId = dbFixture.ItemDefinitionId;
        var tintId = HouseOwnershipService.GetFixtureTintId(dbFixture);
        if (tintId == 0)
            tintId = HousingFixtureActorService.GetTintId(connection.Player, houseId, fixtureGuid);
        tintId = HouseOwnershipService.ResolveItemTintId(_resourceManager, itemDefinitionId, tintId);
        var inventoryItem = AddOrIncrementInventoryItem(
            dbContext,
            characterId,
            itemDefinitionId,
            tintId,
            out var inventoryRowCreated);

        dbContext.HouseFixtures.Remove(dbFixture);
        dbContext.SaveChanges();

        UpdateReturnedInventoryItem(connection, inventoryItem);
        HousingFixtureActorService.OnFixtureRemoved(connection.Player, houseId, dbFixture.Id);

        foreach (var occupant in HousingFixtureActorService.GetHouseOccupants(connection.Player))
        {
            var occupantFixtureGuid = HousingFixtureActorService.GetClientFixtureGuid(
                occupant.Guid,
                houseId,
                dbFixture.Id);
            occupant.SendTunneled(new HousingPacketRemoveFixture
            {
                FixtureGuid = occupantFixtureGuid
            });
            HousingFixtureActorService.Remove(occupant, houseId, occupantFixtureGuid);
        }

        // Reload without tracked navigation state so the outgoing count/list
        // cannot retain the just-deleted fixture through EF relationship fixup.
        dbContext.ChangeTracker.Clear();
        var refreshedHouse = dbContext.Houses
            .AsNoTracking()
            .Include(h => h.Fixtures)
            .First(h => h.Id == houseId);
        if (inventoryRowCreated)
            HouseOwnershipService.SendFixtureItemList(connection, refreshedHouse, _resourceManager);
        HouseOwnershipService.SendHouseInfoUpdate(connection, refreshedHouse, inEditMode: true);

        _logger.LogInformation(
            "Player {PlayerGuid} picked up fixture {FixtureId} selected as {RequestedGuid}/{FixtureGuid}; returned item {ItemDefinitionId} tint {TintId} to inventory row {InventoryItemId} (count {InventoryItemCount}) in house {HouseId}.",
            connection.Player.Guid,
            dbFixture.Id,
            requestedGuid,
            fixtureGuid,
            itemDefinitionId,
            tintId,
            inventoryItem.Id,
            inventoryItem.Count,
            houseId);

        return true;
    }

    private static bool TryParseFixtureGuid(ReadOnlySpan<byte> data, short expectedSubOpCode, out ulong fixtureGuid)
    {
        fixtureGuid = 0;

        var reader = new PacketReader(data);
        if (!reader.TryRead(out short opCode) || opCode != BaseHousingPacket.OpCode)
            return false;

        if (!reader.TryRead(out short subOpCode) || subOpCode != expectedSubOpCode)
            return false;

        if (reader.RemainingLength >= sizeof(ulong))
            return reader.TryRead(out fixtureGuid);

        if (reader.TryRead(out int fixtureId))
        {
            fixtureGuid = (ulong)fixtureId;
            return true;
        }

        return false;
    }

    private static DbItem AddOrIncrementInventoryItem(
        DatabaseContext dbContext,
        ulong characterId,
        int itemDefinitionId,
        int tint,
        out bool created)
    {
        var dbItem = dbContext.Items.FirstOrDefault(i =>
            i.CharacterId == characterId &&
            i.Definition == itemDefinitionId &&
            i.Tint == tint);

        if (dbItem is not null)
        {
            created = false;
            dbItem.Count++;
            return dbItem;
        }

        created = true;
        dbItem = new DbItem
        {
            Id = (dbContext.Items
                .Where(i => i.CharacterId == characterId)
                .Select(i => (int?)i.Id)
                .Max() ?? 0) + 1,
            CharacterId = characterId,
            Definition = itemDefinitionId,
            Tint = tint,
            Count = 1
        };

        dbContext.Items.Add(dbItem);
        return dbItem;
    }

    internal static void UpdateReturnedInventoryItem(GatewayConnection connection, DbItem dbItem)
    {
        var clientItem = connection.Player.Items.SingleOrDefault(item => item.Id == dbItem.Id);
        var needsAdd = clientItem is null;

        if (clientItem is null)
        {
            clientItem = new ClientItem
            {
                Id = dbItem.Id,
                Definition = dbItem.Definition,
                Tint = dbItem.Tint,
                Count = dbItem.Count
            };
            connection.Player.Items.Add(clientItem);
        }
        else
        {
            clientItem.Count = dbItem.Count;
        }

        if (!_resourceManager.ClientItemDefinitions.TryGetValue(dbItem.Definition, out var itemDefinition))
        {
            _logger.LogWarning("Returned housing fixture item {ItemDefinitionId} is missing a client item definition.", dbItem.Definition);
            return;
        }

        if (needsAdd)
        {
            using var writer = new PacketWriter();
            clientItem.Serialize(writer);
            itemDefinition.Serialize(writer);

            connection.SendTunneled(new ClientUpdatePacketItemAdd
            {
                Payload = writer.Buffer
            });
            return;
        }

        connection.SendTunneled(new ClientUpdatePacketItemUpdate
        {
            ItemGuid = dbItem.Id,
            Count = dbItem.Count
        });
    }

    private static bool TryGetActiveHouse(GatewayConnection connection, out int houseId)
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
