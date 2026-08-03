using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

using static Sanctuary.Gateway.Handlers.AbilityPacketClientRequestStartAbilityHandler;

namespace Sanctuary.Gateway.Handlers.Abilities;

// Health/Energy/Replenishment potions (CategoryId 9 - see PotionAbilities) - the "potion belt"
// mechanic's self/group-restore half. CategoryId 9 is a broad bucket (also holds scrolls, buff items,
// unrelated potions), so this only matches items whose real name resolves to a known heal/energy
// potion - see PotionAbilities.TryResolve's suffix match. Extracted from
// AbilityPacketClientRequestStartAbilityHandler as the fifth migrated category (PR #27 review). Logic
// is unchanged from before the move, EXCEPT the heal-shower FX cleanup: that used to spawn a background
// Task.Delay to remove the tag, the same anti-pattern flagged (and fixed, live-confirmed) for Silly
// String's cooldown - swapped to Player.SendTunneledToVisibleDelayed here too while moving this code.
public sealed class CombatPotionAbility : IConsumableAbility
{
    public bool Matches(ClientItemDefinition itemDefinition) =>
        itemDefinition.CategoryId == 9 && PotionAbilities.TryResolve(itemDefinition.Comment, out _);

    public bool Handle(GatewayConnection connection, AbilityPacketClientRequestStartAbility packet, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition)
    {
        PotionAbilities.TryResolve(itemDefinition.Comment, out var potion);

        var player = connection.Player;

        if (IsOnCooldown(player.Guid, itemDefinition.Id))
            return SendFailure(connection);

        player.SendTunneledToVisible(new AbilityPacketStartCasting
        {
            Unknown = player.Guid,
            Unknown2 = player.Guid,
            Animation = PotionAbilities.DrinkAnimId,
            AbilityId = slot + 1,
            ActionTime = 0.4f,
        }, sendToSelf: true);

        // Shared potions heal nearby allies within a radius (see PotionAbilities.SharedRadius) - NOT every
        // player in the zone instance, which would hand a free heal to unrelated strangers in an open-world
        // town. Self is always included regardless of distance.
        List<Player> targets;
        if (potion.Shared && player.Zone is { } potionZone)
        {
            var c = player.Position;
            var r2 = PotionAbilities.SharedRadius * PotionAbilities.SharedRadius;
            targets = potionZone.Players
                .Where(p => ReferenceEquals(p, player) ||
                    ((p.Position.X - c.X) * (p.Position.X - c.X) + (p.Position.Z - c.Z) * (p.Position.Z - c.Z)) <= r2)
                .ToList();
        }
        else
        {
            targets = [player];
        }

        foreach (var target in targets)
        {
            if (potion.Effect is PotionEffect.Health or PotionEffect.Replenishment)
            {
                // Live feedback 2026-07-27: "i dont see my health moving when using potions" - the packet
                // below is only the floating "+N" combat text, it never touched CurrentHitpoints. Player.Heal
                // is the real HP-bar update (same packets TakeDamage/RegenTick use). HealPercent (not a flat
                // amount) so the potion scales with each player's own max HP instead of favoring low-HP
                // players (live feedback: "make sure health potions ... scale for all players").
                var healedAmount = target.HealPercent(PotionAbilities.HealFraction);
                var maxHpStat = target.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 0;
                target.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
                {
                    Guid = target.Guid,
                    Guid2 = target.Guid,
                    Unknown = true,
                    Unknown2 = maxHpStat,
                    Unknown3 = target.CurrentHitpoints,
                    Unknown4 = healedAmount,
                }, sendToSelf: true);
                // CORRECTED 2026-07-28 (live feedback: "health effects seems to be stuck in the world
                // during dungeon playthrough") - PotionAbilities.HealFxId (15921) is the SAME "_loop_" heal-
                // shower asset the Health power-up/heart pickups use, fired here as a one-shot world-
                // positioned trigger with no stop - same bug class as Volley's rain FX and the Health
                // power-up before their own fixes. Tag-attach it to the drinker and remove it after a hold,
                // instead of leaving a looping effect parked at the exact spot they happened to drink it.
                var healTagId = System.Threading.Interlocked.Increment(ref _castFxTagCounter);
                target.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
                {
                    Guid = target.Guid,
                    TagId = healTagId,
                    CompositeEffectId = PotionAbilities.HealFxId,
                    SourceGuid = target.Guid,
                }, sendToSelf: true);
                target.SendTunneledToVisibleDelayed(
                    new PlayerUpdatePacketRemoveEffectTagCompositeEffect { Guid = target.Guid, TagId = healTagId },
                    PotionAbilities.HealShowerMs, sendToSelf: true);
            }

            if (potion.Effect is PotionEffect.Energy or PotionEffect.Replenishment)
            {
                // Only the DRINKER'S OWN energy pool is ours to touch directly here (the private _energy
                // dict is keyed per-player, same mechanism PowerupSystem.RequestEnergyRefill bridges into -
                // this handler already owns it, no bridge needed for our own case). Party members' energy
                // still gets the visual FX below even though we can't authoritatively refill a DIFFERENT
                // connection's bar from here without its own GatewayConnection - a real gap for "Shared"
                // energy/replenishment potions used on someone else, flagged rather than silently skipped.
                if (target.Guid == player.Guid)
                {
                    _energy[player.Guid] = MaxEnergy;
                    SendEnergy(player, MaxEnergy);
                }
                target.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                {
                    Guid = target.Guid,
                    CompositeEffectId = PotionAbilities.EnergyFxId,
                    Position = target.Position,
                }, sendToSelf: true);
            }
        }

        const int PotionCooldownMs = 10000; // no wiki/data source for the real per-potion cooldown - a reasonable guess
        StartCooldown(player.Guid, itemDefinition.Id, PotionCooldownMs);

        var count = clientItem.Count;
        var hasItemLeft = !itemDefinition.SingleUse || count > 1;

        if (itemDefinition.SingleUse)
            ConsumeItem(connection, clientItem, itemDefinition, slot);

        if (hasItemLeft)
            player.StartActionBarCooldown(2, slot, itemDefinition.Icon.Id, itemDefinition.NameId,
                itemDefinition.SingleUse ? count - 1 : count, PotionCooldownMs, IconTintId(clientItem, itemDefinition));

        return true;
    }
}
