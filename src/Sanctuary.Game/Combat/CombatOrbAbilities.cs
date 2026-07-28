using System.Collections.Generic;

namespace Sanctuary.Game.Combat;

// Battle-item orbs/spheres/grenades/balls - the wiki's "Orbs, Spheres and Potions" combat mechanic
// (freerealms.fandom.com/wiki/Combat): "spheres and orbs normally have effects such as confuse, stun,
// sleep, root, knockback, or simply inflict damage upon an enemy," used from a 4-slot "potion belt."
//
// REAL, generic identification: every item in this family shares ClientItemDefinions.json's
// CategoryId 14 (confirmed by dumping every CategoryId-14 item's Comment - it's cleanly just this
// family: "<Effect> Orb 1/Orb/Sphere" tiers + a few "Grenade"/"Ball" variants, nothing else). Dispatch
// matches on the item's internal Comment (server-side only field, see BaseItemDefinition.Comment) rather
// than a hardcoded item-id list, so every tier of every orb works the same way without needing its own
// entry - e.g. "Sleep Orb", "Sleep Orb 1", and "Sleep Sphere" all resolve identically.
//
// Real descriptions (mined from the game's own locale dump, matched to each item's DescriptionId):
//   Sleep     - "A magical orb that puts opponents to sleep."
//   Unmoving  - "A magical orb that roots opponents."
//   Flabbergast - "A magical orb that stuns opponents."
//   Frag      - "A magical orb that damages opponents."
//   Blast     - "A magical orb that knocks opponents away from you."
//   Confusion - "A magical orb that confuses opponents."
// Scare/Frost (seen as item names in the same CategoryId 14 family, "Scare Orb/Sphere" / "Frost
// Grenade") aren't in the 6 Mystery-Pack spheres this was built for and have no description text found
// yet to confirm Fear/Freeze specifically - left unmapped rather than guessed.
public enum OrbEffect
{
    Damage,
    Sleep,
    Root,
    Stun,
    Confuse,
    Knockback,
}

// DurationMs/Damage are NOT sourced anywhere (no ability-definitions numeric table exists in our
// resources, and the item's own flavor text has no numbers) - reasonable placeholders sized to these
// being a Cost-50, PowerRating-3 (low/mid tier) consumable, not wiki-verified. Flagged the same as every
// other unsourced number in this codebase, not silently presented as confirmed.
public sealed record CombatOrbDefinition(OrbEffect Effect, int DurationMs, int Damage);

public static class CombatOrbAbilities
{
    private const int DefaultCcDurationMs = 5000;
    private const int DefaultFragDamage = 1500;

    // Real, wiki-sourced (freerealms.fandom.com/wiki/Sleep_Orb): "puts your target to sleep for 10
    // seconds, and hitting your target will wake them." The wake-on-hit half lives in
    // Npc.ApplyDamage (StatusEffects.Clear on any damage), independent of duration - this just fixes the
    // duration from the shared 5s estimate to the real 10s. Root/Stun/Confuse have no equivalent source
    // found yet, so they stay on the flagged-estimate default.
    private const int SleepDurationMs = 10000;

    private static readonly (string Keyword, CombatOrbDefinition Orb)[] _byKeyword =
    [
        ("sleep",       new CombatOrbDefinition(OrbEffect.Sleep, SleepDurationMs, 0)),
        ("unmoving",    new CombatOrbDefinition(OrbEffect.Root, DefaultCcDurationMs, 0)),
        ("flabbergast", new CombatOrbDefinition(OrbEffect.Stun, DefaultCcDurationMs, 0)),
        ("confusion",   new CombatOrbDefinition(OrbEffect.Confuse, DefaultCcDurationMs, 0)),
        ("blast",       new CombatOrbDefinition(OrbEffect.Knockback, 0, 0)),
        ("frag",        new CombatOrbDefinition(OrbEffect.Damage, 0, DefaultFragDamage)),
    ];

    // FX ids: the real "PFX_orb-explosion_<color>_cog_<name>" family (ActorCompositeEffectDefinitions.xml,
    // ids 16572-16576) - a dedicated 5-effect set that maps 1:1 onto 5 of these 6 orb effects. Frag has no
    // matching entry in that family (it's a plain damage orb, not a status effect) - falls back to a
    // generic explosion, not a name-matched real id like the other 5.
    private static readonly Dictionary<OrbEffect, int> _impactFx = new()
    {
        [OrbEffect.Sleep] = 16572,     // PFX_orb-explosion_blue_cog_sleeping-gas
        [OrbEffect.Root] = 16573,      // PFX_orb-explosion_green_cog_ooze
        [OrbEffect.Stun] = 16574,      // PFX_orb-explosion_white_cog_stars-yellow
        [OrbEffect.Knockback] = 16575, // PFX_orb-explosion_orange_cog_shockwave-yellow
        [OrbEffect.Confuse] = 16576,   // PFX_orb-explosion_purple_cog_question-marks
        [OrbEffect.Damage] = 5361,     // PFX_fire-smoke_explosion-big (generic - no dedicated Frag cog effect found)
    };

    public static bool TryResolve(string comment, out CombatOrbDefinition orb)
    {
        var lower = comment.ToLowerInvariant();
        foreach (var (keyword, def) in _byKeyword)
        {
            if (lower.Contains(keyword))
            {
                orb = def;
                return true;
            }
        }

        orb = null!;
        return false;
    }

    public static int ImpactFxFor(OrbEffect effect) => _impactFx.GetValueOrDefault(effect, 5361);

    public static StatusEffectKind? ToStatusEffectKind(OrbEffect effect) => effect switch
    {
        OrbEffect.Sleep => StatusEffectKind.Sleep,
        OrbEffect.Root => StatusEffectKind.Root,
        OrbEffect.Stun => StatusEffectKind.Stun,
        OrbEffect.Confuse => StatusEffectKind.Confuse,
        OrbEffect.Knockback => StatusEffectKind.Knockback,
        _ => null,
    };
}
