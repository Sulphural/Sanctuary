using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class CoinStoreBuyBackRequestPacketHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CoinStoreBuyBackRequestPacketHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!CoinStoreBuyBackRequestPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(CoinStoreBuyBackRequestPacket));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(CoinStoreBuyBackRequestPacket), packet);

        // TransactionId == 0 is a tab-activation list request — just send the current list.
        if (packet.TransactionId == 0)
        {
            var listResponse = new CoinStoreBuyBackResponsePacket();
            listResponse.Transactions.AddRange(
                connection.Player.CoinStoreTransactions.Where(x => x.Type == 2));
            connection.SendTunneled(listResponse);
            return true;
        }

        var transaction = connection.Player.CoinStoreTransactions
            .FirstOrDefault(x => x.Id == packet.TransactionId && x.Type == 2);

        var coinStoreTransactionCompletePacket = new CoinStoreTransactionCompletePacket
        {
            TransactionRecord = { Type = 1 }
        };

        if (transaction is null)
        {
            coinStoreTransactionCompletePacket.Result = 4;
            connection.SendTunneled(coinStoreTransactionCompletePacket);
            return true;
        }

        var quantity = packet.Quantity == 0 ? transaction.QuantityRemaining : packet.Quantity;

        if (transaction.QuantityRemaining < quantity)
        {
            coinStoreTransactionCompletePacket.Result = 4;
            connection.SendTunneled(coinStoreTransactionCompletePacket);
            return true;
        }

        if (!_resourceManager.ClientItemDefinitions.TryGetValue(transaction.ItemRecord.Definition, out var def))
        {
            coinStoreTransactionCompletePacket.Result = 3;
            connection.SendTunneled(coinStoreTransactionCompletePacket);
            return true;
        }

        var totalCost = def.ResellValue * quantity;

        if (connection.Player.Coins < totalCost)
        {
            coinStoreTransactionCompletePacket.Result = 7;
            connection.SendTunneled(coinStoreTransactionCompletePacket);
            return true;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();

        var dbQuery = dbContext.Characters
            .Where(x => x.Id == GuidHelper.GetPlayerId(connection.Player.Guid))
            .Select(x => new
            {
                Character = x,
                Item = x.Items.SingleOrDefault(i => i.Definition == def.Id && i.Tint == transaction.ItemRecord.Tint),
                NextId = x.Items.Max(i => i.Id)
            })
            .SingleOrDefault();

        if (dbQuery is null)
        {
            coinStoreTransactionCompletePacket.Result = 8;
            connection.SendTunneled(coinStoreTransactionCompletePacket);
            return true;
        }

        var dbItem = dbQuery.Item;

        if (dbItem is not null)
        {
            dbItem.Count += quantity;
        }
        else
        {
            dbItem = new Database.Entities.DbItem
            {
                Id = dbQuery.NextId + 1,
                Definition = def.Id,
                Tint = transaction.ItemRecord.Tint,
                Count = quantity
            };

            dbQuery.Character.Items.Add(dbItem);
        }

        dbQuery.Character.Coins -= totalCost;

        if (dbContext.SaveChanges() <= 0)
        {
            coinStoreTransactionCompletePacket.Result = 8;
            connection.SendTunneled(coinStoreTransactionCompletePacket);
            return true;
        }

        // Update in-memory state
        transaction.QuantityRemaining -= quantity;
        if (transaction.QuantityRemaining <= 0)
            connection.Player.CoinStoreTransactions.Remove(transaction);

        var clientItem = connection.Player.Items.SingleOrDefault(x => x.Definition == def.Id && x.Tint == transaction.ItemRecord.Tint);
        var addItem = clientItem is null;

        if (addItem)
        {
            clientItem = new ClientItem
            {
                Id = dbItem.Id,
                Tint = dbItem.Tint,
                Count = dbItem.Count,
                Definition = dbItem.Definition
            };
            connection.Player.Items.Add(clientItem);
        }
        else
        {
            clientItem!.Count = dbItem.Count;
        }

        connection.Player.Coins = dbQuery.Character.Coins;

        if (addItem)
        {
            using var itemWriter = new Core.IO.PacketWriter();
            clientItem!.Serialize(itemWriter);

            var addPacket = new ClientUpdatePacketItemAdd { Payload = itemWriter.Buffer };
            connection.SendTunneled(addPacket);
        }
        else
        {
            connection.SendTunneled(new ClientUpdatePacketItemUpdate
            {
                ItemGuid = clientItem!.Id,
                Count = clientItem.Count
            });
        }

        connection.SendTunneled(new ClientUpdatePacketCoinCount { Coins = connection.Player.Coins });

        coinStoreTransactionCompletePacket.Result = 1;
        coinStoreTransactionCompletePacket.ItemGuid = clientItem!.Id;
        coinStoreTransactionCompletePacket.TransactionRecord.Id = packet.TransactionId;
        coinStoreTransactionCompletePacket.TransactionRecord.ItemRecord.Definition = def.Id;
        coinStoreTransactionCompletePacket.TransactionRecord.ItemRecord.Tint = transaction.ItemRecord.Tint;
        coinStoreTransactionCompletePacket.TransactionRecord.Quantity = quantity;
        coinStoreTransactionCompletePacket.TransactionRecord.MerchantGuid = connection.Player.ActiveMerchantGuid;
        connection.SendTunneled(coinStoreTransactionCompletePacket);

        // Send updated buy-back list.
        var buyBackResponse = new CoinStoreBuyBackResponsePacket();
        buyBackResponse.Transactions.AddRange(
            connection.Player.CoinStoreTransactions.Where(x => x.Type == 2));
        connection.SendTunneled(buyBackResponse);

        return true;
    }
}
