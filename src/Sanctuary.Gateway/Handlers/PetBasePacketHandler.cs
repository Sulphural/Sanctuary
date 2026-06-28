using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PetBasePacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PetBasePacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        _logger.LogInformation("Received PetBasePacket. CharacterId={characterId}, BaseOpCode={baseOpCode}",
            connection.Player?.CharacterId ?? 0, PetBasePacket.OpCode);

        if (!reader.TryRead(out byte opCode))
        {
            _logger.LogError("Failed to read sub-opcode from PetBasePacket. ( Data: {data} )", Convert.ToHexString(reader.Span));
            return false;
        }

        _logger.LogInformation("PetBasePacket sub-opcode: {subOpCode}", opCode);

        var result = opCode switch
        {
            PetSummonRecallPacket.OpCode => PetSummonRecallPacketHandler.HandlePacket(connection, reader.Span),
            PetListPacket.OpCode => PetListPacketHandler.HandlePacket(connection),
            PacketPetSpawn.OpCode => PacketPetSpawnHandler.HandlePacket(connection, reader.Span),
            PacketPetDismount.OpCode => PacketPetDismountHandler.HandlePacket(connection),
            _ => HandleUnknownOpCode(opCode)
        };

        return result;
    }

    private static bool HandleUnknownOpCode(byte opCode)
    {
        _logger.LogWarning("Unknown PetBasePacket sub-opcode: {subOpCode}", opCode);
        return false;
    }
}
