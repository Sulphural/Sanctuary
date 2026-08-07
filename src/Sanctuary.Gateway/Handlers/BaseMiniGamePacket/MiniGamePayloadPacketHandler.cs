using System;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// op39/sub14 - the minigame PAYLOAD channel: the text pipe between the server and whatever Flash
// microgame the client currently has open. The body is one tab-delimited, null-terminated message
// ("<MsgName>\t<arg>\t<arg>..."), which the game's SoeNetworkTypeFreeRealms splits back apart and
// dispatches to the handler of that name. StateId carries the game the message belongs to.
[PacketHandler]
public static class MiniGamePayloadPacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(MiniGamePayloadPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!MiniGamePayloadPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(MiniGamePayloadPacket));
            return false;
        }

        var message = Encoding.UTF8.GetString(packet.Payload).TrimEnd('\0');

        _logger.LogInformation("MiniGame payload C2S (state {state}): {message}", packet.StateId, message);

        // Mining Practice
        if (packet.StateId == 1113)
        {
            if (message.Split('\t')[0] == "OnConnectMsg")
                connection.SendTunneled(new MiniGamePayloadPacket
                {
                    StateId = packet.StateId,
                    Payload = Encoding.UTF8.GetBytes("OnServerReadyMsg\0")
                });

            return true;
        }

        // "Spin For The Win!" - the daily prize wheel (game_wheel.gfx).
        if (DailyWheelGame.HandleMessage(connection, message, packet.StateId))
            return true;

        return true;
    }
}
