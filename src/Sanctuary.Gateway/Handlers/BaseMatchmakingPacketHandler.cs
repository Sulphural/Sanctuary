using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class BaseMatchmakingPacketHandler
{
    private static ILogger _logger = null!;
    private static Sanctuary.Game.Party.IPartyManager _partyManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(BaseMatchmakingPacketHandler));

        _partyManager = serviceProvider.GetRequiredService<Sanctuary.Game.Party.IPartyManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        if (!reader.TryRead(out short opCode))
        {
            _logger.LogError("Failed to read opcode from packet. ( Data: {data} )", Convert.ToHexString(reader.Span));
            return false;
        }

        return opCode switch
        {
            ListQueuesRequestPacket.OpCode => ListQueuesRequestPacketHandler.HandlePacket(connection, reader.Span),
            QueueStatsRequestPacket.OpCode => QueueStatsRequestPacketHandler.HandlePacket(connection, reader.Span),
            AddMatchRequestPacket.OpCode => AddMatchRequestPacketHandler.HandlePacket(connection, reader.Span),
            6 => LeaveQueue(connection),  // 141/6 CancelMatchRequest - "Leave Queue"
            _ => LogUnhandled(opCode, reader)
        };
    }

    // 141/6 CancelMatchRequest - the panel's "Leave Queue". Its body hasn't been decoded and doesn't need
    // to be: a player can only be in one queue, and this connection already says who they are, so the
    // sweep-every-queue Leave does the job without reading a byte of it.
    private static bool LeaveQueue(GatewayConnection connection)
    {
        var player = connection.Player;

        // A group queues together (see AddMatchRequestPacketHandler), so it leaves together - otherwise the
        // leader backs out and the rest sit in a queue they never chose to be in on their own.
        var party = _partyManager.GetParty(player);
        var leaving = party?.Members ?? [player];

        foreach (var member in leaving)
            MatchmakingQueueTable.Withdraw(member);

        _logger.LogInformation("Matchmaking: {player} left the queue with {size} group member(s).",
            player.Name?.FullName, leaving.Count);

        return true;
    }

    // The rest of the family is mapped but not implemented - 3/4 AddMatchRequest(+Response),
    // 5 ClearMatchRequest, 6 CancelMatchRequest, 9/10 MatchInvitationRequest(+Response), 12
    // SelectQueueForUser, 14 QueueStatsResponse, 15 MatchmakingServerStatus. Logging them beats returning
    // false silently: the whole point of opening the panel is to find out which of these the client
    // actually sends and when, and an unanswered request otherwise looks identical to a packet we never
    // received.
    private static bool LogUnhandled(short subOpCode, PacketReader reader)
    {
        _logger.LogInformation("BaseMatchmakingPacket UNHANDLED sub-opcode={sub} | body={hex}",
            subOpCode, Convert.ToHexString(reader.RemainingSpan));

        return true;
    }
}