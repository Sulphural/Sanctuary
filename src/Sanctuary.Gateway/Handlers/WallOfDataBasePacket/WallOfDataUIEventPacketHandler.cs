using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class WallOfDataUIEventPacketHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(WallOfDataUIEventPacketHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        Console.WriteLine($"[DEBUG] WallOfDataUIEventPacketHandler.HandlePacket called! Data length: {data.Length}");

        if (!WallOfDataUIEventPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(WallOfDataUIEventPacket));
            Console.WriteLine("[DEBUG] WallOfDataUIEventPacket deserialization FAILED");
            return false;
        }

        Console.WriteLine($"[DEBUG] Packet deserialized successfully: TableName={packet.TableName}, Callback={packet.Callback}, Param={packet.Param}");
        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(WallOfDataUIEventPacket), packet);

        // Handle claim code redemption
        if (packet.Callback == "redeemCode" && !string.IsNullOrEmpty(packet.Param))
        {
            Console.WriteLine($"[DEBUG] Calling HandleClaimCode with code: {packet.Param}");
            return HandleClaimCode(connection, packet.Param);
        }

        Console.WriteLine("[DEBUG] Packet processed but not a claim code redemption");
        return true;
    }

    private static bool HandleClaimCode(GatewayConnection connection, string code)
    {
        _logger.LogInformation("HandleClaimCode called with code: {Code}", code);

        if (connection.Player?.Zone is not Sanctuary.Game.Zones.BaseZone zone)
        {
            _logger.LogError("Player zone is not a BaseZone");
            SendRedemptionNotification(connection, false);
            return true;
        }

        var claimCode = zone.GetClaimCodes().FirstOrDefault(x =>
            string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));

        if (claimCode is null)
        {
            _logger.LogWarning("Invalid claim code: {Code}", code);
            SendRedemptionNotification(connection, false);
            return true;
        }

        var itemIds = zone.GetClaimCodeItemIds(code);
        var itemDefs = new List<Sanctuary.Packet.Common.ClientItemDefinition>();

        foreach (var itemId in itemIds)
        {
            if (!_resourceManager.ClientItemDefinitions.TryGetValue(itemId, out var def))
            {
                _logger.LogWarning("Claim code {Code} references missing item {ItemId} — skipping", code, itemId);
                continue;
            }
            itemDefs.Add(def);
        }

        if (itemDefs.Count == 0)
        {
            _logger.LogError("Claim code {Code} has no valid item definitions", code);
            SendRedemptionNotification(connection, false);
            return true;
        }

        // Reject if the player already has any of the bundle items
        if (connection.Player.Items.Any(x => itemDefs.Any(d => d.Id == x.Definition)))
        {
            _logger.LogInformation("Player {Guid} already redeemed code {Code}", connection.Player.Guid, code);
            SendRedemptionNotification(connection, false);
            return true;
        }

        var newItems = new List<ClientItem>();
        foreach (var def in itemDefs)
        {
            var newItem = new ClientItem { Definition = def.Id, Count = zone.GetClaimCodeItemCount(code, def.Id), Tint = 0 };

            if (!connection.SaveItemToDatabase(newItem))
            {
                _logger.LogError("Failed to save item {Definition} to database for code {Code}", def.Id, code);
                SendRedemptionNotification(connection, false);
                return true;
            }

            connection.Player.Items.Add(newItem);
            newItems.Add(newItem);
        }

        // Send all item definitions in one packet
        using var defWriter = new Core.IO.PacketWriter();
        defWriter.Write(itemDefs.ToArray());
        connection.SendTunneled(new PlayerUpdatePacketItemDefinitions { Payload = defWriter.Buffer });

        // Send one ItemAdd packet per item
        foreach (var item in newItems)
        {
            using var writer = new Core.IO.PacketWriter();
            item.Serialize(writer);
            connection.SendTunneled(new ClientUpdatePacketItemAdd { Payload = writer.Buffer });
        }

        SendRedemptionNotification(connection, true);
        connection.SendTunneled(new ExecuteScriptPacket { Script = "WelcomeHandler.close()" });

        _logger.LogInformation("Granted {Count} item(s) to player {Guid} via code {Code}",
            newItems.Count, connection.Player.Guid, code);

        return true;
    }

    private static void SendRedemptionNotification(GatewayConnection connection, bool success)
    {
        connection.SendTunneled(new KeyCodeRedemptionNotificationPacket { Success = success });
        // Empty bundle list dismisses the "Processing... Please Wait" spinner in the Claim window
        connection.SendTunneled(new PromotionalBundleDataPacket());
        _logger.LogInformation("Sent KeyCodeRedemptionNotificationPacket (Success={Success})", success);
    }
}