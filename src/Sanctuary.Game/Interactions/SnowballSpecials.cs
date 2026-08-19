using System;
using System.Collections.Concurrent;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Game.Interactions;

// The Snowball Battles SPECIALS - what the piles hand out, and what sits in the arena bar's last slot
// (index 2, the "3" key).
//
// Retail: "two-charge" special powers from snowball piles, TWO piles on each side of the field
// (legacy.fanbyte.com/wiki/fr_minigame:Snowball_Fighting). Each team's pair is POWER and FREEZING, and a
// player picks whichever they want - taking from the other pile replaces what they were holding, so the
// choice is one-at-a-time rather than a stockpile of both.
//
// All four names/piles are real client strings:
//   Power    snowball 419562 · pile 419529 · buff 419560
//   Freezing snowball 41334  · pile 419550 · buff 419568
public static class SnowballSpecials
{
    public enum SpecialKind
    {
        Power,
        Freezing,
    }

    // The arena bar's last slot - see JobWeaponAbilities.BuildSnowballArenaToolbar.
    public const int ToolbarSlotIndex = 2;

    // ★ A SPECIAL IS NOT CONSUMED. It stays on the bar once picked up and simply goes on a long cooldown
    // when thrown - the only way to change what you're holding is to walk to the other pile. (The wiki
    // describes retail's as "two-charge"; a persistent one on a long timer is the behaviour asked for
    // here, and it keeps the slot from silently emptying mid-fight.)
    //
    // The cooldown deliberately SURVIVES a swap, so hopping between the two piles can't be used to fire a
    // special twice in a row. Tunable live with `/snowball special <ms>`.
    public static int CooldownMs { get; set; } = 25_000;

    // ── Power ─────────────────────────────────────────────────────────────────────────────────────────
    // Hits harder: a longer knockdown than a plain snowball, so a hit takes the victim out of the fight
    // for meaningfully longer.
    public const int PowerNameId = 419562;          // "Power Snowball"
    public const int PowerPileNameId = 419529;      // "Power Snowball Pile"
    public const int PowerIconId = 2617;            // icon_abil_brawler_rockthrow_32 (raw image id)
    public const int PowerBadgeId = 241;            // NotificationImages: bubble + rockthrow_64 (2511)
    public const int PowerBlastFxId = 16172;        // PFX_ice_white_explosion_lg_wizard-ice-nova
    public const int PowerStunMs = 5_000;

    // ── Freezing ──────────────────────────────────────────────────────────────────────────────────────
    // Freezes rather than knocks down - the Freeze status kind already exists in StatusEffects.
    public const int FreezingNameId = 41334;        // "Freezing Snowball"
    public const int FreezingPileNameId = 419550;   // "Freezing Snowball Pile"
    public const int FreezingIconId = 9203;         // icon_item_blue_hot_spring_ice_cube_32 (raw image id)
    public const int FreezingBadgeId = 240;         // NotificationImages: bubble + ice_cube_64 (9204)
    public const int FreezeLoopFxId = 5337;         // PFX_ice-cube_blue_freeze_loop - frozen solid
    public const int ThawFxId = 5196;               // PFX_ice-mist_white_explosion_small - the thaw
    private const int FreezeTagId = 91022;
    public const int FreezingStunMs = 6_000;

    // ★ Both icons are RAW IMAGE ids at _32, the size the job kits' own slot icons use - NOT image-set ids.
    //
    // ★★ AND THEY ARE NOT GUESSES: NotificationImages 239/240/241 are the snowball-fight badge TRIO, sitting
    // together right after the rest of the snowball block - 239 = bubble + icon_event_snowball_fights_64
    // (the generic one Calvin and the Snowhill piles wear), 240 = bubble + icon_item_blue_hot_spring_ice_cube_64,
    // 241 = bubble + icon_abil_brawler_rockthrow_64. So retail itself pairs FREEZING with the ice cube and
    // POWER with the rock throw; the toolbar icons here are just the _32 siblings of that same art, and the
    // piles wear the matching badges. (An earlier guess of the archer's freezing-arrow icon for Freezing was
    // wrong - the ice cube is the one on screen.)

    private static readonly ConcurrentDictionary<ulong, SpecialKind> _held = new();
    private static readonly ConcurrentDictionary<ulong, DateTime> _cooldowns = new();

    public static bool TryGetHeld(Player player, out SpecialKind kind) => _held.TryGetValue(player.Guid, out kind);

    public static bool IsOnCooldown(Player player) =>
        _cooldowns.TryGetValue(player.Guid, out var readyAt) && DateTime.UtcNow < readyAt;

    public static int NameIdFor(SpecialKind kind) =>
        kind == SpecialKind.Power ? PowerNameId : FreezingNameId;

    public static int IconIdFor(SpecialKind kind) =>
        kind == SpecialKind.Power ? PowerIconId : FreezingIconId;

    // Taking from a pile REPLACES whatever was held - you carry one special, not a bag of them. Note it
    // does NOT clear the cooldown: swapping changes which special you have, not when you can next use one.
    public static void Grant(Player player, SpecialKind kind, IResourceManager resources)
    {
        _held[player.Guid] = kind;

        Combat.JobWeaponAbilities.SendToolbar(player, resources);
    }

    // The slot for the arena bar, or null when the player is holding nothing.
    public static AbilityPacketSetDefinition.Slot? MakeToolbarSlot(Player player)
    {
        if (!_held.TryGetValue(player.Guid, out var kind))
            return null;

        return new AbilityPacketSetDefinition.Slot
        {
            Type = 3,
            ManaCost = 0,
            IconId = IconIdFor(kind),
            NameId = NameIdFor(kind),
            AbilityDefinitionId = 0,
        };
    }

    // Throw the held special. The slot STAYS - it just goes on cooldown.
    public static bool TryThrow(Player player, IResourceManager resources, ulong selectedGuid)
    {
        if (!_held.TryGetValue(player.Guid, out var kind))
            return false;

        if (IsOnCooldown(player))
            return false;

        // The throw itself is the ordinary snowball path - same projectile, animation and cone - with the
        // special's harder effect applied on impact. Reusing it keeps aiming identical whichever slot is
        // pressed, which matters when the "1" and "3" keys are both snowballs.
        //
        // ★ The cooldown is only started once the throw actually goes out: a refused throw (no target
        // resolution problem, just a failed cast) must not eat the whole 25 seconds.
        if (!SnowballTool.TryThrow(player, resources, selectedGuid, kind))
            return false;

        _cooldowns[player.Guid] = DateTime.UtcNow.AddMilliseconds(CooldownMs);

        return true;
    }

    // Leaving the arena drops whatever was held - these only exist inside a match - and clears the
    // cooldown with it, so a new match doesn't start with a dead slot.
    public static void Clear(Player player)
    {
        _held.TryRemove(player.Guid, out _);
        _cooldowns.TryRemove(player.Guid, out _);
    }

    // What a special looks like where it lands. Power bursts once; Freezing encases the victim for the
    // whole duration and cracks open again as it wears off.
    public static void PlayImpact(Player victim, SpecialKind kind, int durationMs)
    {
        if (kind == SpecialKind.Power)
        {
            victim.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = 0, // world-positioned - a one-shot burst, nothing to leave attached
                CompositeEffectId = PowerBlastFxId,
                Position = victim.Position,
            }, sendToSelf: true);

            return;
        }

        // Frozen solid: a LOOP, attached by tag so it rides them while they're stuck and can be pulled
        // off precisely when the freeze ends rather than guessing a clip length.
        victim.SendTunneledToVisible(new PlayerUpdatePacketAddEffectTagCompositeEffect
        {
            Guid = victim.Guid,
            TagId = FreezeTagId,
            CompositeEffectId = FreezeLoopFxId,
            SourceGuid = victim.Guid,
        }, sendToSelf: true);

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(durationMs);

                victim.SendTunneledToVisible(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                {
                    Guid = victim.Guid,
                    TagId = FreezeTagId,
                }, sendToSelf: true);

                // The thaw - the ice breaking apart as they come free.
                victim.SendTunneledToVisible(new PlayerUpdatePacketPlayCompositeEffect
                {
                    Guid = 0,
                    CompositeEffectId = ThawFxId,
                    Position = victim.Position,
                }, sendToSelf: true);
            }
            catch { }
        });
    }
}
