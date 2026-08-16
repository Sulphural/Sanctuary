using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// 141/13 QueueStatsRequest - the Matchmaking panel asking for fresh "N Waiting / Avg Wait" numbers while
// it is open (Matchmaking:UpdateAllQueueStats in lobby.lua).
//
// ★ CORRECTION: this used to answer by re-sending the queue list, on a reading of lobby.lua that said the
// counts were columns of the queue record. They are not - stamping every int field of a row with a marker
// left "Waiting 0 / Avg Wait -" untouched on screen. The answer is a real QueueStatsResponse (141/14),
// two parallel List<int>. See QueueStatsResponsePacket.
[PacketHandler]
public static class QueueStatsRequestPacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(QueueStatsRequestPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!QueueStatsRequestPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(QueueStatsRequestPacket));
            return false;
        }

        _logger.LogInformation("Matchmaking: queue stats requested (guid {guid}).", packet.Guid);

        MatchmakingQueueTable.SendStats(connection.Player, packet.Guid);

        return true;
    }
}
