using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class CoinStoreItemDynamicListUpdateRequestPacketHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CoinStoreItemDynamicListUpdateRequestPacketHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!CoinStoreItemDynamicListUpdateRequestPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(CoinStoreItemDynamicListUpdateRequestPacket));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(CoinStoreItemDynamicListUpdateRequestPacket), packet);
        Console.WriteLine($"[165:8] Client requested dynamic items. ActiveMerchantGuid={connection.Player.ActiveMerchantGuid}");

        var merchantGuid = connection.Player.ActiveMerchantGuid;
        if (merchantGuid != 0 && _resourceManager.NpcVendors.TryGetValue(merchantGuid, out var vendorDef))
        {
            _resourceManager.Stores.TryGetValue(1, out var mainStore);

            // Log which bundles are found vs missing in the store.
            foreach (var bundleId in vendorDef.Bundles)
            {
                var found = mainStore?.Bundles.ContainsKey(bundleId) == true;
                Console.WriteLine($"[165:8] Bundle {bundleId}: {(found ? "FOUND in store" : "MISSING from store")}");
            }

            // 66:42 — merchant bundle list.
            var bundleListPacket = new InGamePurchaseMerchantListPacket { MerchantGuid = merchantGuid };
            bundleListPacket.BundleIds.AddRange(vendorDef.Bundles);
            var b42 = bundleListPacket.Serialize();
            Console.WriteLine($"[165:8→66:42] ({b42.Length} bytes): {Convert.ToHexString(b42)}");
            connection.SendTunneled(bundleListPacket);

            // 66:45 — available quantities per bundle.
            var quantityPacket = new InGamePurchaseUpdateMerchantBundleQuantityPacket();
            foreach (var bundleId in vendorDef.Bundles)
                quantityPacket.Entries.Add((bundleId, 999));
            var b45 = quantityPacket.Serialize();
            Console.WriteLine($"[165:8→66:45] ({b45.Length} bytes): {Convert.ToHexString(b45)}");
            connection.SendTunneled(quantityPacket);
        }

        // 165:9 — populate dynamic items for the active merchant.
        var response = new CoinStoreItemDynamicListUpdateResponsePacket();

        if (merchantGuid != 0 && _resourceManager.NpcVendors.TryGetValue(merchantGuid, out var vendorForDynamic))
        {
            _resourceManager.Stores.TryGetValue(1, out var dynStore);

            foreach (var bundleId in vendorForDynamic.Bundles)
            {
                if (dynStore?.Bundles.TryGetValue(bundleId, out var bundle) != true)
                    continue;

                foreach (var entry in bundle.Entries)
                {
                    if (response.DynamicItems.ContainsKey(entry.MarketingItemId))
                        continue;

                    if (!_resourceManager.ClientItemDefinitions.ContainsKey(entry.MarketingItemId))
                        continue;

                    // Use the bundle's CategoryGroupId as CategoryId so the client can match
                    // this item against the category group filter in the merchant window (165:10).
                    response.DynamicItems[entry.MarketingItemId] = new ItemDefinitionMetaData
                    {
                        Id = entry.MarketingItemId,
                        CategoryId = bundle.CategoryGroupId,
                    };
                }
            }
        }

        var responseBytes = response.Serialize();
        Console.WriteLine($"[165:9] Sending response ({responseBytes.Length} bytes, {response.DynamicItems.Count} dynamic items): {Convert.ToHexString(responseBytes)}");
        connection.SendTunneled(response);

        return true;
    }
}
