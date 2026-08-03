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

    // The active job's weapon toolbar (op36/5), or null. ALWAYS carries the held power-up slot (if any) -
    // see PowerupSystem.MakeHeldSlot - not just when explicitly sent via SendToolbarWithPowerup below.
    //
    // FIXED 2026-07-29 (live feedback: "still cannot pick up Flame Wave/Earth Shard/Super Shield even after
    // not having it"): PowerupSystem._held is a static dictionary that only ever gets CLEARED by TryUse
    // (pressing "3") - it has no expiry and isn't touched by zone transitions, job swaps, or level-ups. But
    // the held-slot ICON only ever got attached by the two callers that explicitly asked for it (Grant/
    // TryUse's own SendToolbarWithPowerup calls) - every OTHER toolbar refresh in the game (dungeon entry's
    // SendToolbarWithFxPreload, weapon-swap, job-switch, Player.RestoreWeaponToolbar after a level-up) went
    // through this method directly and silently OMITTED the slot. The very first unrelated toolbar refresh
    // after picking one up would erase its visible icon from the player's screen while _held stayed set
    // server-side - the player then has zero indication they're holding anything, yet every later pickup of
    // a held-type kind keeps failing as "already holding" with nothing to visibly point at. Moving the
    // append here (the single choke point every caller already goes through) means it can never desync
    // again, instead of trying to find and patch every individual toolbar-send call site.
    public static AbilityPacketSetDefinition? BuildToolbar(Player player, IResourceManager resources)
    {
        var def = JobKits.Active(player)?.BuildToolbar(player, resources);
        if (def is not null && PowerupSystem.MakeHeldSlot(player.Guid) is { } powerupSlot)
            def.Slots.Add(powerupSlot);
        return def;
    }

    // Same toolbar; BuildToolbar above already carries the held power-up slot (if any) whenever a job kit
    // exists, so this only needs its own fallback for the no-kit case. Kept as a separate method (rather
    // than folding call sites into plain BuildToolbar+SendTunneled) since PowerupSystem.Grant/TryUse want to
    // guarantee the slot shows even for a player with no active combat job.
    public static void SendToolbarWithPowerup(Player player, IResourceManager resources)
    {
        var def = BuildToolbar(player, resources);
        if (def is null)
        {
            def = AbilityPacketSetDefinition.CreateEmpty(player.ActiveProfileId);
            if (PowerupSystem.MakeHeldSlot(player.Guid) is { } powerupSlot)
                def.Slots.Add(powerupSlot);
        }

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
