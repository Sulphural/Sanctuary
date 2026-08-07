using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketClientIsReadyHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        _logger = loggerFactory.CreateLogger(nameof(PacketClientIsReadyHandler));
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

        return true;
    }
}