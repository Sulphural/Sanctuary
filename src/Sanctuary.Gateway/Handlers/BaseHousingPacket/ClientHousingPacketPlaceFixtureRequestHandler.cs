using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketPlaceFixtureRequestHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketPlaceFixtureRequestHandler));

        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketPlaceFixtureRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketPlaceFixtureRequest));
            return false;
        }

        if (connection.Player.CurrentHouseGuid == 0)
        {
            _logger.LogWarning("Player {name} tried to place furniture but not in a house", 
                connection.Player.Name.FirstName);
            return false;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();

        // Create fixture in database
        var dbFixture = new DbHouseFixture
        {
            HouseId = connection.Player.CurrentHouseGuid,
            ItemDefinitionId = packet.ItemDefinitionId,
            PositionX = packet.Position.X,
            PositionY = packet.Position.Y,
            PositionZ = packet.Position.Z,
            PositionW = packet.Position.W,
            RotationX = packet.Rotation.X,
            RotationY = packet.Rotation.Y,
            RotationZ = packet.Rotation.Z,
            RotationW = packet.Rotation.W,
            Scale = packet.Scale,
            CustomizationData = null,
            Created = DateTimeOffset.UtcNow
        };

        dbContext.HouseFixtures.Add(dbFixture);
        dbContext.SaveChanges();

        _logger.LogInformation("Player {name} placed furniture item {itemId} at position {pos}", 
            connection.Player.Name.FirstName, packet.ItemDefinitionId, packet.Position);

        // Send confirmation to client
        var response = new HousingPacketPlaceFixture
        {
            FixtureGuid = dbFixture.Id,
            ItemDefinitionId = packet.ItemDefinitionId,
            Position = packet.Position,
            Rotation = packet.Rotation,
            Scale = packet.Scale
        };

        connection.SendTunneled(response);

        // TODO: Remove item from inventory if it was placed from inventory
        // For now furniture items remain in inventory

        return true;
    }
}
