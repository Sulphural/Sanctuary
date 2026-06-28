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
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketInGamePurchasePlaceOrderPacketHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketBaseInGamePurchaseHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!PacketInGamePurchasePlaceOrderPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketInGamePurchasePlaceOrderPacket));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PacketInGamePurchasePlaceOrderPacket), packet);

        var packetInGamePurchasePlaceOrderResponse = new PacketInGamePurchasePlaceOrderResponse();

        var orderDetail = packet.Order.Details.FirstOrDefault();

        if (orderDetail is null)
        {
            packetInGamePurchasePlaceOrderResponse.Result = 2;

            connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

            return true;
        }

        if (!_resourceManager.Stores.TryGetValue(orderDetail.StoreId, out var storeDefinition))
        {
            packetInGamePurchasePlaceOrderResponse.Result = 2;

            connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

            return true;
        }

        if (!storeDefinition.Bundles.TryGetValue(orderDetail.StoreBundleId, out var appStoreBundleDefinition))
        {
            packetInGamePurchasePlaceOrderResponse.Result = 2;

            connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

            return true;
        }

        if (!int.TryParse(orderDetail.Tint, out var orderDetailTint))
        {
            packetInGamePurchasePlaceOrderResponse.Result = 2;

            connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

            return true;
        }

        var cost = connection.Player.MembershipStatus == 0
            ? appStoreBundleDefinition.Price
            : appStoreBundleDefinition.MemberDiscount;

        var totalCost = cost * orderDetail.Quantity;

        if (connection.Player.StationCash < totalCost)
        {
            packetInGamePurchasePlaceOrderResponse.Result = 5;

            connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

            return true;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == GuidHelper.GetPlayerId(connection.Player.Guid));

        if (dbCharacter is null)
        {
            packetInGamePurchasePlaceOrderResponse.Result = 2;

            connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

            return true;
        }

        var lastItemId = dbContext.Items.Where(i => i.CharacterId == dbCharacter.Id)
            .Select(i => (int?)i.Id)
            .Max() ?? 0;

        foreach (var bundleEntry in appStoreBundleDefinition.Entries)
        {
            if (!_resourceManager.ClientItemDefinitions.TryGetValue(bundleEntry.MarketingItemId, out var clientItemDefinition))
            {
                packetInGamePurchasePlaceOrderResponse.Result = 2;

                connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                return true;
            }

            if (clientItemDefinition.Type == 1 || clientItemDefinition.Type == 12)
            {
                var totalQuantity = orderDetail.Quantity * bundleEntry.Quantity;

                var dbItem = dbContext.Items.SingleOrDefault(i => i.CharacterId == dbCharacter.Id &&
                    i.Definition == clientItemDefinition.Id && i.Tint == orderDetailTint);

                if (dbItem is not null)
                {
                    dbItem.Count += totalQuantity;
                }
                else
                {
                    dbItem = new DbItem
                    {
                        Id = lastItemId++ + 1,
                        Definition = clientItemDefinition.Id,
                        Tint = orderDetailTint,

                        Count = totalQuantity
                    };

                    dbCharacter.Items.Add(dbItem);
                }

                var clientItem = connection.Player.Items.SingleOrDefault(x => x.Definition == clientItemDefinition.Id && x.Tint == orderDetailTint);

                var addItem = false;

                if (clientItem is not null)
                {
                    clientItem.Count = dbItem.Count;
                }
                else
                {
                    addItem = true;

                    clientItem = new ClientItem
                    {
                        Id = dbItem.Id,
                        Tint = dbItem.Tint,
                        Count = dbItem.Count,
                        Definition = dbItem.Definition
                    };

                    connection.Player.Items.Add(clientItem);
                }

                if (addItem)
                {
                    using var writer = new PacketWriter();

                    clientItem.Serialize(writer);

                    var clientUpdatePacketItemAdd = new ClientUpdatePacketItemAdd();

                    clientUpdatePacketItemAdd.Payload = writer.Buffer;

                    connection.SendTunneled(clientUpdatePacketItemAdd);
                }
                else
                {
                    var clientUpdatePacketItemUpdate = new ClientUpdatePacketItemUpdate
                    {
                        ItemGuid = clientItem.Id,
                        Count = clientItem.Count,
                    };

                    connection.SendTunneled(clientUpdatePacketItemUpdate);
                }
            }
            else if (clientItemDefinition.Type == 19) // Mounts
            {
                if (!_resourceManager.Mounts.TryGetValue(clientItemDefinition.Param1, out var mountDefinition))
                {
                    packetInGamePurchasePlaceOrderResponse.Result = 2;

                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                    return true;
                }

                if (connection.Player.Mounts.Any(x => x.Definition == mountDefinition.Id && x.TintId == orderDetailTint))
                {
                    packetInGamePurchasePlaceOrderResponse.Result = 2;

                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                    return true;
                }

                var lastMountId = dbContext.Mounts.Where(x => x.CharacterId == dbCharacter.Id)
                    .Select(x => (int?)x.Id)
                    .Max() ?? 0;

                var dbMount = new DbMount
                {
                    Id = lastMountId + 1,

                    Tint = orderDetailTint,
                    Definition = mountDefinition.Id,
                    IsUpgraded = mountDefinition.IsUpgradable // TODO: Implement training
                };

                dbCharacter.Mounts.Add(dbMount);

                connection.Player.Mounts.Add(new PacketMountInfo
                {
                    Id = dbMount.Id,
                    Definition = mountDefinition.Id,
                    NameId = mountDefinition.NameId,
                    ImageSetId = mountDefinition.ImageSetId,
                    TintId = orderDetailTint,
                    TintAlias = mountDefinition.TintAlias ?? string.Empty,
                    MembersOnly = mountDefinition.MembersOnly,
                    IsUpgradable = mountDefinition.IsUpgradable,
                    IsUpgraded = dbMount.IsUpgraded
                });

                var packetMountList = new PacketMountList
                {
                    Mounts = connection.Player.Mounts
                };

                connection.Player.SendTunneled(packetMountList);
            }
            else if (clientItemDefinition.Type == 2) // Pets
            {
                _logger.LogInformation("Attempting to purchase pet. Item ID: {ItemId}, Param1 (Pet Def ID): {Param1}", clientItemDefinition.Id, clientItemDefinition.Param1);

                if (!_resourceManager.Pets.TryGetValue(clientItemDefinition.Param1, out var petDefinition))
                {
                    _logger.LogError("Pet definition {PetDefId} not found in Pets.json", clientItemDefinition.Param1);
                    packetInGamePurchasePlaceOrderResponse.Result = 2;

                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                    return true;
                }

                if (connection.Player.Pets.Any(x => x.Definition == petDefinition.Id && x.TintId == orderDetailTint))
                {
                    packetInGamePurchasePlaceOrderResponse.Result = 2;

                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                    return true;
                }

                var lastPetId = dbContext.Pets.Where(x => x.CharacterId == dbCharacter.Id)
                    .Select(x => (int?)x.Id)
                    .Max() ?? 0;

                var dbPet = new DbPet
                {
                    Id = lastPetId + 1,
                    Name = petDefinition.IsNameable ? "New Pet" : string.Empty,
                    Tint = orderDetailTint,
                    Definition = petDefinition.Id,
                    Created = DateTimeOffset.UtcNow,
                    CharacterId = dbCharacter.Id
                };

                dbCharacter.Pets.Add(dbPet);

                if (dbContext.SaveChanges() <= 0)
                {
                    _logger.LogError("Failed to save new pet to database. CharacterId={characterId}, PetDefId={petDefId}", dbCharacter.Id, petDefinition.Id);
                    packetInGamePurchasePlaceOrderResponse.Result = 2;
                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);
                    return true;
                }

                var petInfo = new PacketPetInfo
                {
                    Id = dbPet.Id,
                    Definition = petDefinition.Id,
                    NameId = petDefinition.NameId,
                    ImageSetId = petDefinition.ImageSetId,
                    TintId = orderDetailTint,
                    TintAlias = petDefinition.TintAlias ?? string.Empty,
                    MembersOnly = petDefinition.MembersOnly,
                    IsNameable = petDefinition.IsNameable, // Server-side only
                    IsUpgradable = false, // Match mount structure - pets don't upgrade
                    IsUpgraded = false, // Match mount structure
                    Guid = 0 // Keep at 0 in ClientPcData (like mounts), calculate only when needed for world spawning
                };

                connection.Player.Pets.Add(petInfo);

                _logger.LogInformation("Pet purchased successfully. PetId={petId}, Definition={definition}, NameId={nameId}, ImageSetId={imageSetId}, Guid={guid}, TintId={tintId}, IsNameable={isNameable}",
                    petInfo.Id, petInfo.Definition, petInfo.NameId, petInfo.ImageSetId, petInfo.Guid, petInfo.TintId, petInfo.IsNameable);

                var petListPacket = new PetListPacket { Pets = connection.Player.Pets };
                _logger.LogInformation("Sending PetListPacket after purchase. TotalPetsCount={count}, PacketOpCode={opCode}, SubOpCode={subOpCode}",
                    petListPacket.Pets.Count, PetBasePacket.OpCode, PetListPacket.OpCode);
                connection.Player.SendTunneled(petListPacket);
            }
            else if (clientItemDefinition.Type == 16) // Houses
            {
                _logger.LogInformation("Attempting to purchase house. Item ID: {ItemId}, Param1 (House Def ID): {Param1}", clientItemDefinition.Id, clientItemDefinition.Param1);

                if (!_resourceManager.Houses.TryGetValue(clientItemDefinition.Param1, out var houseDefinition))
                {
                    _logger.LogError("House definition {HouseDefId} not found in Houses.json", clientItemDefinition.Param1);
                    packetInGamePurchasePlaceOrderResponse.Result = 2;

                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                    return true;
                }

                // Check if player already owns this house type
                var existingHouse = dbContext.Houses.FirstOrDefault(h => h.OwnerId == dbCharacter.Id && h.HouseDefinitionId == houseDefinition.Id);
                if (existingHouse != null)
                {
                    _logger.LogWarning("Player {CharacterId} already owns house type {HouseDefId}", dbCharacter.Id, houseDefinition.Id);
                    packetInGamePurchasePlaceOrderResponse.Result = 2;

                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                    return true;
                }

                // Create new house for player
                var dbHouse = new DbHouse
                {
                    OwnerId = dbCharacter.Id,
                    HouseDefinitionId = houseDefinition.Id,
                    NameId = 0,
                    CustomName = null,
                    IsLocked = false,
                    IsMembersOnly = false,
                    IsFloraAllowed = true,
                    PetAutospawn = false,
                    MaxFixtureCount = 200,
                    MaxLandmarkCount = 0,
                    IconId = 33439,
                    Description = null,
                    KeywordList = null,
                    Rating = 0,
                    Votes = 0,
                    Created = DateTimeOffset.UtcNow,
                    LastVisited = DateTimeOffset.UtcNow
                };

                dbContext.Houses.Add(dbHouse);

                if (dbContext.SaveChanges() <= 0)
                {
                    _logger.LogError("Failed to save new house to database. CharacterId={characterId}, HouseDefId={houseDefId}", dbCharacter.Id, houseDefinition.Id);
                    packetInGamePurchasePlaceOrderResponse.Result = 2;
                    connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);
                    return true;
                }

                _logger.LogInformation("House purchased successfully. HouseId={houseId}, Definition={definition}, OwnerId={ownerId}", 
                    dbHouse.Id, dbHouse.HouseDefinitionId, dbHouse.OwnerId);
            }
            else
            {
                // TODO: Implement other item types

                packetInGamePurchasePlaceOrderResponse.Result = 2;

                connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

                return true;
            }
        }

        dbCharacter.StationCash -= totalCost;

        if (dbContext.SaveChanges() <= 0)
        {
            packetInGamePurchasePlaceOrderResponse.Result = 2;

            connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

            return true;
        }

        connection.Player.StationCash = dbCharacter.StationCash;

        packetInGamePurchasePlaceOrderResponse.Result = 1;

        packetInGamePurchasePlaceOrderResponse.OrderTrackingId = packet.Order.OrderTrackingId;
        packetInGamePurchasePlaceOrderResponse.OrderId = packet.Order.OrderTrackingId.ToString();

        packetInGamePurchasePlaceOrderResponse.Total = totalCost;

        connection.SendTunneled(packetInGamePurchasePlaceOrderResponse);

        return true;
    }
}