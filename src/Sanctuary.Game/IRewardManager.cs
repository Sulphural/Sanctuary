using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Rewards;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game;

public interface IRewardManager
{
    bool TryRollReward(string rewardTableKey, out RewardDropDefinition? drop);

    // Describes what a reward table can pay into a reward-preview bundle (the offer / turn-in
    // "Show Details" panel), so a caller no longer has to hand-author a parallel display-only item
    // list that nothing keeps in step with the table's real drops. Adds entries; never clears.
    bool TryBuildPreview(string rewardTableKey, RewardBundleBase bundle);

    bool TryGrantReward(Player player, RewardDropDefinition drop, ulong sourceGuid = 0);

    bool TryGrantItem(Player player, int itemDefinitionId, int tint, int quantity = 1, ulong sourceGuid = 0);

    bool TryGrantCurrency(Player player, CurrencyType currencyType, int amount);
}
