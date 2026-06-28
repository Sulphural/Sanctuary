using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Database;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketPickupFixtureHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketPickupFixtureHandler));

        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketPickupFixture.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketPickupFixture));
            return false;
        }

        if (connection.Player.CurrentHouseGuid == 0)
        {
            _logger.LogWarning("Player {name} tried to pickup furniture but not in a house", 
                connection.Player.Name.FirstName);
            return false;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();

        // Find the fixture
        var dbFixture = dbContext.HouseFixtures
            .FirstOrDefault(f => f.Id == packet.FixtureGuid && f.HouseId == connection.Player.CurrentHouseGuid);

        if (dbFixture == null)
        {
            _logger.LogWarning("Fixture {guid} not found for player {name}", 
                packet.FixtureGuid, connection.Player.Name.FirstName);
            return false;
        }

        // Delete the fixture
        dbContext.HouseFixtures.Remove(dbFixture);
        dbContext.SaveChanges();

        _logger.LogInformation("Player {name} removed furniture fixture {guid} (item {itemId})", 
            connection.Player.Name.FirstName, packet.FixtureGuid, dbFixture.ItemDefinitionId);

        // Send removal packet to client
        var response = new HousingPacketRemoveFixture
        {
            FixtureGuid = packet.FixtureGuid
        };

        connection.SendTunneled(response);

        // TODO: Add item back to player's inventory
        // For now the item is just deleted

        return true;
    }
}
