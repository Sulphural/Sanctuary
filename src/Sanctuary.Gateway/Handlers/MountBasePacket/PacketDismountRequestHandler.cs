using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketDismountRequestHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketDismountRequestHandler));
    }

    public static bool HandlePacket(GatewayConnection connection)
    {
        _logger.LogTrace("Received {name} packet.", nameof(PacketDismountRequest));

        // If the player is in a transformation, dismount removes the transform instead.
        if (connection.Player.TemporaryAppearance != 0)
        {
            AbilityPacketClientRequestStartAbilityHandler.RemoveTransform(connection);
            return true;
        }

        // Body moved to Player.Dismount by upstream 455be39 so collection-node interactions can
        // dismount too; it is the same packets, stats and effect ids this handler used to send inline.
        connection.Player.Dismount();
        return true;
    }
}
