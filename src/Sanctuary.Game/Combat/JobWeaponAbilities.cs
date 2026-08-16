using System;
using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
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
        // Snowball Battles overrides the bar entirely - see BuildSnowballArenaToolbar.
        if (BuildSnowballArenaToolbar(player) is { } arenaBar)
            return arenaBar;

        var def = JobKits.Active(player)?.BuildToolbar(player, resources);
        if (def is not null)
            ApplyThirdSlot(player, def);
        return def;
    }

    // ★ THE ARENA BAR IS FIXED AND JOB-INDEPENDENT: 0 = throw a snowball, 1 = guard, 2 = the pile
    // power-up - i.e. the "1", "2" and "3" keys, which is retail's layout (the first-time-event text says
    // "Hit the [1] key to throw snowballs", and the fanbyte wiki puts the pile power on "3").
    //
    // This is why it can't just be the normal bar plus extras: outside the arena the snowball tool sits on
    // slot 2 alongside a job's attack/special, but in here the job is forced to Adventurer (no kit, so no
    // attack or special of its own) and the throw has to move to slot 0. Returns null when the player
    // isn't in the arena, so every other zone is untouched.
    private static AbilityPacketSetDefinition? BuildSnowballArenaToolbar(Player player)
    {
        if (player.Zone is not Zones.SnowballArenaZone)
            return null;

        var def = AbilityPacketSetDefinition.CreateEmpty(player.ActiveProfileId);

        // ★ THE ARENA SLOTS CARRY REAL ABILITY-DEFINITION IDS. They used to be 0 (copying
        // PowerupSystem's held slot), which means the client has NO op36/13 definition behind them - and
        // the cooldown radial is driven off that definition, so a slot with none can't render one at all.
        // The definitions themselves are seeded by SendArenaAbilityDefinitions, BEFORE the toolbar: the
        // client asks for a def the instant it reads a slot and won't re-check.
        SendArenaAbilityDefinitions(player);

        // Positional serialization: every slot up to the highest one used has to exist, so an unheld
        // power-up leaves an empty placeholder rather than shifting guard onto the "3" key.
        var throwSlot = SnowballTool.MakeToolbarSlot(player) ?? EmptySlot();
        throwSlot.AbilityDefinitionId = ArenaThrowAbilityId;
        // The last slot is the PILE SPECIAL (Power or Freezing), not the generic combat power-up:
        // in this minigame the piles ARE the power-ups.
        var specialSlot = SnowballSpecials.MakeToolbarSlot(player) ?? EmptySlot();
        specialSlot.AbilityDefinitionId = ArenaSpecialAbilityId;

        def.Slots.Add(throwSlot);                       // 0 - the "1" key
        var guardSlot = SnowballGuard.MakeToolbarSlot();
        guardSlot.AbilityDefinitionId = ArenaGuardAbilityId;

        def.Slots.Add(guardSlot);                       // 1 - the "2" key
        def.Slots.Add(specialSlot);                     // 2 - the "3" key

        return def;
    }

    private static AbilityPacketSetDefinition.Slot EmptySlot() => new() { Type = 0 };

    // Ability-definition ids for the three arena slots. Arbitrary but stable, and well clear of any real
    // ability id.
    public const int ArenaThrowAbilityId = 990001;
    public const int ArenaGuardAbilityId = 990002;
    public const int ArenaSpecialAbilityId = 990003;

    // ★ THE COOLDOWN-RADIAL EXPERIMENT. Which op36/13 field feeds the ability button's sweep end-time was
    // never pinned down - the candidates were narrowed to these float offsets and the packet already
    // exposes them as Probe* for exactly this. Each is set to the ability's cooldown IN SECONDS.
    //
    // ★★ SETTLED 2026-08-15: NONE OF THE EIGHT DRIVE IT. Tested one at a time at 15s against a zeroed
    // baseline, on a guard button that was verifiably receiving its cooldown (i.e. AFTER the StartCasting
    // slot-naming fix - the earlier all-eight-at-once test predated that and was invalid, which is why it
    // was worth re-running). Every field: still the same ~1s sweep. No field suppressed it either.
    //
    // That closes the last server-side lever this codebase had identified for the sweep's LENGTH. What the
    // server CAN do is already done and correct: MeleeRefresh sets the true cooldown-end (so the button
    // greys for the real duration), StartCasting names the slot, LaunchAndLand renders the sweep. Only the
    // sweep's ~1s animation length is beyond reach, and it is a client-side constant.
    //
    // DO NOT re-run this probe. If it is ever revisited, the only untried lever left is LaunchAndLand's
    // empty list field (+0x18) - which has a client-crash history - and past that it is an exe change,
    // which the standing no-client-patches rule bars.
    //
    // ProbeField: null = every field (the old blunt behaviour), "none" = leave them all zero, otherwise the
    // hex offset name of the single field to set ("44", "48", "6c", "78", "7c", "8c", "90", "a8").
    public static string? ProbeField { get; set; } = "none";

    // Overrides the per-ability cooldown when probing, so a 2s throw can be given an obviously-long value.
    public static float? ProbeSeconds { get; set; }

    public static readonly string[] ProbeFields = ["44", "48", "6c", "78", "7c", "8c", "90", "a8"];

    private static void SendArenaAbilityDefinitions(Player player)
    {
        Send(ArenaThrowAbilityId, SnowballTool.ToolNameId, SnowballTool.ToolIconId, SnowballTool.ThrowCooldownMs / 1000f);
        Send(ArenaGuardAbilityId, SnowballGuard.NameId, SnowballGuard.IconId, SnowballGuard.CooldownMs / 1000f);

        if (SnowballSpecials.TryGetHeld(player, out var kind))
        {
            Send(ArenaSpecialAbilityId,
                SnowballSpecials.NameIdFor(kind),
                SnowballSpecials.IconIdFor(kind),
                SnowballSpecials.CooldownMs / 1000f);
        }

        void Send(int abilityId, int nameId, int iconId, float cooldownSeconds)
        {
            var seconds = ProbeSeconds ?? cooldownSeconds;

            var definition = new AbilityPacketAbilityDefinition
            {
                AbilityId = abilityId,
                NameId = nameId,
                IconId = iconId,
            };

            void Set(string field, float value)
            {
                switch (field)
                {
                    case "44": definition.Probe44 = value; break;
                    case "48": definition.Probe48 = value; break;
                    case "6c": definition.Probe6c = value; break;
                    case "78": definition.Probe78 = value; break;
                    case "7c": definition.Probe7c = value; break;
                    case "8c": definition.Probe8c = value; break;
                    case "90": definition.Probe90 = value; break;
                    case "a8": definition.ProbeA8 = value; break;
                }
            }

            if (ProbeField is null)
            {
                foreach (var field in ProbeFields)
                    Set(field, seconds);
            }
            else if (!string.Equals(ProbeField, "none", StringComparison.OrdinalIgnoreCase))
            {
                Set(ProbeField.ToLowerInvariant(), seconds);
            }

            player.SendTunneled(definition);
        }
    }

    // Two EXTRA slots on top of the job's own attack (0) and special (1):
    //   index 2 - the "3" key - a held combat POWER-UP (transient; gone the moment it's pressed)
    //   index 3 - the "4" key - the SNOWBALL TOOL, for anyone who has picked one up in Snowhill
    //
    // They used to share index 2 and take precedence over each other, which meant picking up a power-up
    // hid the snowball tool. They're separate keys now, so both can be held at once.
    //
    // ★ Assigned BY INDEX, not appended. Slots serialize positionally (see AbilityPacketSetDefinition), so
    // appending only lands on the right index when the kit happens to have contributed exactly that many
    // slots - on the empty no-kit toolbar an append would put it on index 0, i.e. the "1" key.
    private static void ApplyThirdSlot(Player player, AbilityPacketSetDefinition def)
    {
        Assign(PowerupSystem.MakeHeldSlot(player.Guid), PowerupSlotIndex);
        Assign(SnowballTool.MakeToolbarSlot(player), SnowballTool.ToolbarSlotIndex);

        void Assign(AbilityPacketSetDefinition.Slot? slot, int index)
        {
            if (slot is null)
                return;

            // Positional serialization: every slot up to this one has to exist, or a gap shifts everything
            // after it onto the wrong key.
            while (def.Slots.Count < index)
                def.Slots.Add(new AbilityPacketSetDefinition.Slot { Type = 0 });

            if (def.Slots.Count == index)
                def.Slots.Add(slot);
            else
                def.Slots[index] = slot;
        }
    }

    // The "3" key - held combat power-ups.
    public const int PowerupSlotIndex = 2;

    // Same toolbar; BuildToolbar above already carries the third slot whenever a job kit exists, so this
    // only needs its own fallback for the no-kit case. Kept as a separate method (rather than folding call
    // sites into plain BuildToolbar+SendTunneled) since PowerupSystem.Grant/TryUse and the snowball pickup
    // want to guarantee the slot shows even for a player with no active combat job.
    public static void SendToolbarWithPowerup(Player player, IResourceManager resources)
    {
        var def = BuildToolbar(player, resources);
        if (def is null)
        {
            def = AbilityPacketSetDefinition.CreateEmpty(player.ActiveProfileId);
            ApplyThirdSlot(player, def);
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
        SnowballTool.PreloadEffects(player); // no-op unless they're carrying one
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
