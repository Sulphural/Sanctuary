using System;
using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// WARRIOR (profile 32) — clubs, axes and hammers, heavy melee. Weapon-driven like the medic/ninja/brawler kits:
// the equipped "Warrior's <Weapon> of <Special>" (or a novelty/coin-shop weapon) grants a MELEE (slot 0) + a
// named SPECIAL (slot 1).
//
// Real per-item data mined 2026-07-29 from the OSFR community combat spreadsheet (2 tabs: an "icons/anim" tab,
// one row per named ability incl. numbered per-weapon-instance variants like "Sweeping Slash 5" vs bare
// "Sweeping Slash"; and a "weapon-summary" tab, one row per real weapon ITEM with its own real tooltip
// Basic/Super damage numbers) cross-referenced against src/Resources/ClientItemDefinitions.json (Comment field
// exact match -> real item id + NameId). This REPLACES the previous pass's bug: 10 shared "Kit" objects
// (SpinningKit/CleaveKit/etc.) were each built ONCE then reused across every weapon tier that carries that
// special, so e.g. Cudgel L1, Axe L5, Battle Hammer L8, Double Axe L12 and Warlord Axe L16 all showed the exact
// same "Spinning Attack" damage despite being 5 different real items with 5 different real tooltip numbers. Kits
// are now per-special-TYPE factory functions (Spinning/Cleave/Quake/Warcry/Whirlwind/Hurling/Berserk/Frenzy),
// called ONCE PER REAL ITEM ID with that item's own real numbers — same pattern as MedicWeaponAbilities.cs.
// Commanding Shout and Thunderclap only exist on one real item each (Warlord Axe L16), so they stay static
// single instances, also matching Medic's convention for top-tier-only specials (Antibodies/Laser Surgery).
//
// DAMAGE: the weapon-summary tab's "Data Status" column is CONFIRMED for most of the 5 real tiers (32 items) —
// used verbatim. A few rows are PENDING (basic-attack variant name uncertain, marked "(?)" in the sheet) but
// still carry a real tooltip number — used anyway per the sheet's own guidance, flagged per row below. Where a
// column shows "N (x2)"/"N (x5)" (the ability hits multiple times), the WeaponAbility record has no multi-hit
// field (same documented gap as Medic's Cauterize), so the SUM of all hits is used as the single Damage value —
// a simplification, not an invented number; the real per-hit value is cited in the per-line comment.
// ICONS: the icons/anim tab's own per-weapon-instance Icon IMAGE_ID is used for the melee slot (varies by exact
// basic-attack name variant, e.g. "Sweeping Slash 13" != bare "Sweeping Slash" != "Sweeping Slash 5" - matches
// Medic's Icon4254/Icon3961/etc. pattern) and the SUPER ATTACKS section's IMAGE_ID for the special slot (fixed
// per special-TYPE, doesn't vary by tier). Where a variant's IMAGE_ID column is blank, the central MeleeIcon
// constant (abil_warrior_crushing_blows) is reused as a stand-in rather than shipping 0/blank, same convention
// Medic used for its own icon gaps. This pass also caught 2 outright WRONG icon ids from the previous pass:
// "Spinning Attack"'s real icon is 23006, not 23007 (23007 belongs to a DIFFERENT ability, "Spinning Blade");
// "Warcry"'s real icon is 23014, not 23013.
// FX: EffectId/CastEffectId are sourced from the icons/anim tab's own "FX EffectId"/"FX EffectDef Name" columns
// where present, using the pattern observed across every row that has both a primary id and a parenthetical
// second id: the PRIMARY column value is the caster-side CastEffectId (e.g. Quake's own "warrior-quake-level-5"
// loop), the PARENTHETICAL id is the target-facing EffectId (e.g. Quake's "(column 16072)" ground impact) — this
// matches, and confirms, Cleave's and Thunderclap's FX exactly as the previous pass already had them (both
// already real, ActorCompositeEffectDefinitions.xml exact-name-match citations, independently verified again
// here). Where the sheet lists no FX at all for an ability (Axe Throw, Berserk's impact half, Frenzy's impact
// half, Warcry's impact half), the previous pass's own ActorCompositeEffectDefinitions.xml citations are kept
// unchanged (real, just from a different source than this spreadsheet). Whirlwind is the one exception: the
// previous pass's FX (16107/16105, "warrior-air-attack") was already flagged as a same-family-not-exact
// mismatch; the sheet gives a DIFFERENT real id (5378, "PFX_squares_red_arm-r_warrior-spin-attack-trail") under
// the literal "Whirlwind" row, so that supersedes it here — though the sheet's own Notes column hedges this one
// too ("(allegedly) shares spin trail" — the same FX id is also claimed for the unrelated "Spinning Blade"
// ability), so still not a fully-confirmed pairing, just a closer name-match than before.
// ANIM: where the icons/anim tab lists a real Animation ID for a special (Quake=1001022, Commanding
// Shout=1061141, Warcry=1061143, Berserk=1038), that supersedes the previous pass's arbitrary
// com_2hp_special_01..08 (1091-1098) / com_2hs_special (1051-1052) pool assignment — same "sheet data beats an
// arbitrary pool pick" precedent Medic's file already established. The sheet's "Anim Status" column marks these
// UNKNOWN or PENDING (community-sourced, not dev-confirmed) rather than CONFIRMED, so still not 100% certain.
// Specials the sheet gives no anim for (Spinning Attack, Cleave, Whirlwind, Axe Throw, Frenzy) keep the old pool
// assignment, now clearly flagged as the weaker fallback. Melee-slot Animation is picked per equipped weapon
// MODEL (1h/2h/fist) at resolve time via MeleeAnimFor, same as before — the literal Animation passed into each
// factory function below is a placeholder immediately overwritten by ResolveAbility's `with { Animation = ... }`.
// NOVELTY/COIN-SHOP WEAPONS: several items already in this file (Nature Claw, Ice Axe, Lightning Blade, The
// Kingmaker, Twin Crescent Axe, Exploding Axe, Angro's Vanquisher) previously carried a placeholder guess
// ("closest kit we have") because no matching wiki page had been found. The spreadsheet's weapon-summary tab
// has real "Variable" (scales with player level) rows for all of these under slightly different names ("New
// School Twin Crescent Axe" / "New School Exploding Axe" / "New School Angro's Vanquisher" — the plain items in
// ClientItemDefinitions.json have no "New School"/"Old School" qualifier in their Comment field, so this is a
// name cross-reference, not an exact Comment match, same caveat as before but now backed by real per-level
// numbers instead of an unrelated kit reuse) plus 4 with an EXACT Comment match already (Nature Claw, Ice Axe,
// Lightning Blade, The Kingmaker). All 7 are wired to their own real ability names + top-tier (level 16) numbers
// below, replacing the old kit-reuse guesses. Several new CONFIRMED single/few-id novelty weapons (Butterfly
// Club, Daring Champion Cudgel, Soapy Battle Axe, Illuminating Hammer, Balloon Axe, Fastvi's Frozen Fire,
// Heartthrob Hammer, Smokey Axe, Magical Essence Warlords Axe) and 2 large decorative dye-variant ranges
// (Smasher, Warlord Axe) were added from the same spreadsheet. Many more PENDING/ambiguous named rows (Cudgel,
// Axe, Jeweled Axe, Dual Smasher, the "Guardian"/"Defender"/"Protector"/"Champion" hammer&axe lines, etc.) were
// surveyed but left UNMAPPED: their sheet rows are PENDING with an uncertain "(?)" ability variant AND their
// Comment field matches a large ambiguous block of a dozen+ ids with no "of <Special>" naming to disambiguate
// which real item the row describes — no id was guessed for these, per the "don't invent an id" rule.
public sealed record WarriorWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class WarriorWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int WarriorProfileId = 32;

    // Melee swing anims (AnimationGroups.xml). Picked per equipped weapon model at resolve time (MeleeAnimFor) —
    // the Animation value passed into each factory function below is always overwritten by ResolveAbility.
    private const int FistMeleeAnim = 1000;    // com_h2h_attack (Nature Claw, Lightning Blade)
    private const int OneHandMeleeAnim = 1020; // com_1hs_attack (Cudgel, Axe, Ice Axe, The Kingmaker)
    private const int TwoHandMeleeAnim = 1080; // com_2hp_attack (Battle Hammer, Double Axe, Warlord Axe, most novelties)
    private const int MeleeHitFx = 7;          // PFX_Hit_Flash — generic impact flash, used wherever no dedicated FX exists.

    // REAL ability icons — abil_warrior_* Small (type-5) IMAGE_IDs, reversed from the client.
    // Basic-attack fallback (used wherever a per-weapon-instance icon column is blank in the sheet) - a heavy
    // strike, since there's no dedicated generic "attack" icon.
    private const int MeleeIcon = 11657;       // abil_warrior_crushing_blows

    // Special-ability icons (fixed per special-TYPE, from the icons/anim tab's SUPER ATTACKS section).
    // Spinning/Warcry corrected 2026-07-29 (previous pass had 23007/23013 - those are OTHER abilities' icons).
    private const int IcoSpinning = 23006, IcoCleave = 23001, IcoQuake = 11663, IcoWarcry = 23014,
        IcoWhirlwind = 23016, IcoAxeThrow = 22995, IcoBerserk = 22998, IcoFrenzy = 23004,
        IcoCommand = 11654, IcoThunderclap = 23010;

    // Per-weapon-tier melee icons (icons/anim tab, matched to the exact basic-attack name variant each real
    // weapon's row lists - e.g. Cudgel tier's "Fierce Edge 3"/"Sweeping Slash 13" both happen to share icon
    // 4179; Axe tier's "Fierce Edge"/"Power Slash"/"Sweeping Slash 5"/"Dual Strike" all share icon 3968).
    private const int IconCudgel = 4179, IconAxe = 3968, IconBattleHammer = 4230, IconDoubleAxe = 4218,
        IconWarlordAxe = 4125;
    // Individual per-name icons used by novelty weapons below (each cited inline at its use site).
    private const int Icon14103 = 14103, Icon14121 = 14121, Icon14229 = 14229, Icon14271 = 14271,
        Icon14752 = 14752, Icon14758 = 14758, Icon30981 = 30981, Icon30206 = 30206, Icon30190 = 30190,
        Icon45896 = 45896, Icon27727 = 27727, Icon39205 = 39205, Icon39237 = 39237, Icon498 = 498;

    // Placeholder special anim for novelty items the sheet gives no Animation ID for at all (same pool as the
    // core specials' own fallback, see file header).
    private const int NoveltySpecialAnim = 1091;

    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    // 1-hand models swing 1hs; the fist claw swings h2h; everything else (2h hammers/axes) swings 2hp.
    // Includes the novelty weapons keyed by their client model (club/sword/single-axe = 1h; fist = h2h).
    private static readonly HashSet<int> OneHandWeaponDefIds = new()
    {
        75120, 75121, 75122, 75123, 75124, 75125, // Cudgel + Axe (base)
        7012, 29963,   // Student Warrior Cudgel (club)
        78711,         // Ice Axe (single axe)
        79021,         // The Kingmaker (sword)
    };
    private static readonly HashSet<int> FistWeaponDefIds = new()
    {
        78200,         // Warrior's Nature Claw (fist)
        78714,         // Lightning Blade (fist model)
    };
    private static int MeleeAnimFor(int weaponDefId) =>
        FistWeaponDefIds.Contains(weaponDefId) ? FistMeleeAnim
        : OneHandWeaponDefIds.Contains(weaponDefId) ? OneHandMeleeAnim
        : TwoHandMeleeAnim;

    public static readonly WeaponAbility BareMelee = new("Sweeping Slash", MeleeIcon, 200, TwoHandMeleeAnim, MeleeHitFx);

    // ── TRAITS ── (effects from the ZAM job page; magnitudes ours):
    //   L5 Instigation · L10 Piercing Strikes · L15 High Morale · L20 Counterattack.
    public const int InstigationLevel = 5;
    public const int PiercingStrikesLevel = 10;
    public const int HighMoraleLevel = 15;
    public const int CounterattackLevel = 20;

    // Gameplay magnitudes (ours to tune):
    // Piercing Strikes: an unlocked Warrior rolls crits on its hits at base + this bonus chance.
    public const int BaseCritChancePercent = 5;
    public const int PiercingStrikesCritChanceBonus = 15;
    public const float BaseCritMultiplier = 2.0f;
    // High Morale: energy regenerates faster (added to the +4/s base -> +8/s = ~12.5s refill).
    public const int HighMoraleEnergyRegenBonus = 4;
    // Counterattack: reflect this share of an incoming hit back at the attacker.
    public const float CounterattackReflectPercent = 0.30f;
    // Instigation (taunt) has no server-side aggro model yet -> passive/display only.

    // REAL name/desc Global.Text ids + real trait icons (abil_warrior_* Small IMAGE_IDs). NameId/DescId/IconId/Level.
    private static readonly JobTraits.Trait[] TraitData =
    [
        new(420950, 420974, 11666, InstigationLevel),      // Instigation (warrior_spirit)
        new(24398,  420975, 22605, PiercingStrikesLevel),  // Piercing Strikes (rage)
        new(420952, 420976, 39872, HighMoraleLevel),       // High Morale (boon)
        new(420953, 420977, 22602, CounterattackLevel),    // Counterattack (armor_spikes)
    ];

    public static List<AbilityExperience> BuildTraitEntries(int rank) => JobTraits.Build(TraitData, rank, WarriorProfileId);

    public static bool HasTrait(Player player, int traitLevel) =>
        player.ActiveProfileId == WarriorProfileId && player.ActiveProfile.Rank >= traitLevel;

    // Piercing Strikes (L10) adds crit CHANCE. A no-op for non-Warriors / below L10.
    public static int ApplyTraitDamage(Player player, int baseDamage)
    {
        if (!HasTrait(player, PiercingStrikesLevel))
            return baseDamage;

        var critChance = BaseCritChancePercent + PiercingStrikesCritChanceBonus;
        if (Random.Shared.Next(100) >= critChance)
            return baseDamage;

        return Math.Max(1, (int)(baseDamage * BaseCritMultiplier));
    }

    // ── SPECIALS (10 types) ── melee (slot 0) + the named special (slot 1), one factory function PER SPECIAL
    // TYPE, called ONCE PER REAL WEAPON ITEM with that item's own real numbers (see file header - this is the
    // fix for the shared-kit-object bug). AoeRadius > 0 => hits every hostile in range of the caster. Looping
    // cast FX (Warcry/Berserk/Frenzy/Command) use CastEffectStopMs.
    //
    // Melee name families match 1:1 across every real tier ("Sweeping Slash"->Spinning Attack,
    // "Fierce Edge"->Cleave, "Power Slash"->Quake, "Dual Strike"->Warcry, "Gale Axe"->Whirlwind,
    // "Reckless Strike"->Axe Throw, "Hack 'n' Slash"->Berserk, "Crushing Blow"->Frenzy), confirmed directly
    // against every "Warrior's <Weapon> of <Special>" row in the weapon-summary tab.

    private static WarriorWeapon Spinning(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Sweeping Slash", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // impact=4009 "PFX_Spinning_Blades_Land", cast=4001 "PFX_Spinning_Blades" (ActorCompositeEffectDefinitions.xml,
        // exact name match, unchanged from the previous pass - the sheet lists no FX for this ability).
        new("Spinning Attack", IcoSpinning, specialDmg, 1091, 4009, CastEffectId: 4001, AoeRadius: 8f, CastEffectStopMs: 1200));

    private static WarriorWeapon Cleave(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Fierce Edge", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // impact=16226 (paren id), cast=16202 "WFX_beam-trail_blue_warrior-cleave" (primary id) - matches the
        // sheet's own Cleave row exactly (FX EffectId=16202, EffectDefName "...cleave (sparkles 16226)").
        new("Cleave", IcoCleave, specialDmg, 1092, 16226, CastEffectId: 16202, AoeRadius: 10f, CastEffectStopMs: 1000));

    private static WarriorWeapon Quake(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Power Slash", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // impact=16072 (paren "column" id, unchanged), cast=16009 "PFX_rocks_brown_root_warrior-quake-level-5"
        // (primary id, NEW - the sheet's own Quake row). Anim 1001022 also from the sheet (Anim Status: UNKNOWN).
        new("Quake", IcoQuake, specialDmg, 1001022, 16072, CastEffectId: 16009, AoeRadius: 10f));

    private static WarriorWeapon Warcry(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Dual Strike", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // cast=16199 "PFX_sound_blue_head_warrior-warcry_loop" (ActorCompositeEffectDefinitions.xml, exact name
        // match, unchanged - the sheet lists no FX for this ability). EffectId=0 (single-target/self shout, not
        // an AOE impact - see previous pass's reasoning). Anim 1061143 is NEW, from the sheet (Anim Status: PENDING).
        new("Warcry", IcoWarcry, specialDmg, 1061143, 0, CastEffectId: 16199, CastEffectStopMs: 2000));

    private static WarriorWeapon Whirlwind(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Gale Axe", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // FX changed 2026-07-29: sheet's own "Whirlwind" row gives 5378 "PFX_squares_red_arm-r_warrior-spin-attack-trail"
        // (Notes: "(allegedly) shares spin trail" - the same id is also claimed for the unrelated "Spinning
        // Blade" ability, so still not fully confirmed) - supersedes the previous pass's 16107/16105
        // ("warrior-air-attack"), which was itself already flagged as a same-family-not-exact mismatch. No anim
        // id given (Anim Status: UNKNOWN) - keeps the old pool value.
        new("Whirlwind", IcoWhirlwind, specialDmg, 1095, 5378, AoeRadius: 10f));

    private static WarriorWeapon Hurling(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Reckless Strike", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // No FX in the sheet (Notes: "projectile", Anim Status: UNKNOWN) - keeps the previous pass's real
        // PRJ_battleaxe_* composites (15490 impact / 16177 cast trail, ActorCompositeEffectDefinitions.xml).
        // Real client ability name is "Axe Throw" (matches the sheet's own SUPER ATTACKS row and the toolbar
        // AbilityNameIds below); the wiki calls the underlying weapon-item suffix "Hurling" instead - not
        // renamed here, same as before.
        new("Axe Throw", IcoAxeThrow, specialDmg, 1096, 15490, CastEffectId: 16177));

    private static WarriorWeapon Berserk(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Hack 'n' Slash", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // cast=16232 "PFX_warrior_berserk_red_blades" (exact name match, unchanged). EffectId=0 (single-target,
        // same reasoning as Warcry). Anim 1038 is NEW, from the sheet (Anim Status: UNKNOWN).
        new("Berserk", IcoBerserk, specialDmg, 1038, 0, CastEffectId: 16232, CastEffectStopMs: 2500));

    private static WarriorWeapon Frenzy(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Crushing Blow", meleeIcon, meleeDmg, TwoHandMeleeAnim, MeleeHitFx),
        // cast=16245 "PFX_sparkles_added_warrior-frenzy" (exact name match, unchanged). EffectId=0 (same
        // reasoning as Warcry/Berserk). No anim in the sheet - keeps the old pool value.
        new("Frenzy", IcoFrenzy, specialDmg, 1098, 0, CastEffectId: 16245, CastEffectStopMs: 2000));

    // Commanding Shout only exists on ONE real item (Warrior's Warlord Axe of Command, L16) - static instance,
    // not a factory, matching Medic's convention for top-tier-only specials (Antibodies/Laser Surgery).
    private static readonly WarriorWeapon CommandKit = new(
        new("Dizzying Blow", IconWarlordAxe, 2372, TwoHandMeleeAnim, MeleeHitFx),
        // cast=15987 "PFX_sound_white_mouth_warrior-commanding-shout-level-5" (sheet's own Commanding Shout row,
        // primary id) - supersedes the previous pass's 15233 ("...-level-5_loop", a same-family sibling
        // composite). EffectId=4004 "PFX_waves_red_head_shout" kept from the previous pass's own reasoning (a
        // one-shot shout-wave composite for the AOE taunt impact; the sheet gives no impact id). Anim 1061141 is
        // NEW, from the sheet (Anim Status: PENDING) - supersedes the previous pass's 1051 (which actually
        // belongs to a different ability, Spinning Blade/Thunderclap's pool slot). Damage: CONFIRMED row exists
        // but its Super Attack value is "(?)" (unknown) - Commanding Shout's real client description is a
        // taunt + self-buff (attack power, invincibility) with no listed damage number at all; kept as the
        // previous pass's placeholder since we have no aggro/taunt system to redirect enemies onto the caster.
        new("Commanding Shout", IcoCommand, 4000, 1061141, 4004, CastEffectId: 15987, AoeRadius: 12f, CastEffectStopMs: 2500));

    // Thunderclap only exists on ONE real item (Warrior's Warlord Axe of Thunder, L16) - static instance.
    private static readonly WarriorWeapon ThunderKit = new(
        // Rampage hits twice per the sheet (1186 x2) - summed to 2372 (see file header's multi-hit note).
        new("Rampage", IconWarlordAxe, 2372, TwoHandMeleeAnim, MeleeHitFx),
        // impact=16122 (paren "p2p" id), cast=16280 "PFX_lightning_blue_root_warrior_thunderclap" (primary id) -
        // matches the sheet's own Thunderclap row exactly, unchanged from the previous pass.
        new("Thunderclap", IcoThunderclap, 5977, 1052, 16122, CastEffectId: 16280, AoeRadius: 10f));

    // ── NOVELTY / COIN-SHOP WEAPONS with their OWN unique ability pair (not one of the 10 families above) ──
    // real names/numbers from the weapon-summary tab's "Variable" (top/L16 tier used, matching Medic's PowerFist
    // convention of picking the max-rank bracket) or fixed-level rows. None of these have FX/anim data in the
    // sheet (blank columns), so they use the generic melee-hit fallback + a placeholder pool anim, same honesty
    // convention as Medic's HeartKit/BalloonKit/PowerFistKit.
    private static readonly WarriorWeapon BalloonAxeKit = new(
        // Balloon Axe (CONFIRMED, L16). Notes: "Three variants; both combat versions seem to share ability data."
        new("Cake Cutter", Icon30981, 2372, TwoHandMeleeAnim, MeleeHitFx),
        new("Party Crasher", 291, 8453, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    private static readonly WarriorWeapon HeartthrobKit = new(
        // Heartthrob Hammer (CONFIRMED, L16).
        new("Love Struck", Icon30206, 2372, TwoHandMeleeAnim, MeleeHitFx),
        new("Heart Breaker", Icon30190, 6575, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    private static readonly WarriorWeapon AngroVanquisherKit = new(
        // "New School Angro's Vanquisher" (CONFIRMED, Variable/top-tier L16 used) - the real item's Comment is
        // plain "Angro's Vanquisher" (no "New School" qualifier), see file header note.
        new("Angro Chop", IconWarlordAxe, 2372, TwoHandMeleeAnim, MeleeHitFx),
        new("Vanquish", Icon498, 8302, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    private static readonly WarriorWeapon NatureClawKit = new(
        // Warrior's Nature Claw (CONFIRMED, Variable/top-tier L16 used, exact Comment match).
        new("Feral Swipe", Icon39205, 2372, FistMeleeAnim, MeleeHitFx),
        new("Feral Spirit", Icon39237, 9132, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 8f));

    private static readonly WarriorWeapon IceAxeKit = new(
        // Ice Axe (PENDING, Variable/top-tier L16 used, exact Comment match). Super hits 3x per the sheet
        // ("3x 4732 (Lvl 16)") - summed to 14196 (see file header's multi-hit note). No icon listed for either
        // ability - both fall back to the central MeleeIcon.
        new("Freezing Strike", MeleeIcon, 2372, OneHandMeleeAnim, MeleeHitFx),
        new("Snow Storm", MeleeIcon, 14196, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    private static readonly WarriorWeapon LightningBladeKit = new(
        // Lightning Blade (PENDING, Variable/top-tier L16 used, exact Comment match). No icon listed either.
        new("Electric Punch", MeleeIcon, 2372, FistMeleeAnim, MeleeHitFx),
        new("Lightning Blast", MeleeIcon, 9132, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 8f));

    private static readonly WarriorWeapon KingmakerKit = new(
        // The Kingmaker (PENDING, Variable/top-tier L16 used, exact Comment match).
        new("Regal Strike", Icon45896, 2372, OneHandMeleeAnim, MeleeHitFx),
        new("Royal Power", MeleeIcon, 8302, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    private static readonly WarriorWeapon TwinCrescentKit = new(
        // "New School Twin Crescent Axe" (CONFIRMED, Variable/top-tier L16 used) - the real item's Comment is
        // plain "Twin Crescent Axe" (no "New School" qualifier), see file header note. Super hits 3x per the
        // sheet ("3x 4732 (Lvl 16)") - summed to 14196. Corrects the previous pass's guess (WhirlwindKit reuse,
        // already flagged there as an unconfirmed pairing) with this weapon's own REAL ability names.
        new("Ember Strike", Icon14752, 2372, TwoHandMeleeAnim, MeleeHitFx),
        new("Ignite", 23006, 14196, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    private static readonly WarriorWeapon ExplodingAxeKit = new(
        // "New School Exploding Axe" (CONFIRMED, Variable/top-tier L12 used - no L16 value listed in the sheet
        // for this one) - the real item's Comment is plain "Exploding Axe", see file header note. Corrects the
        // previous pass's guess (QuakeKit reuse). Real ability also has an 11% HP instant-heal lifesteal
        // component on Vampiric Wrath that isn't modeled (no lifesteal mechanic exists in this combat system,
        // same documented gap as Medic's Shock Paddles revive note) - damage-only here.
        new("Twilight Strike", Icon14121, 2372, TwoHandMeleeAnim, MeleeHitFx),
        new("Vampiric Wrath", 23006, 4750, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    private static readonly WarriorWeapon CandyStripedAxeKit = new(
        // Candy Striped Axe (PENDING, Variable/top-tier L16 used).
        new("Deck the Halls", MeleeIcon, 2372, TwoHandMeleeAnim, MeleeHitFx),
        new("Candy Hurricane", Icon27727, 8302, NoveltySpecialAnim, MeleeHitFx, AoeRadius: 10f));

    // weapon def id -> ability pair, one PER ITEM (not shared). Real client Warrior weapons (Cudgel L1, Axe
    // L4/L5, Battle Hammer L8, Double Axe L12, Warlord Axe L16) — the 75120-75149 "of <Special>" item series,
    // ids verified directly against ClientItemDefinitions.json, numbers/names/icons verified against the
    // spreadsheet's exact per-weapon ability-instance suffix (see the IconCudgel/IconAxe/etc. consts' header
    // comment above). Note: ClientItemDefinitions.json's own Cost field shows all 4 "Warrior's Axe of <X>" items
    // sharing the SAME cost (850), so - despite the sheet nominally splitting "Axe" into an L4 and an L5 row -
    // they're treated here as one real tier, matching the item data.
    private static readonly Dictionary<int, WarriorWeapon> _byWeaponDefId = new()
    {
        // Cudgel (L1) — "Sweeping Slash 13"/"Fierce Edge 3", both icon 4179.
        [75120] = Spinning(IconCudgel, 279, 506),   // Warrior's Cudgel of Spinning - CONFIRMED
        [75121] = Cleave(IconCudgel, 279, 1143),    // Warrior's Cudgel of Cleaving - CONFIRMED

        // Axe (L4/L5) — "Fierce Edge"/"Power Slash"/"Sweeping Slash 5"/"Dual Strike", all icon 3968.
        [75122] = Spinning(IconAxe, 488, 885),      // Warrior's Axe of Spinning - CONFIRMED
        [75123] = Cleave(IconAxe, 488, 1998),       // Warrior's Axe of Cleaving - CONFIRMED
        [75124] = Quake(IconAxe, 444, 1554),        // Warrior's Axe of Earthquake - CONFIRMED (super variant "(?)")
        [75125] = Warcry(IconAxe, 444, 1118),        // Warrior's Axe of Warcry - CONFIRMED, Dual Strike 222(x2)=444

        // Battle Hammer (L8) — icon 4230.
        [75126] = Spinning(IconBattleHammer, 853, 1548),   // of Spinning - CONFIRMED
        [75127] = Cleave(IconBattleHammer, 853, 3492),     // of Cleaving - CONFIRMED
        [75128] = Quake(IconBattleHammer, 776, 2716),      // of Earthquake - CONFIRMED (super variant "(?)")
        [75129] = Warcry(IconBattleHammer, 776, 1955),      // of Warcry - CONFIRMED, Dual Strike 2 388(x2)=776
        [75130] = Whirlwind(IconBattleHammer, 776, 2716),  // of Whirlwind - CONFIRMED
        [75131] = Hurling(IconBattleHammer, 776, 2716),    // of Hurling - CONFIRMED

        // Double Axe (L12) — icon 4218.
        [75132] = Spinning(IconDoubleAxe, 1492, 2707),     // of Spinning - CONFIRMED
        [75133] = Cleave(IconDoubleAxe, 1492, 6107),       // of Cleaving - CONFIRMED
        [75134] = Quake(IconDoubleAxe, 1357, 6107),        // of Earthquake - CONFIRMED
        [75135] = Warcry(IconDoubleAxe, 1358, 3420),        // of Warcry - CONFIRMED, Dual Strike 3 679(x2)=1358
        [75136] = Whirlwind(IconDoubleAxe, 1357, 4750),    // of Whirlwind - CONFIRMED
        [75137] = Hurling(IconDoubleAxe, 1357, 4750),      // of Hurling - CONFIRMED
        [75138] = Berserk(IconDoubleAxe, 1358, 6107),       // of Berserking - CONFIRMED, Hack 'n' Slash 2 679(x2)=1358
        [75139] = Frenzy(IconDoubleAxe, 1357, 6715),        // of Frenzy - CONFIRMED, Frenzy 1343(x5)=6715

        // Warlord Axe (L16) — all 10 specials, icon 4125 (except of Spinning: sheet lists no icon for its
        // basic-attack variant, so it falls back to the central MeleeIcon).
        [75140] = Spinning(MeleeIcon, 2609, 4732),          // of Spinning - PENDING (basic variant unknown)
        [75141] = Cleave(IconWarlordAxe, 2609, 10674),      // of Cleaving - CONFIRMED
        [75142] = Quake(IconWarlordAxe, 2372, 8302),        // of Earthquake - CONFIRMED
        [75143] = Warcry(IconWarlordAxe, 2372, 5977),        // of Warcry - CONFIRMED, Dual Strike 4 1186(x2)=2372
        [75144] = Whirlwind(IconWarlordAxe, 2372, 8302),    // of Whirlwind - CONFIRMED
        [75145] = Hurling(IconWarlordAxe, 2372, 8302),      // of Hurling - CONFIRMED
        [75146] = Berserk(IconWarlordAxe, 2372, 10674),      // of Berserking - CONFIRMED, Hack 'n' Slash 1186(x2)=2372
        [75147] = Frenzy(IconWarlordAxe, 2372, 11740),       // of Frenzy - CONFIRMED, Frenzy 2348(x5)=11740
        [75148] = CommandKit,                               // of Command - CONFIRMED (super value unknown)
        [75149] = ThunderKit,                               // of Thunder - CONFIRMED

        // ── Starter / novelty / coin-shop weapons ── real item ids looked up by exact Comment match against
        // ClientItemDefinitions.json (except the "New School <X>" cross-references noted above), real ability
        // data from the same spreadsheet's weapon-summary tab.
        [7012] = Spinning(Icon14229, 279, 506), [29963] = Spinning(Icon14229, 279, 506), // Student Warrior Cudgel - CONFIRMED, "Sweeping Slash 11"
        [48164] = Spinning(MeleeIcon, 2372, 4732),   // Butterfly Club - CONFIRMED, "Sweeping Slash 2" (no icon listed)
        [38458] = Spinning(Icon14103, 853, 1548),    // Soapy Battle Axe - CONFIRMED, "Sweeping Slash 9"
        [76708] = HeartthrobKit,                     // Heartthrob Hammer
        [55818] = Spinning(Icon14758, 2609, 4732),   // Magical Essence Warlords Axe - CONFIRMED, "Sweeping Slash 7"
        [76561] = CandyStripedAxeKit,                // Candy Striped Axe

        [78711] = IceAxeKit,          // Ice Axe (fist->no, single-axe model, 1h)
        [78714] = LightningBladeKit,  // Lightning Blade (fist model)
        [79021] = KingmakerKit,       // The Kingmaker (sword, 1h)
        [78200] = NatureClawKit,      // Warrior's Nature Claw (fist)
        [13671] = ExplodingAxeKit, [55363] = ExplodingAxeKit,                    // Exploding Axe
        [9027] = AngroVanquisherKit, [13670] = AngroVanquisherKit,
        [30564] = AngroVanquisherKit, [38540] = AngroVanquisherKit,              // Angro's Vanquisher
    };

    // Large dye/tint-variant + decorative id ranges - real items, one set of numbers per base weapon name (dye
    // color doesn't change stats). Field initializers run before this ctor body, so AllWeaponDefIds (snapshotted
    // at the end) picks these up too.
    static WarriorWeaponAbilities()
    {
        // Daring Champion Cudgel ("Sweeping Slash"/"Spinning Attack", CONFIRMED, no basic-attack icon listed) -
        // 10 dye variants.
        foreach (var id in new[] { 38393, 38396, 38399, 38402, 38406, 38409, 38413, 38416, 38420, 38424 })
            _byWeaponDefId[id] = Spinning(MeleeIcon, 488, 885);

        // Illuminating Hammer ("Sweeping Slash 6"/"Spinning Attack", CONFIRMED) - 2 ids.
        foreach (var id in new[] { 22210, 48141 })
            _byWeaponDefId[id] = Spinning(Icon14271, 2609, 4732);

        // Balloon Axe (CONFIRMED, "Three variants; both combat versions seem to share ability data") - 6 ids.
        foreach (var id in new[] { 16360, 16361, 16362, 16363, 16364, 77448 })
            _byWeaponDefId[id] = BalloonAxeKit;

        // Fastvi's Frozen Fire ("Sweeping Slash 5"/"Spinning Attack", CONFIRMED) - 3 ids.
        foreach (var id in new[] { 37010, 38562, 45092 })
            _byWeaponDefId[id] = Spinning(IconAxe, 2609, 4732);

        // Smokey Axe ("Sweeping Slash 8"/"Spinning Attack", CONFIRMED) - 2 ids.
        foreach (var id in new[] { 23022, 48145 })
            _byWeaponDefId[id] = Spinning(Icon14758, 2609, 4732);

        // Twin Crescent Axe - real item ids (13672 + 55333 base, 55430-55464 dye range).
        _byWeaponDefId[13672] = TwinCrescentKit;
        _byWeaponDefId[55333] = TwinCrescentKit;
        for (var id = 55430; id <= 55464; id++)
            _byWeaponDefId[id] = TwinCrescentKit;

        // Smasher (decorative, non-job weapon; "Sweeping Slash 6"/"Spinning Attack", CONFIRMED L12 numbers) -
        // the largest dye/tint range (~80 ids).
        foreach (var id in new[] {
            7070,7071,7072,7073,7077,7078,7079,7080,7081,7082,7083,7084,7085,7086,7087,7088,7089,7090,7091,7092,
            7093,7094,7095,7096,7097,7098,7099,7100,7101,7102,7103,7104,30140,30141,30142,30143,30144,30145,
            30146,30147,30148,30149,38501,38503,38504,38505,38507,38508,38510,38511,38513,38514,38516,38517,
            38519,38520,38522,38523,38524,38526,38528,38530,38531,38532,38534,38535,38536,38538 })
            _byWeaponDefId[id] = Spinning(Icon14271, 1492, 2707);

        // Warlord Axe (decorative, non-job weapon; "Sweeping Slash 7"/"Spinning Attack", CONFIRMED L16 numbers)
        // - another large dye/tint range (~80 ids).
        foreach (var id in new[] {
            4213,4214,4215,4217,4218,4219,4220,4221,4222,4223,4224,4225,4226,4227,4228,4229,4230,4231,4232,4233,
            4234,4235,4236,4237,4238,4239,4240,4241,4242,4243,4244,4245,4246,4247,30200,30201,30202,30203,30204,
            30205,30206,30207,30208,30209,38539,38541,38542,38543,38545,38546,38548,38549,38551,38552,38554,
            38555,38557,38558,38560,38561,38563,38565,38566,38568,38569,38570,38572,38573,38574,38576 })
            _byWeaponDefId[id] = Spinning(Icon14758, 2609, 4732);

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, WarriorWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

    // REAL ability name Global.Text ids — reversed from the client en_us_data. Fills the AbilitiesScreen
    // Attack/Special columns. Ability descriptions aren't mined yet (DescId 0 -> blank tooltip). The novelty
    // weapons' own unique ability names (Cake Cutter/Party Crasher/Love Struck/Heart Breaker/Angro Chop/
    // Vanquish/Feral Swipe/Feral Spirit/Freezing Strike/Snow Storm/Electric Punch/Lightning Blast/Regal Strike/
    // Royal Power/Ember Strike/Ignite/Twilight Strike/Vampiric Wrath/Deck the Halls/Candy Hurricane) don't have
    // a resolved Global.Text id yet (no T4-hash pass was done for them this session) - they fall back to NameId
    // 0 (blank AbilitiesScreen label) via SlotNameIcon below, same as any other unmined name.
    private static readonly IReadOnlyDictionary<string, int> AbilityNameIds = new Dictionary<string, int>
    {
        // specials
        ["Spinning Attack"] = 442458, ["Cleave"] = 420527, ["Quake"] = 24694, ["Warcry"] = 421063,
        ["Whirlwind"] = 421252, ["Axe Throw"] = 421253, ["Berserk"] = 7933, ["Frenzy"] = 421281,
        ["Commanding Shout"] = 24706, ["Thunderclap"] = 421305,
        // melee (basic attack) flavor names
        ["Sweeping Slash"] = 420252, ["Fierce Edge"] = 420521, ["Power Slash"] = 420987, ["Dual Strike"] = 421059,
        ["Gale Axe"] = 421254, ["Reckless Strike"] = 421255, ["Hack 'n' Slash"] = 35612, ["Crushing Blow"] = 421269,
        ["Dizzying Blow"] = 421292, ["Rampage"] = 421293,
    };

    public static (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot)
    {
        ByWeaponDefId.TryGetValue(weaponDefId, out var weapon);
        var ability = weapon is null ? BareMelee : (slot == 1 ? weapon.Special : weapon.Melee);
        var nameId = AbilityNameIds.TryGetValue(ability.Name, out var id) ? id : 0;
        return (nameId, 0, ability.IconImageId);
    }

    public static List<ItemDefinition.ItemAbilityEntry> BuildItemAbilityEntries(int weaponDefId)
    {
        var (_, _, meleeIcon) = SlotNameIcon(weaponDefId, 0);
        var (_, _, specialIcon) = SlotNameIcon(weaponDefId, 1);
        return new List<ItemDefinition.ItemAbilityEntry>
        {
            new() { Slot = 0, Id = MeleeSlotDefId, IconId = meleeIcon },
            new() { Slot = 1, Id = SpecialSlotDefId, IconId = specialIcon },
        };
    }

    public static (int NameId, int DescId, int IconId)? ResolveDefinition(Player player, int abilityDefId)
    {
        var slot = abilityDefId switch
        {
            MeleeSlotDefId => 0,
            SpecialSlotDefId => 1,
            _ => -1,
        };
        if (slot < 0)
            return null;

        return SlotNameIcon(player.GetEquippedWeaponDefinitionId(), slot);
    }

    public static WarriorWeapon? GetEquippedWeapon(Player player)
    {
        var defId = player.GetEquippedWeaponDefinitionId();
        return defId != 0 && ByWeaponDefId.TryGetValue(defId, out var weapon) ? weapon : null;
    }

    // slot 0 = melee (swing anim picked by weapon type), slot 1 = special.
    public static WeaponAbility ResolveAbility(Player player, int slot)
    {
        var defId = player.GetEquippedWeaponDefinitionId();

        if (!ByWeaponDefId.TryGetValue(defId, out var weapon))
            return BareMelee with { Animation = MeleeAnimFor(defId) };

        return slot <= 0 ? weapon.Melee with { Animation = MeleeAnimFor(defId) } : weapon.Special;
    }

    public const int SpecialEnergyCost = 100;

    public static AbilityPacketSetDefinition BuildToolbar(Player player, IResourceManager resources)
    {
        var weapon = GetEquippedWeapon(player);

        if (weapon is null)
            return AbilityPacketSetDefinition.CreateEmpty(WarriorProfileId);

        var nameId = 0;
        if (resources.ClientItemDefinitions.TryGetValue(player.GetEquippedWeaponDefinitionId(), out var weaponDef))
            nameId = weaponDef.NameId;

        var def = new AbilityPacketSetDefinition { ProfileId = WarriorProfileId, SlotCount = 8 };

        def.Slots.Add(MakeSlot(MeleeSlotDefId, weapon.Melee.IconImageId, nameId, manaCost: 0));
        def.Slots.Add(MakeSlot(SpecialSlotDefId, weapon.Special.IconImageId, nameId, manaCost: SpecialEnergyCost));

        return def;
    }

    private static AbilityPacketSetDefinition.Slot MakeSlot(int abilityDefId, int iconId, int nameId, int manaCost) => new()
    {
        Type = 3,
        Unknown2 = abilityDefId,
        ManaCost = manaCost,
        IconId = iconId,
        NameId = nameId,
        AbilityDefinitionId = abilityDefId,
    };
}
