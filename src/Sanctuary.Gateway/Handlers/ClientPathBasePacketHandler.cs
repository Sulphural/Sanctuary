using System;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

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

        var reply = new ClientPathReplyPacket { RequestId = request.RequestId, ResultType = request.Mode };

        reply.Path.Add(request.Start);

        var pathfinder = connection.Player.Zone.Pathfinder;
        if (pathfinder is not null)
        {
            var start = new Vector3(request.Start.X, request.Start.Y, request.Start.Z);
            var goal = new Vector3(request.End.X, request.End.Y, request.End.Z);

            foreach (var node in pathfinder.FindPath(start, goal))
                reply.Path.Add(new Vector4(node.Position.X, node.Position.Y, node.Position.Z, 1f));
        }

        reply.Path.Add(request.End);

        connection.Player.SendTunneled(reply);

        return true;
    }
}
