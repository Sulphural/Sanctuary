using System.Collections.Generic;

using Sanctuary.Core.Collections;

namespace Sanctuary.Game.Resources.Definitions.Rewards;

public sealed class RewardTableDefinition
{
    public string Key { get; set; } = string.Empty;
    public List<RewardDropDefinition> DropTable { get; set; } = [];

    // Optional stand-in shown in a reward PREVIEW instead of the pool itself. Retail's mystery gift
    // previews the wrapped present and only reveals the sweater-or-cookie on payout, so a table that
    // sets this previews as that one item however many outcomes it really holds. 0 = preview the pool.
    public int PreviewItemDefinitionId { get; set; }

    // How many of PreviewItemDefinitionId the preview shows; 1 hides the "xN" label. Ignored when
    // PreviewItemDefinitionId is 0, since pool entries preview at their own Quantity.
    public int PreviewQuantity { get; set; } = 1;

    private WeightedDropTable<RewardDropDefinition>? _table;
    public WeightedDropTable<RewardDropDefinition> Table => _table ??= new WeightedDropTable<RewardDropDefinition>(DropTable);
}
