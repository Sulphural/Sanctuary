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
public static class ClientHousingPacketRequestGrantHandler
{
    public const short OpCode = 23;

    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketRequestGrantHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!TryDeserialize(data))
        {
            _logger.LogError(
                "Failed to deserialize ClientHousingPacketRequestGrant. Length={Length} Data={Data}",
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouse(connection, out var houseId))
            return true;

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var house = dbContext.Houses
            .Include(h => h.Fixtures)
            .FirstOrDefault(h => h.Id == houseId && h.CharacterId == characterId);

        if (house is null)
        {
            _logger.LogWarning(
                "Player {PlayerGuid} requested housing grant for non-owned house {HouseId}.",
                connection.Player.Guid,
                houseId);
            return true;
        }

        var wasInEditMode = HousingFixtureActorService.IsInEditMode(connection.Player);
        HousingFixtureActorService.SetEditMode(connection.Player, inEditMode: true);
        HouseOwnershipService.SendHouseGrantData(connection, house, _resourceManager, inEditMode: true);
        var replayedFixtureCount = !wasInEditMode
            ? HousingFixtureActorService.ReplayPersistedFixtureUpdates(
                connection.Player,
                house,
                _resourceManager)
            : 0;

        _logger.LogInformation(
            "Player {PlayerGuid} received housing edit grant for house {HouseId}; rebuilt {FixtureCount} fixture links.",
            connection.Player.Guid,
            house.Id,
            replayedFixtureCount);

        return true;
    }

    private static bool TryDeserialize(ReadOnlySpan<byte> data)
    {
        var reader = new PacketReader(data);
        if (!reader.TryRead(out short opCode) || opCode != BaseHousingPacket.OpCode)
            return false;

        if (!reader.TryRead(out short subOpCode) || subOpCode != OpCode)
            return false;

        return reader.RemainingLength == 0;
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
