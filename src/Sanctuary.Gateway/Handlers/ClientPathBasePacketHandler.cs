using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// Opcode 98 sub 1: the "Take Me There" path request.
//
// The client does the routing itself. It ships a per-zone roadmap ("<zone>.map" - the same file we
// load into Zone.Pathfinder) and searches that graph locally for a full route to its target before it
// ever asks us. What it then requests is only the short off-road hop from the player's exact position
// to the FIRST node of its own route; it walks our hop and continues along its own path from there.
// It refuses to send the request at all when that hop is longer than 300 units, which is why requests
// keep arriving while the objective is thousands of units away.
//
// So the answer is simply the hop the client asked for. Substituting our own destination here (the
// tracked quest NPC) replaces a ~30-unit hop with a multi-thousand-unit straight line, which the
// client then walks literally, through whatever scenery is in the way. In a zone the client has no
// roadmap for, its search finds nothing and End is the raw target, so answering End stays correct.
[PacketHandler]
public static class ClientPathBasePacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientPathBasePacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        var fullBuffer = reader.Span;

        if (!reader.TryRead(out byte subOpCode))
            return false;

        return subOpCode switch
        {
            ClientPathRequestPacket.OpCode => HandlePathRequest(connection, fullBuffer),
            _ => false
        };
    }

    private static bool HandlePathRequest(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientPathRequestPacket.TryDeserialize(data, out var request))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientPathRequestPacket));
            return false;
        }

        // Mode says WHICH client controller asked and ResultType routes the reply back to it: 1 = the
        // breadcrumb trail follower (draws the green line), 2 = the auto-move controller (walks the
        // character). They run independently and each sends its own request, so echoing the Mode back
        // is all that is needed - and a trail refresh can no longer make the character walk off on its
        // own, because it is never answered with an auto-move reply.
        var reply = new ClientPathReplyPacket { RequestId = request.RequestId, ResultType = request.Mode };

        reply.Path.Add(request.Start);
        reply.Path.Add(request.End);

        connection.Player.SendTunneled(reply);

        return true;
    }
}
