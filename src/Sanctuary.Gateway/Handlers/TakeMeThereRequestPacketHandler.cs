using System;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game.Quests;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class TakeMeThereRequestPacketHandler
{
    private static ILogger _logger = null!;
    private static IQuestManager _questManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(TakeMeThereRequestPacketHandler));
        _questManager = serviceProvider.GetRequiredService<IQuestManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, Span<byte> data)
    {
        if (!TakeMeThereRequestPacket.TryDeserialize(data, out var request))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(TakeMeThereRequestPacket));
            return false;
        }

        var player = connection.Player;

        if (!_questManager.TryGetActiveObjectiveTarget(player, out var goalPosition))
            return true;

        var reply = new ClientPathReplyPacket { RequestId = request.RequestId };

        reply.Path.Add(player.Position);

        var pathfinder = player.Zone.Pathfinder;
        if (pathfinder is not null)
        {
            var start = new Vector3(player.Position.X, player.Position.Y, player.Position.Z);

            foreach (var node in pathfinder.FindPath(start, goalPosition))
                reply.Path.Add(new Vector4(node.Position.X, node.Position.Y, node.Position.Z, 1f));
        }

        reply.Path.Add(new Vector4(goalPosition.X, goalPosition.Y, goalPosition.Z, 1f));

        player.SendTunneled(reply);

        return true;
    }
}
