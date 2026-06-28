using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketPetDismountHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketPetDismountHandler));
    }

    public static bool HandlePacket(GatewayConnection connection)
    {
        _logger.LogTrace("Received {name} packet.", nameof(PacketPetDismount));

        if (connection.Player.Pet is null)
            return true;

        var petDismountResponsePacket = new PetDismountResponsePacket();

        petDismountResponsePacket.OwnerGuid = connection.Player.Guid;
        petDismountResponsePacket.CompositeEffectId = 0;

        connection.Player.SendTunneledToVisible(petDismountResponsePacket, true);

        connection.Player.Pet.Dispose();
        connection.Player.Pet = null;

        return true;
    }
}
