using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class MountBasePacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(MountBasePacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        _logger.LogInformation("Received MountBasePacket. CharacterId={characterId}", connection.Player?.CharacterId ?? 0);

        if (!reader.TryRead(out byte opCode))
        {
            _logger.LogError("Failed to read opcode from packet. ( Data: {data} )", Convert.ToHexString(reader.Span));
            return false;
        }

        _logger.LogInformation("MountBasePacket sub-opcode: {subOpCode}", opCode);

        return opCode switch
        {
            PacketDismountRequest.OpCode => PacketDismountRequestHandler.HandlePacket(connection),
            PacketMountSpawn.OpCode => PacketMountSpawnHandler.HandlePacket(connection, reader.Span),
            PacketMountSpawnByItemDefinitionId.OpCode => PacketMountSpawnByItemDefinitionIdHandler.HandlePacket(connection, reader.Span),
            PacketMountList.OpCode => SendMountList(connection),
            _ => HandleUnknownOpCode(opCode)
        };
    }

    private static bool SendMountList(GatewayConnection connection)
    {
        _logger.LogInformation("Client requested mount list. Sending {count} mounts", connection.Player.Mounts.Count);

        var packetMountList = new PacketMountList
        {
            Mounts = connection.Player.Mounts
        };

        connection.Player.SendTunneled(packetMountList);
        return true;
    }

    private static bool HandleUnknownOpCode(byte opCode)
    {
        _logger.LogWarning("Unknown MountBasePacket sub-opcode: {subOpCode}", opCode);
        return false;
    }
}