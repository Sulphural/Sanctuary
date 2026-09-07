using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game.Quests;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// Reply to a CommandPacketShowDialog click with CommandPacketEndDialog (not CommandPacketQuestDialogComplete, which targets the quest start/end screen and would leave HUD/movement locked here).
[PacketHandler]
public static class PacketDialogResponseHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketDialogResponseHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!PacketDialogResponse.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketDialogResponse));
            return false;
        }

        // A multi-turn conversation plays its next line here; the teardown is held back until the
        // exchange actually runs out of turns, or the dialog would close after the first bubble.
        if (QuestDialogue.TryAdvance(connection.Player))
            return true;

        connection.Player.SendTunneled(new CommandPacketEndDialog());
        return true;
    }
}
