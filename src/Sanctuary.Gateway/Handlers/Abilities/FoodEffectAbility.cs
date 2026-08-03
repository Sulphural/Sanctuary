using Sanctuary.Packet;
using Sanctuary.Packet.Common;

using static Sanctuary.Gateway.Handlers.AbilityPacketClientRequestStartAbilityHandler;

namespace Sanctuary.Gateway.Handlers.Abilities;

// Food items with a plain visual/chat effect and no other mechanic (see FoodEffectDefinition) -
// TriggerAbilityEffect is shared with the old handler's own generic fallback (an item with no
// recognized category at all), so it stays on the old handler rather than moving here. Extracted from
// AbilityPacketClientRequestStartAbilityHandler as the seventh migrated category (PR #27 review).
public sealed class FoodEffectAbility : IConsumableAbility
{
    private const int FoodEffectCooldownMs = 120_000;

    public bool Matches(ClientItemDefinition itemDefinition) =>
        _resourceManager.Consumables.FoodEffects.ContainsKey(itemDefinition.ActivatableAbilityId);

    public bool Handle(GatewayConnection connection, AbilityPacketClientRequestStartAbility packet, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition)
    {
        if (IsOnCooldown(connection.Player.Guid, itemDefinition.Id))
            return SendFailure(connection);

        StartCooldown(connection.Player.Guid, itemDefinition.Id, FoodEffectCooldownMs);

        TriggerAbilityEffect(connection, itemDefinition);

        var count = clientItem.Count;
        var hasItemLeft = !itemDefinition.SingleUse || count > 1;

        if (itemDefinition.SingleUse)
            ConsumeItem(connection, clientItem, itemDefinition, slot);

        if (hasItemLeft)
            connection.Player.StartActionBarCooldown(2, slot, itemDefinition.Icon.Id, itemDefinition.NameId,
                itemDefinition.SingleUse ? count - 1 : count, FoodEffectCooldownMs, IconTintId(clientItem, itemDefinition));

        return true;
    }
}
