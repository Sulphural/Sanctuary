using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Combat;

// Job-agnostic front door for the combat kits. Everything routes through the active player's IJobKit (see
// JobKits), so zone-load / job-swap / weapon-equip / ability-press don't care which job it is.
public static class JobWeaponAbilities
{
    // Does the active job have a weapon-ability kit?
    public static bool HasKit(Player player) => JobKits.Has(player.ActiveProfileId);

    // The active job's weapon toolbar (op36/5), or null.
    public static AbilityPacketSetDefinition? BuildToolbar(Player player, IResourceManager resources) =>
        JobKits.Active(player)?.BuildToolbar(player, resources);

    // Same toolbar, with the held power-up (if any) pinned at slot index 2 (the "3" key) - see
    // PowerupSystem. The normal toolbar only ever populates indices 0/1 (basic/special), so appending
    // lands exactly at index 2 the way the wire format expects (see AbilityPacketSetDefinition.Serialize).
    public static void SendToolbarWithPowerup(Player player, IResourceManager resources)
    {
        var def = BuildToolbar(player, resources) ?? AbilityPacketSetDefinition.CreateEmpty(player.ActiveProfileId);

        if (PowerupSystem.MakeHeldSlot(player.Guid) is { } powerupSlot)
            def.Slots.Add(powerupSlot);

        player.SendTunneled(def);
    }

    // Resolve a pressed slot; jobs without a kit fall back to the ninja bare-hand strike.
    public static WeaponAbility ResolveAbility(Player player, int slot) =>
        JobKits.Active(player)?.ResolveAbility(player, slot) ?? NinjaWeaponAbilities.ResolveAbility(player, slot);

    // A client AbilityDefinition request (op36/12) -> name/desc/icon for the AbilitiesScreen columns.
    public static (int NameId, int DescId, int IconId)? ResolveAbilityDefinition(Player player, int abilityDefId) =>
        JobKits.Active(player)?.ResolveDefinition(player, abilityDefId);

    // Bow range for archers, melee envelope otherwise.
    public static float AutoTargetReach(Player player) => JobKits.Active(player)?.AutoTargetReach ?? 7f;

    // Send the toolbar and warm its FX cache. False when the job has no kit.
    public static bool SendToolbarWithFxPreload(Player player, IResourceManager resources)
    {
        var toolbar = BuildToolbar(player, resources);
        if (toolbar is null)
            return false;

        // Seed the def map BEFORE the toolbar — the client requests a def per slot the instant it reads the
        // toolbar and won't re-check, so the defs must already be present for the columns to resolve.
        PreloadAbilityDefinitions(player);
        player.SendTunneled(toolbar);
        PreloadAbilityEffects(player);
        return true;
    }

    // Push the equipped weapon's ability definitions up front, before the AbilitiesScreen opens.
    public static void PreloadAbilityDefinitions(Player player)
    {
        var kit = JobKits.Active(player);
        if (kit is null)
            return;

        foreach (var defId in kit.SlotAbilityDefIds)
        {
            var def = kit.ResolveDefinition(player, defId);
            if (def is null)
                continue;

            player.SendTunneled(new AbilityPacketAbilityDefinition
            {
                AbilityId = defId,
                NameId = def.Value.NameId,
                DescriptionId = def.Value.DescId,
                IconId = def.Value.IconId,
            });
        }
    }

    // Warm the FX cache: most composite effects load on demand, so the first play renders nothing. Play each of
    // the equipped weapon's effects once, far below the player, so the first real cast shows immediately.
    public static void PreloadAbilityEffects(Player player)
    {
        var ids = new HashSet<int>();
        for (var slot = 0; slot <= 1; slot++)
        {
            var ability = ResolveAbility(player, slot);
            ids.Add(ability.EffectId);
            // Lingering trail loops (CastEffectStopMs > 0) have no stop when unattached, so warming them would
            // leave one sitting under the map forever — they cache on their first tag-play instead.
            if (ability.CastEffectStopMs == 0)
                ids.Add(ability.CastEffectId);
            ids.Add(ability.CasterEndEffectId);
            ids.Add(ability.EnemyExtraEffectId);
            ids.Add(ability.SwordEffectId);
        }

        var warmPos = new Vector4(player.Position.X, player.Position.Y - 400f, player.Position.Z, 1f);

        foreach (var id in ids)
        {
            if (id <= 0)
                continue;

            player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = 0, // world-positioned, not attached to an actor
                CompositeEffectId = id,
                Position = warmPos,
            });
        }

    }
}
