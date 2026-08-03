using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

using static Sanctuary.Gateway.Handlers.AbilityPacketClientRequestStartAbilityHandler;

namespace Sanctuary.Gateway.Handlers.Abilities;

// Combat orbs/spheres/grenades (CategoryId 14 - see CombatOrbAbilities) - the wiki's "potion belt"
// battle items: thrown at a target to apply a real status effect or plain damage. Extracted from
// AbilityPacketClientRequestStartAbilityHandler as the fourth migrated category (PR #27 review). Logic
// is unchanged from before the move - only the shared internal statics it reaches back into
// (_resourceManager, _logger, IsOnCooldown, StartCooldown, SendFailure, ConsumeItem, IconTintId) live
// on the old handler.
public sealed class CombatOrbAbility : IConsumableAbility
{
    // Throw animation for a combat orb - reuses "air_throw" (1033), the same real thrown-item animation
    // Ninja's Fan of Blades/1000 Storms already use elsewhere in this codebase. Not independently confirmed
    // for battle-item use specifically (no dedicated "use potion belt item" animation was found), but it's
    // a real, already-proven throw motion rather than a guessed id.
    private const int OrbThrowAnim = 1033;

    public bool Matches(ClientItemDefinition itemDefinition) =>
        itemDefinition.CategoryId == 14 && CombatOrbAbilities.TryResolve(itemDefinition.Comment, out _);

    public bool Handle(GatewayConnection connection, AbilityPacketClientRequestStartAbility packet, int slot, ClientItem clientItem, ClientItemDefinition itemDefinition)
    {
        CombatOrbAbilities.TryResolve(itemDefinition.Comment, out var orb);

        var player = connection.Player;
        var zone = player.Zone;

        if (IsOnCooldown(player.Guid, itemDefinition.Id))
            return SendFailure(connection);

        // Same target resolution as a combat ability: honor the client's selected enemy, else nearest
        // live hostile within the auto-target reach.
        Npc? target = null;
        if (packet.Guid != 0 && zone.TryGetNpc(packet.Guid, out var selected) && selected.IsDamageable && selected.IsAlive)
        {
            target = selected;
        }
        else
        {
            var reach2 = JobWeaponAbilities.AutoTargetReach(player);
            reach2 *= reach2;
            var best2 = reach2;
            foreach (var n in zone.Npcs)
            {
                if (!n.IsHostile || !n.IsDamageable || !n.IsAlive)
                    continue;
                var dx = n.Position.X - player.Position.X;
                var dz = n.Position.Z - player.Position.Z;
                var d2 = dx * dx + dz * dz;
                if (d2 >= best2)
                    continue;
                best2 = d2;
                target = n;
            }
        }

        if (target is null)
            return SendFailure(connection); // no target in range - the orb isn't thrown/consumed

        player.EnterWorldCombat();

        player.SendTunneledToVisible(new AbilityPacketStartCasting
        {
            Unknown = player.Guid,
            Unknown2 = target.Guid,
            Animation = OrbThrowAnim,
            AbilityId = slot + 1,
            ActionTime = 0.4f,
        }, sendToSelf: true);

        var fxId = CombatOrbAbilities.ImpactFxFor(orb.Effect);

        if (orb.Effect == OrbEffect.Damage)
        {
            var killed = target.ApplyDamage(orb.Damage);

            player.SendTunneledToVisible(new AbilityPacketDetonateProjectile
            {
                Guid = target.Guid,
                CompositeEffectId = fxId,
            }, sendToSelf: true);

            player.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
            {
                Guid = player.Guid,
                Guid2 = target.Guid,
                Unknown = true,
                Unknown2 = target.MaxHealth,
                Unknown3 = target.Health,
                Unknown4 = -orb.Damage,
            }, sendToSelf: true);

            if (killed)
                player.Zone.OnNpcKilled(player, target);
            else
                player.Zone.OnNpcDamaged(player, target);
        }
        else
        {
            player.SendTunneledToVisible(new AbilityPacketDetonateProjectile
            {
                Guid = target.Guid,
                CompositeEffectId = fxId,
            }, sendToSelf: true);

            var kind = CombatOrbAbilities.ToStatusEffectKind(orb.Effect);
            if (kind is { } k)
                StatusEffects.Apply(target, k, orb.DurationMs, source: player);
        }

        _logger.LogTrace("Combat orb: {name} ({effect}) used by {who} on {target}.",
            itemDefinition.Comment, orb.Effect, player.Name, target.Name);

        const int OrbCooldownMs = 15000; // no wiki/data source for the real belt-slot cooldown - a reasonable guess
        StartCooldown(player.Guid, itemDefinition.Id, OrbCooldownMs);

        var count = clientItem.Count;
        var hasItemLeft = !itemDefinition.SingleUse || count > 1;

        if (itemDefinition.SingleUse)
            ConsumeItem(connection, clientItem, itemDefinition, slot);

        if (hasItemLeft)
            player.StartActionBarCooldown(2, slot, itemDefinition.Icon.Id, itemDefinition.NameId,
                itemDefinition.SingleUse ? count - 1 : count, OrbCooldownMs, IconTintId(clientItem, itemDefinition));

        return true;
    }
}
