using System;
using System.Collections.Generic;
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
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketPickupAllFixturesHandler
{
    public const short OpCode = 4;

    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    private readonly record struct FixtureReturn(
        DbHouseFixture Fixture,
        ulong SourceFixtureGuid,
        int TintId);

    private readonly record struct InventoryKey(int ItemDefinitionId, int TintId);

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketPickupAllFixturesHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!TryDeserialize(data))
        {
            _logger.LogError(
                "Failed to deserialize ClientHousingPacketPickupAllFixtures. Length={Length} Data={Data}",
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouse(connection, out var houseId))
            return true;

        _logger.LogInformation(
            "Player {PlayerGuid} requested pickup-all in house {HouseId}.",
            connection.Player.Guid,
            houseId);

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var house = dbContext.Houses
            .Include(h => h.Fixtures)
            .FirstOrDefault(h => h.Id == houseId && h.CharacterId == characterId);

        if (house is null)
        {
            _logger.LogWarning(
                "Player {PlayerGuid} tried to pick up all fixtures in non-owned house {HouseId}.",
                connection.Player.Guid,
                houseId);
            return true;
        }

        foreach (var pending in HousingPlacementSession.TakeAll(connection.Player.Guid, houseId))
        {
            connection.SendTunneled(new HousingPacketRemoveFixture
            {
                FixtureGuid = pending.FixtureGuid
            });
            HousingFixtureActorService.Remove(connection.Player, houseId, pending.FixtureGuid);
        }

        var returns = house.Fixtures
            .OrderBy(fixture => fixture.Id)
            .Select(fixture =>
            {
                var fixtureGuid = HousingFixtureActorService.GetClientFixtureGuid(
                    connection.Player.Guid,
                    houseId,
                    fixture.Id);
                var tintId = HouseOwnershipService.GetFixtureTintId(fixture);
                if (tintId == 0)
                    tintId = HousingFixtureActorService.GetTintId(connection.Player, houseId, fixtureGuid);
                tintId = HouseOwnershipService.ResolveItemTintId(
                    _resourceManager,
                    fixture.ItemDefinitionId,
                    tintId);

                return new FixtureReturn(fixture, fixtureGuid, tintId);
            })
            .ToList();

        if (returns.Count == 0)
        {
            HouseOwnershipService.SendHouseInfoUpdate(connection, house, inEditMode: true);
            return true;
        }

        var nextInventoryId = (dbContext.Items
            .Where(item => item.CharacterId == characterId)
            .Select(item => (int?)item.Id)
            .Max() ?? 0) + 1;
        var returnedInventoryItems = new List<DbItem>();

        foreach (var group in returns.GroupBy(entry =>
                     new InventoryKey(entry.Fixture.ItemDefinitionId, entry.TintId)))
        {
            var inventoryItem = dbContext.Items.FirstOrDefault(item =>
                item.CharacterId == characterId &&
                item.Definition == group.Key.ItemDefinitionId &&
                item.Tint == group.Key.TintId);

            if (inventoryItem is null)
            {
                inventoryItem = new DbItem
                {
                    Id = nextInventoryId++,
                    CharacterId = characterId,
                    Definition = group.Key.ItemDefinitionId,
                    Tint = group.Key.TintId,
                    Count = group.Count()
                };
                dbContext.Items.Add(inventoryItem);
            }
            else
            {
                inventoryItem.Count += group.Count();
            }

            returnedInventoryItems.Add(inventoryItem);
        }

        dbContext.HouseFixtures.RemoveRange(returns.Select(entry => entry.Fixture));
        dbContext.SaveChanges();

        foreach (var inventoryItem in returnedInventoryItems)
            ClientHousingPacketPickupFixtureHandler.UpdateReturnedInventoryItem(connection, inventoryItem);

        var occupants = HousingFixtureActorService.GetHouseOccupants(connection.Player);
        foreach (var entry in returns)
        {
            HousingFixtureActorService.OnFixtureRemoved(connection.Player, houseId, entry.Fixture.Id);

            foreach (var occupant in occupants)
            {
                var occupantFixtureGuid = occupant.Guid == connection.Player.Guid
                    ? entry.SourceFixtureGuid
                    : HousingFixtureActorService.GetClientFixtureGuid(
                        occupant.Guid,
                        houseId,
                        entry.Fixture.Id);

                occupant.SendTunneled(new HousingPacketRemoveFixture
                {
                    FixtureGuid = occupantFixtureGuid
                });
                HousingFixtureActorService.Remove(occupant, houseId, occupantFixtureGuid);
            }
        }

        // The original house and its collection were tracked before RemoveRange.
        // Reload clean state so the client receives an actually empty fixture list.
        dbContext.ChangeTracker.Clear();
        var refreshedHouse = dbContext.Houses
            .AsNoTracking()
            .Include(h => h.Fixtures)
            .First(h => h.Id == houseId);
        HouseOwnershipService.SendFixtureItemList(connection, refreshedHouse, _resourceManager);
        HouseOwnershipService.SendHouseInfoUpdate(connection, refreshedHouse, inEditMode: true);

        _logger.LogInformation(
            "Player {PlayerGuid} picked up all {FixtureCount} fixtures in house {HouseId}.",
            connection.Player.Guid,
            returns.Count,
            houseId);

        return true;
    }

    internal static bool TryDeserialize(ReadOnlySpan<byte> data)
    {
        var reader = new PacketReader(data);
        return reader.TryRead(out short opCode) &&
            opCode == BaseHousingPacket.OpCode &&
            reader.TryRead(out short subOpCode) &&
            subOpCode == OpCode &&
            reader.RemainingLength == 0;
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
