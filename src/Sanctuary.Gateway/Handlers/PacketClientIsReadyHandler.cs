using System;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;
using Sanctuary.UdpLibrary.Enumerations;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketClientIsReadyHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        _logger = loggerFactory.CreateLogger(nameof(PacketClientIsReadyHandler));
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection)
    {
        _logger.LogTrace("Received {name} packet.", nameof(PacketClientIsReady));

        connection.Player.Zone.OnClientIsReady(connection.Player);

        // Publish today's daily-wheel spins, which is what un-greys Spin For The Win's Play button in the
        // minigames menu (see DailyWheelGame.SendSpinAvailability). It goes HERE, not in the login burst
        // next to the pet/housing lists: sent that early it crashed the client outright (2026-08-06), the
        // same packet being harmless once the player is in the world.
        DailyWheelGame.SendSpinAvailability(connection);

        ScheduleHousingSync(connection, TimeSpan.FromMilliseconds(400), sendFixtureReplay: false);
        ScheduleHousingSync(connection, TimeSpan.FromMilliseconds(650), sendFixtureReplay: true);

        return true;
    }

    private static void ScheduleHousingSync(
        GatewayConnection connection,
        TimeSpan delay,
        bool sendFixtureReplay)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);

            if (connection.Status != Status.Connected)
                return;

            try
            {
                using var dbContext = _dbContextFactory.CreateDbContext();

                if (!sendFixtureReplay)
                {
                    HouseOwnershipService.SendHouseList(connection, dbContext, _resourceManager);
                    return;
                }

                HousingFixtureActorService.ResendActors(connection.Player);

                var houseGuid = connection.Player.CurrentHouseGuid;
                if (houseGuid == 0 || HousingFixtureActorService.IsInEditMode(connection.Player))
                    return;

                var house = HouseOwnershipService.TryGetHouse(dbContext, houseGuid);
                if (house is not null && connection.Player.CurrentHouseGuid == houseGuid)
                {
                    HousingFixtureActorService.ReplayPersistedFixtureUpdates(
                        connection.Player,
                        house,
                        _resourceManager);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to send delayed housing client-ready state.");
            }
        });
    }
}
