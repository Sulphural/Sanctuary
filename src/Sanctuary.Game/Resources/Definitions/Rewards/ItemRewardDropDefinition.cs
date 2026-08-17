using System.Collections.Generic;

using Sanctuary.Core.Collections;

namespace Sanctuary.Game.Resources.Definitions.Rewards;

public sealed class ItemRewardDropDefinition : RewardDropDefinition
{
    public int ItemDefinitionId { get; set; }

    // Currency drops already carry an Amount; without this an item drop can only ever grant one.
    public int Quantity { get; set; } = 1;
    public List<TintDropDefinition> Tints { get; set; } = [];

    private WeightedDropTable<TintDropDefinition>? _tintTable;
    public WeightedDropTable<TintDropDefinition>? TintTable => Tints.Count == 0
        ? null
        : _tintTable ??= new WeightedDropTable<TintDropDefinition>(Tints);
}
