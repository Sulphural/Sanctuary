using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class PacketTunneledClientPacketHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(PacketTunneledClientPacketHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, Span<byte> data)
    {
        if (!PacketTunneledClientPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(PacketTunneledClientPacket));
            return false;
        }

        var reader = new PacketReader(packet.Payload);

        if (!reader.TryRead(out short opCode))
        {
            _logger.LogError("Failed to read opcode from packet. ( Data: {data} )", Convert.ToHexString(data));
            return false;
        }

        bool handled;
        try
        {
            handled = opCode switch
            {
            PacketClientFinishedLoading.OpCode => PacketClientFinishedLoadingHandler.HandlePacket(connection),
            PacketClientIsReady.OpCode => PacketClientIsReadyHandler.HandlePacket(connection),
            BaseChatPacket.OpCode => BaseChatPacketHandler.HandlePacket(connection, reader),
            BaseCommandPacket.OpCode => BaseCommandPacketHandler.HandlePacket(connection, reader),
            BasePlayerUpdatePacket.OpCode => BasePlayerUpdatePacketHandler.HandlePacket(connection, reader),
            BaseMiniGamePacket.OpCode => BaseMiniGamePacketHandler.HandlePacket(connection, reader),
            BaseAbilityPacket.OpCode => BaseAbilityPacketHandler.HandlePacket(connection, reader),
            BaseEncounterPacket.OpCode => BaseEncounterPacketHandler.HandlePacket(connection, reader),
            BaseInventoryPacket.OpCode => BaseInventoryPacketHandler.HandlePacket(connection, reader),
            PacketGameTimeSync.OpCode => PacketGameTimeSyncHandler.HandlePacket(connection, packet.Payload),
            PacketBaseInGamePurchase.OpCode => PacketBaseInGamePurchaseHandler.HandlePacket(connection, reader),
            BaseQuickChatPacket.OpCode => BaseQuickChatPacketHandler.HandlePacket(connection, reader),
            PacketZoneTeleportRequest.OpCode => PacketZoneTeleportRequestHandler.HandlePacket(connection, packet.Payload),
            PacketClientMetrics.OpCode => PacketClientMetricsHandler.HandlePacket(connection, packet.Payload),
            PacketClientLog.OpCode => PacketClientLogHandler.HandlePacket(connection, packet.Payload),
            PacketZoneSafeTeleportRequest.OpCode => PacketZoneSafeTeleportRequestHandler.HandlePacket(connection, packet.Payload),
            PlayerUpdatePacketUpdatePosition.OpCode => PlayerUpdatePacketUpdatePositionHandler.HandlePacket(connection, packet.Payload),
            PlayerUpdatePacketCameraUpdate.OpCode => PlayerUpdatePacketCameraUpdateHandler.HandlePacket(connection, packet.Payload),
            BaseHousingPacket.OpCode => BaseHousingPacketHandler.HandlePacket(connection, reader),
            BasePlayerTitlePacket.OpCode => BasePlayerTitlePacketHandler.HandlePacket(connection, reader),
            BaseFotomatPacket.OpCode => BaseFotomatPacketHandler.HandlePacket(connection, reader),
            PlayerUpdatePacketJump.OpCode => PlayerUpdatePacketJumpHandler.HandlePacket(connection, packet.Payload),
            BaseCoinStorePacket.OpCode => BaseCoinStorePacketHandler.HandlePacket(connection, reader),
            BaseActivityServicePacket.OpCode => BaseActivityServicePacketHandler.HandlePacket(connection, reader, 2),
            MountBasePacket.OpCode => MountBasePacketHandler.HandlePacket(connection, reader),
            PetBasePacket.OpCode => PetBasePacketHandler.HandlePacket(connection, reader),
            PacketClientInitializationDetails.OpCode => PacketClientInitializationDetailsHandler.HandlePacket(connection, packet.Payload),
            BaseNameChangePacket.OpCode => BaseNameChangePacketHandler.HandlePacket(connection, reader),
            BaseCombatPacket.OpCode => BaseCombatPacketHandler.HandlePacket(connection, reader),
            BaseQuestPacket.OpCode => BaseQuestPacketHandler.HandlePacket(connection, reader),
            BaseObjectivePacketHandler.OpCode => BaseObjectivePacketHandler.HandlePacket(connection, reader), // SPIKE: observe client objective reports (45/7)
            BaseUiPacket.OpCode => BaseUiPacketHandler.HandlePacket(connection, reader),
            ClientPathBasePacket.OpCode => ClientPathBasePacketHandler.HandlePacket(connection, reader),
            BaseGroupPacket.OpCode => BaseGroupPacketHandler.HandlePacket(connection, reader),
                _ => false
            };
        }
        catch (Exception ex)
        {
            // A handler throwing here would otherwise propagate to the UDP receive loop in
            // GatewayService.ExecuteAsync and tear down the entire host. Log and swallow so one
            // bad packet/interaction only drops that action, not the whole server.
            _logger.LogError(ex, "Unhandled exception processing tunneled packet op={op}. ( Data: {data} )",
                opCode, Convert.ToHexString(packet.Payload));
            handled = true;
        }

        // OBSERVE: unhandled tunneled opcodes used to be visible only in DEBUG builds (Debug.WriteLine) —
        // which is how the GO! button's real packet got dropped invisibly in Release (LIVE TEST 1, 2026-07-01).
        // Log them at INFO (with the resolved packet name) so no client packet ever disappears silently again.
        if (!handled)
        {
            reader.Reset();
            var pktName = reader.ReadTunneledPacketName();
            _logger.LogInformation("UNHANDLED tunneled opcode={op} name={name} | payload={hex}",
                opCode, pktName, Convert.ToHexString(packet.Payload));
        }

        return handled;
    }
}