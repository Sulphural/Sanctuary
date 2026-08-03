using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PetSummonByItemIdPacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PetSummonByItemIdPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!PetSummonByItemIdPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PetSummonByItemIdPacket));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(PetSummonByItemIdPacket), packet);

        var petInfo = connection.Player.Pets.SingleOrDefault(x =>
            x.Definition == packet.ItemRecord.Definition && x.TintId == packet.ItemRecord.Tint);

        if (petInfo is null)
        {
            _logger.LogWarning("User tried to summon a pet by item record that doesn't match any owned pet. Definition={definition} Tint={tint}",
                packet.ItemRecord.Definition, packet.ItemRecord.Tint);
            return true;
        }

        // Same real-world action as the pet panel's summon/recall button - reuse the exact toggle
        // logic (including the render-fix and async-load retry already applied there).
        PetSummonRecallPacketHandler.ToggleSummon(connection, petInfo);

        return true;
    }
}
