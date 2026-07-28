namespace Sanctuary.Game.Combat;

// The "potion belt" consumables (user-supplied wiki reference, freerealms.fandom.com/wiki/Category:SC_Supplies,
// 2026-07-27): Large/Shared Health, Energy, and Replenishment (both) potions - real 5-packs, SC-purchased,
// "Any Job: Level 1". Matches the same potion-belt mechanic the earlier "Orbs, Spheres and Potions" Combat
// page quote described ("energy (mana), health, replenishment... vials and potions... spheres, orbs, and
// food"). Distinct from CombatOrbAbilities (enemy-targeted CC/damage): potions are SELF or GROUP restore -
// no target needed, no cooldown gate on the item itself beyond the normal per-item cooldown.
//
// Real, confirmed via ClientItemDefinitions.json (server-side Comment field) + the locale dump: Large Health
// Potion (2591), Large Energy Potion (2592), Large Replenishment Potion (2593), Shared Health Potion (49997),
// Shared Energy Potion (49998), Shared Replenishment Potion (49999) - matching the screenshot's exact 6.
// The SAME json also has many more real tiers under the identical naming convention (Medium/Jumbo/Gigantic/
// Ginormous X Potion, "Health Potion 1/II/IV", "Mana Potion", etc.) - resolved generically by a name-suffix
// match (see TryResolve) so every tier works the same way without a per-item entry, same pattern as
// CombatOrbAbilities. Heal/energy AMOUNTS are NOT sourced anywhere (no numeric ability-data table exists) -
// flagged estimates, not wiki-verified.
public enum PotionEffect
{
    Health,
    Energy,
    Replenishment, // both
}

public sealed record PotionDefinition(PotionEffect Effect, bool Shared);

public static class PotionAbilities
{
    // PERCENT-based (2026-07-27 fix, live feedback: "make sure health potions ... scale for all players,
    // some players have more health than others") - was a flat +400, trivial for a high-level job's much
    // bigger HP pool and huge for a level 1's. 16% keeps the same "bigger dose than the power-up's little
    // heart" ratio the old flat numbers had (400 vs the heart's 125, ~3.2x -> power-up's 5% * 3.2 = 16%).
    public const float HealFraction = 0.16f;
    public const int EnergyAmount = 100; // full refill - potions are a purchased item, generous by design

    // Real, name-matched FX (see the class header comment for XML sourcing): the proven Health heal-shower
    // for a heal, and a real "short head-positioned mana heal" effect for energy.
    public const int HealFxId = 15921;   // PFX_magic-heal_red_head_shower_lg_loop_raised
    // CORRECTED 2026-07-28 (live feedback: "health effects seems to be stuck in the world during dungeon
    // playthrough") - HealFxId's own name says "_loop_": it must be tag-attached and explicitly removed
    // (same mechanism PowerupSystem.HealShowerMs already uses for the identical asset), not fired as a
    // one-shot world-positioned trigger that then loops forever wherever the player happened to be standing.
    public const int HealShowerMs = 15000;
    public const int EnergyFxId = 16325; // PFX_heal_mana_blue_sm_short_head
    public const int DrinkAnimId = 3371; // emo_drink - real client animation, no dedicated "combat drink" found

    public static bool TryResolve(string comment, out PotionDefinition potion)
    {
        var shared = comment.Contains("Shared", System.StringComparison.OrdinalIgnoreCase);

        if (EndsWithAny(comment, "Health Potion"))
        {
            potion = new PotionDefinition(PotionEffect.Health, shared);
            return true;
        }
        if (EndsWithAny(comment, "Energy Potion", "Mana Potion"))
        {
            potion = new PotionDefinition(PotionEffect.Energy, shared);
            return true;
        }
        if (EndsWithAny(comment, "Replenishment Potion", "Replenishing Potion"))
        {
            potion = new PotionDefinition(PotionEffect.Replenishment, shared);
            return true;
        }

        potion = null!;
        return false;
    }

    private static bool EndsWithAny(string comment, params string[] suffixes)
    {
        foreach (var suffix in suffixes)
            if (comment.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
