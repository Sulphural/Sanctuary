using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// Informational only (see PetUiModePacket.cs) - the client reports its pet-panel UI mode as the
// player interacts with it. No reply expected; logged for visibility in case the mode values turn
// out to matter for something later (e.g. distinguishing "browsing" from "active pet details").
[PacketHandler]
public static class PetUiModePacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PetUiModePacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!PetUiModePacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PetUiModePacket));
            return false;
        }

        _logger.LogDebug("{player}'s pet UI mode changed to {mode}.", connection.Player.Name, packet.Mode);

        return true;
    }
}
