using System;
using System.Collections.Concurrent;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Interactions;

// The Snowball Battles GUARD - the arena's second toolbar slot (the "2" key). Puts a shield bubble up for
// a few seconds; snowballs that land on a guarded player are absorbed, so they aren't knocked down and the
// thrower's team scores nothing.
//
// Retail behaviour, from video: a blocking ability with an orange/red circular effect around the player for
// the duration. The arena bar is 0 = snowball, 1 = guard, 2 = power-ups.
public static class SnowballGuard
{
    // Where it sits on the arena toolbar (the "2" key). See JobWeaponAbilities.BuildSnowballArenaToolbar.
    public const int ToolbarSlotIndex = 1;

    // How long the bubble holds, and how long before it can go up again. Retail's numbers aren't recorded
    // anywhere in the client data, so these are tuned by feel: long enough to survive a volley, short
    // enough that you can't just walk the pitch permanently shielded. `/snowball guard <ms> <cooldownMs>`
    // retunes both live.
    public static int DurationMs { get; set; } = 5_000;
    public static int CooldownMs { get; set; } = 15_000;

    // ★ The shield bubble: `PFX_shield_gold_sphere_medium_loop` - a SPHERE around the player, which is
    // the forcefield look, and gold/warm so it still reads as the orange-ish bubble in the video.
    //
    // Two earlier picks were wrong, both by matching a word in the name instead of the shape:
    //   5031  PFX_barrier-fire_orange_loop                  - matched "orange ring", but it is a ring of
    //                                                          FIRE, which reads as an attack buff.
    //   16436 PFX_shields-four_gold_..._no-fade             - four discrete shields ORBITING the body,
    //                                                          not an enclosing field.
    // Alternatives if this still isn't it: **53 PFX_snowflakes_white_sphere_loop** (a sphere of
    // snowflakes - the most on-theme option for a snow fight), 16124 shield_purple_lg wizard-protective-
    // barrier, 5049 shield_swirl_blue_barrier, 5055 ice_elemental_barrier.
    //
    // ★ Whatever is chosen must be a `_loop`: it is attached by tag and held for the guard's duration, so
    // a one-shot would flash and vanish while the block was still up.
    public static int BubbleFxId { get; set; } = 16437;

    // Attached by tag so it rides the player and can be pulled off the instant the guard drops, rather
    // than being a one-shot that outlives it.
    private const int BubbleTagId = 91021;

    // "Guard" - the real localized string. ("Block" 432823 and "Shield" 438417 also exist if this reads
    // wrong; retail's own choice for this button isn't recorded.)
    public const int NameId = 40577;

    // ★ RAW IMAGE id, not an image-set id - toolbar slot icons live in the same raw-image space the job
    // kits' weapon icons do, and the _32 size is what those use. icon_abil_demo_shield_32 is the shield
    // art the Demolition Derby minigame uses; nothing snowball-specific exists (there is no
    // icon_abil_*snow* in the whole table).
    public const int IconId = 11981;

    private static readonly ConcurrentDictionary<ulong, DateTime> _guarding = new();
    private static readonly ConcurrentDictionary<ulong, DateTime> _cooldowns = new();

    public static bool IsGuarding(Player player) =>
        _guarding.TryGetValue(player.Guid, out var until) && DateTime.UtcNow < until;

    public static AbilityPacketSetDefinition.Slot MakeToolbarSlot() => new()
    {
        Type = 3,
        ManaCost = 0,
        IconId = IconId,
        NameId = NameId,
        AbilityDefinitionId = 0,
    };

    // The "2" key was pressed. False when it's still on cooldown, which the caller reports back as an
    // ability failure.
    public static bool TryGuard(Player player)
    {
        var now = DateTime.UtcNow;

        if (_cooldowns.TryGetValue(player.Guid, out var readyAt) && now < readyAt)
            return false;

        _cooldowns[player.Guid] = now.AddMilliseconds(CooldownMs);
        _guarding[player.Guid] = now.AddMilliseconds(DurationMs);

        // The bubble, attached to the player so everyone who can see them sees it too.
        var bubble = new PlayerUpdatePacketAddEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = BubbleTagId,
            CompositeEffectId = BubbleFxId,
            SourceGuid = player.Guid,
        };

        player.SendTunneledToVisible(bubble, sendToSelf: true);

        // Grey the button and sweep it. The guard doesn't target anyone, so it borrows the nearest opponent
        // purely to make the sweep resolve - see SnowballTool.SendCooldown.
        SnowballTool.SendCooldown(player, CooldownMs, 0);

        // Drop the bubble when the guard expires. A later guard can't be in flight yet (the cooldown is
        // longer than the duration), so this doesn't need the ticket dance SnowballTool.Give uses.
        var expiresAt = _guarding[player.Guid];

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(DurationMs);

                if (_guarding.TryGetValue(player.Guid, out var current) && current != expiresAt)
                    return; // superseded

                _guarding.TryRemove(player.Guid, out _);

                player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                {
                    Guid = player.Guid,
                    TagId = BubbleTagId,
                }, sendToSelf: true);
            }
            catch { }
        });

        return true;
    }

    // Called when a player leaves the arena - a looping attached effect is held until something removes it,
    // and a guard left running would follow them out.
    public static void Clear(Player player)
    {
        if (!_guarding.TryRemove(player.Guid, out _))
            return;

        player.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
        {
            Guid = player.Guid,
            TagId = BubbleTagId,
        }, sendToSelf: true);
    }
}
