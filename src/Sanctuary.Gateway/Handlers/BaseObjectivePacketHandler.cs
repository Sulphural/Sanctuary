using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// SPIKE (combat-tutorial research): observe INBOUND op45 objective packets. The op45 family is mostly
// server->client (present/arm objectives), but sub-opcode 7 (ObjectiveClientComplete) is client->server:
// the client sends it when it detects a client-side objective (look-at, first-movement, etc.). This
// handler logs every inbound op45 sub-opcode + raw bytes so we can confirm whether the client reports
// tutorial steps after we arm them (ObjectiveLookAt 45/6 / ObjectiveFirstMovement 45/11). Log-only for
// now (returns true so the dispatcher doesn't warn).
[PacketHandler]
public static class BaseObjectivePacketHandler
{
    public const short OpCode = 45;

    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(BaseObjectivePacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        var span = reader.Span;

        // header: short OpCode(45) + byte SubOpCode
        byte subOp = span.Length >= 3 ? span[2] : (byte)0;

        // sub 7 = ObjectiveClientComplete (client -> server): the client detected a client-side objective
        // (look-at / first-movement / …). Wire format RE-confirmed live: [short 45][byte 7][int ObjectiveId].
        if (subOp == 7 && span.Length >= 7)
        {
            int objectiveId = BitConverter.ToInt32(span.Slice(3, 4));
            _logger.LogInformation("ObjectiveClientComplete: objective={obj}", objectiveId);

            // Advance the combat tutorial if this player is in the tutorial dungeon instance.
            if (connection.Player.Zone is Sanctuary.Game.Zones.CombatTutorialZone tutorial)
                tutorial.OnTutorialObjectiveComplete(connection.Player, objectiveId);

            return true;
        }

        _logger.LogInformation("INBOUND op45 sub={sub} len={len} bytes={hex}",
            subOp, span.Length, Convert.ToHexString(span));

        return true;
    }
}
