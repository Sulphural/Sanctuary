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
public static class ClientHousingPacketTogglePetAutospawnHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketTogglePetAutospawnHandler));

        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketTogglePetAutospawn.TryDeserialize(data, out _))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketTogglePetAutospawn));
            return false;
        }

        if (connection.Player.CurrentHouseGuid == 0)
            return false;

        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbHouse = dbContext.Houses
            .Include(h => h.Fixtures)
            .FirstOrDefault(h => (ulong)h.Id == connection.Player.CurrentHouseGuid);

        if (dbHouse == null)
            return false;

        dbHouse.PetAutospawn = !dbHouse.PetAutospawn;
        dbContext.SaveChanges();

        connection.SendTunneled(new HousingPacketUpdateHouseInfo
        {
            IsLocked = dbHouse.IsLocked,
            IsFloraAllowed = dbHouse.IsFloraAllowed,
            PetAutospawn = dbHouse.PetAutospawn,
            CurFixtureCount = dbHouse.Fixtures.Count,
            CurLandmarkCount = 0,
            FurnitureScore = 0
        });

        _logger.LogInformation("Player {name} toggled pet autospawn to {value}", connection.Player.Name.FirstName, dbHouse.PetAutospawn);

        return true;
    }
}
