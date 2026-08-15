using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketToggleLockedHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketToggleLockedHandler));

        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketToggleLocked.TryDeserialize(data, out _))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketToggleLocked));
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

        dbHouse.IsLocked = !dbHouse.IsLocked;
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

        _logger.LogInformation("Player {name} toggled house lock to {value}", connection.Player.Name.FirstName, dbHouse.IsLocked);

        return true;
    }
}
