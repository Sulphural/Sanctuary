using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketTunneledClientWorldPacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketTunneledClientWorldPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, Span<byte> data)
    {
        if (!PacketTunneledClientWorldPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketTunneledClientWorldPacket));
            return false;
        }

        var reader = new PacketReader(packet.Payload);

        if (!reader.TryRead(out short opCode))
        {
            _logger.LogError("Failed to read opcode from packet. ( Data: {data} )", Convert.ToHexString(data));
            return false;
        }

        var handled = opCode switch
        {
            BaseCommandPacket.OpCode => BaseCommandPacketHandler.HandlePacket(connection, reader),
            // INSTANCE WIP (Frostfang Fury): the GO! button's EncounterParticipantRequestEntrancePacket
            // (op41/sub108, IDA sub_8B6E70) is sent on THIS world tunnel — not the client tunnel. It was
            // being dropped invisibly here (op41 was never routed + unhandled drops were DEBUG-only).
            // Route the encounter + minigame families exactly like the client tunnel does.
            BaseEncounterPacket.OpCode => BaseEncounterPacketHandler.HandlePacket(connection, reader),
            BaseMiniGamePacket.OpCode => BaseMiniGamePacketHandler.HandlePacket(connection, reader),
            PacketWorldTeleportRequest.OpCode => PacketWorldTeleportRequestHandler.HandlePacket(connection, packet.Payload),
            PacketBaseInGamePurchase.OpCode => PacketBaseInGamePurchaseHandler.HandlePacket(connection, reader),
            PacketSetLocale.OpCode => PacketSetLocaleHandler.HandlePacket(connection, packet.Payload),
            BaseLobbyGameDefinitionPacket.OpCode => BaseLobbyGameDefinitionPacketHandler.HandlePacket(connection, reader),
            BaseHousingPacket.OpCode => BaseHousingPacketHandler.HandlePacket(connection, reader),
            BaseMatchmakingPacket.OpCode => BaseMatchmakingPacketHandler.HandlePacket(connection, reader),
            BaseFotomatPacket.OpCode => BaseFotomatPacketHandler.HandlePacket(connection, reader),
            BaseActivityServicePacket.OpCode => BaseActivityServicePacketHandler.HandlePacket(connection, reader, 1),
            WallOfDataBasePacket.OpCode => WallOfDataBasePacketHandler.HandlePacket(connection, reader),
            BaseObjectivePacketHandler.OpCode => BaseObjectivePacketHandler.HandlePacket(connection, reader), // SPIKE: client objective reports (45/7) arrive on the WORLD tunnel
            BaseGroupPacket.OpCode => BaseGroupPacketHandler.HandlePacket(connection, reader),
            _ => false
        };

        // OBSERVE: same Release-visible log as the client tunnel — never drop a packet invisibly again.
        if (!handled)
        {
            _logger.LogInformation("UNHANDLED tunneled WORLD opcode={op} | payload={hex}",
                opCode, Convert.ToHexString(packet.Payload));
        }

        return handled;
    }
}