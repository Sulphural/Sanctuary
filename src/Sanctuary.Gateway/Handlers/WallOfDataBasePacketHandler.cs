using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class WallOfDataBasePacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(WallOfDataBasePacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        if (!reader.TryRead(out byte opCode))
        {
            _logger.LogError("Failed to read opcode from packet. ( Data: {data} )", Convert.ToHexString(reader.Span));
            return false;
        }

        return opCode switch
        {
            WallOfDataUIEventPacket.OpCode => WallOfDataUIEventPacketHandler.HandlePacket(connection, reader.Span),
            // Sub-opcodes 2 and 6: client-side UI telemetry (config-driven via the client's own
            // WallOfDataEventTypes.txt, sibling classes are PlayerClickMove/PlayerKeyboard/WalletBalance -
            // not gameplay-relevant). Acknowledged as a no-op to stop UNHANDLED log spam rather than
            // invent meaning for data we can't confirm a purpose for.
            2 or 6 => true,
            _ => false
        };
    }
}