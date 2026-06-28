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
public static class ClientHousingPacketSaveFixtureHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketSaveFixtureHandler));

        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketSaveFixture.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketSaveFixture));
            return false;
        }

        if (connection.Player.CurrentHouseGuid == 0)
        {
            _logger.LogWarning("Player {name} tried to save furniture but not in a house", 
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

        // Update position, rotation, and scale
        dbFixture.PositionX = packet.Position.X;
        dbFixture.PositionY = packet.Position.Y;
        dbFixture.PositionZ = packet.Position.Z;
        dbFixture.PositionW = packet.Position.W;
        dbFixture.RotationX = packet.Rotation.X;
        dbFixture.RotationY = packet.Rotation.Y;
        dbFixture.RotationZ = packet.Rotation.Z;
        dbFixture.RotationW = packet.Rotation.W;
        dbFixture.Scale = packet.Scale;

        dbContext.SaveChanges();

        _logger.LogInformation("Player {name} moved furniture fixture {guid} to position {pos}", 
            connection.Player.Name.FirstName, packet.FixtureGuid, packet.Position);

        // Client doesn't need a response for this, it already moved the object visually

        return true;
    }
}
