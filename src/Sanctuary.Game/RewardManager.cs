using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Rewards;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game;

public class RewardManager : IRewardManager
{
    private readonly ILogger _logger;
    private readonly IResourceManager _resourceManager;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory;

    public RewardManager(ILogger<RewardManager> logger, IResourceManager resourceManager,
        IDbContextFactory<DatabaseContext> dbContextFactory)
    {
        _logger = logger;
        _resourceManager = resourceManager;
        _dbContextFactory = dbContextFactory;
    }

    public bool TryRollReward(string rewardTableKey, out RewardDropDefinition? drop)
    {
        drop = null;

        if (!_resourceManager.RewardTables.TryGetValue(rewardTableKey.Trim().ToLowerInvariant(), out var table))
        {
            _logger.LogError("Unknown reward table {key}.", rewardTableKey);
            return false;
        }

        drop = table.Table.SelectRandom();
        return true;
    }

    public bool TryBuildPreview(string rewardTableKey, RewardBundleBase bundle)
    {
        if (!_resourceManager.RewardTables.TryGetValue(rewardTableKey.Trim().ToLowerInvariant(), out var table))
        {
            _logger.LogError("Unknown reward table {key}.", rewardTableKey);
            return false;
        }

        // A table that names its own stand-in previews as that single item - the wrapped present, not
        // the thirteen things that might be inside it.
        if (table.PreviewItemDefinitionId != 0)
            return TryAddPreviewEntry(bundle, table.PreviewItemDefinitionId, table.PreviewQuantity);

        // Otherwise the pool IS the preview: every item outcome, at the quantity it would actually pay.
        // Grouped by definition id because one item listed at several weights is still one outcome to
        // the player, and shown at the largest of those quantities so the icon never under-promises.
        var added = false;

        foreach (var outcome in table.DropTable.OfType<ItemRewardDropDefinition>().GroupBy(drop => drop.ItemDefinitionId))
            added |= TryAddPreviewEntry(bundle, outcome.Key, outcome.Max(drop => drop.Quantity));

        // Currency outcomes are deliberately left out: RewardBundleBase.Coins reads as a guaranteed
        // payout, so a coin drop that only lands some of the time cannot be shown honestly through it.
        // Guaranteed coins still come from the caller (a quest's RewardCoins), which this never touches.
        return added;
    }

    // One preview row: icon + name + count for an item the player does NOT own yet, so it carries no
    // inventory row id and the bundle keeps CarriesItemGuids clear.
    private bool TryAddPreviewEntry(RewardBundleBase bundle, int itemDefinitionId, int quantity)
    {
        if (!_resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
        {
            _logger.LogError("Cannot preview unknown item definition {itemDefinitionId}.", itemDefinitionId);
            return false;
        }

        bundle.Entries.Add(new RewardBundleEntryItem
        {
            // Icon.Id is the image-SET id and goes to the wire UNCHANGED - resolving it through
            // ImageSetMappings breaks every reward preview.
            IconId = itemDefinition.Icon.Id,
            TintId = itemDefinition.Icon.TintId,
            NameId = itemDefinition.NameId,
            DefinitionId = itemDefinitionId,
            // 1 hides the "xN" label, which is what a quantity-less reward wants anyway.
            Quantity = Math.Max(1, quantity)
        });

        return true;
    }

    public bool TryGrantReward(Player player, RewardDropDefinition drop, ulong sourceGuid = 0)
    {
        return drop switch
        {
            ItemRewardDropDefinition item => TryGrantItem(player, item.ItemDefinitionId,
                item.TintTable?.SelectRandom().TintId ?? 0, item.Quantity, sourceGuid),
            CurrencyRewardDropDefinition currency => TryGrantCurrency(player, currency.CurrencyType, currency.Amount),
            _ => false
        };
    }

    public bool TryGrantItem(Player player, int itemDefinitionId, int tint, int quantity = 1, ulong sourceGuid = 0)
    {
        // NOTE: this same DbItem/ClientItem/ItemAdd block is written out nine times across the tree
        // (QuestManager, EncounterArenaZone, BaseMiniGamePacketHandler, CommandSupport, the coin store,
        // buy-back, the SC store, housing pickup, the claim-code handler). This is meant to become the
        // one copy - the others still need folding into it.
        if (quantity < 1)
            return false;

        if (!_resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
        {
            _logger.LogError("Cannot grant unknown item definition {itemDefinitionId}.", itemDefinitionId);
            return false;
        }

        var characterId = GuidHelper.GetPlayerId(player.Guid);

        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbCharacter = dbContext.Characters
            .Include(character => character.Items)
            .SingleOrDefault(character => character.Id == characterId);

        if (dbCharacter is null)
            return false;

        var dbItem = dbCharacter.Items.FirstOrDefault(item => item.Definition == itemDefinitionId && item.Tint == tint);

        if (dbItem is null)
        {
            dbItem = new DbItem
            {
                Id = dbCharacter.Items.Select(item => item.Id).DefaultIfEmpty(0).Max() + 1,
                Definition = itemDefinitionId,
                Count = quantity,
                Tint = tint
            };

            dbCharacter.Items.Add(dbItem);
        }
        else
        {
            dbItem.Count += quantity;
        }

        if (dbContext.SaveChanges() <= 0)
            return false;

        var clientItem = player.Items.FirstOrDefault(item => item.Definition == itemDefinitionId && item.Tint == tint);

        if (clientItem is null)
        {
            clientItem = new ClientItem
            {
                Id = dbItem.Id,
                Definition = dbItem.Definition,
                Count = dbItem.Count,
                Tint = dbItem.Tint
            };

            player.Items.Add(clientItem);

            using var writer = new PacketWriter();
            clientItem.Serialize(writer);
            itemDefinition.Serialize(writer);

            player.SendTunneled(new ClientUpdatePacketItemAdd { Payload = writer.Buffer });
        }
        else
        {
            clientItem.Count = dbItem.Count;
            player.SendTunneled(new ClientUpdatePacketItemUpdate
            {
                ItemGuid = clientItem.Id,
                Count = clientItem.Count
            });
        }

        SendRewardToast(player, clientItem, itemDefinition, quantity, sourceGuid);

        return true;
    }

    public bool TryGrantCurrency(Player player, CurrencyType currencyType, int amount)
    {
        if (amount <= 0)
            return false;

        if (currencyType == CurrencyType.StationCash)
        {
            _logger.LogWarning("Station Cash grants are not yet implemented.");
            return false;
        }

        var characterId = GuidHelper.GetPlayerId(player.Guid);

        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbCharacter = dbContext.Characters.SingleOrDefault(character => character.Id == characterId);

        if (dbCharacter is null)
            return false;

        dbCharacter.Coins += amount;

        if (dbContext.SaveChanges() <= 0)
            return false;

        player.Coins = dbCharacter.Coins;

        player.SendTunneled(new ClientUpdatePacketCoinCount { Coins = player.Coins });

        return true;
    }

    private static void SendRewardToast(Player player, ClientItem clientItem, ClientItemDefinition itemDefinition,
        int quantity, ulong sourceGuid)
    {
        var packet = new RewardBundlePacket();

        // A real grant, so the entry carries the player's new inventory row id and the bundle's lead
        // byte has to be set for that tail to reach the wire.
        packet.RewardBundle.CarriesItemGuids = true;
        packet.RewardBundle.SourceGuid = sourceGuid;
        packet.RewardBundle.PlayerGuid = player.Guid;
        packet.RewardBundle.IconId = itemDefinition.Icon.Id;
        packet.RewardBundle.NameId = itemDefinition.NameId;
        packet.RewardBundle.Entries.Add(new RewardBundleEntryItem
        {
            IconId = itemDefinition.Icon.Id,
            TintId = itemDefinition.Icon.TintId,
            NameId = itemDefinition.NameId,
            Quantity = quantity,
            DefinitionId = clientItem.Definition,
            Tint = clientItem.Tint,
            ItemGuid = clientItem.Id
        });

        player.SendTunneled(packet);
    }
}
