using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// BRAWLER (profile 43) — hammers + power fists, blunt melee. Weapon-driven like the ninja/medic kits: the
// equipped "Brawler's <Hammer> of <Special>" weapon (or a novelty hammer/fist) grants a MELEE (slot 0) + a
// named SPECIAL (slot 1).
//
// ★ NO item-def injection: the Brawler weapons are REAL client coin-shop items; seeding their shared item-def
// Abilities lists broke the client for everyone. So BrawlerJobKit.WeaponDefIds is EMPTY — this kit only drives
// the equipped-weapon TOOLBAR + traits + combat for a Brawler player, never the shared item defs.
//
// DATA SOURCE (rewritten 2026-07-29): the OSFR community combat spreadsheet's Brawler tabs (icons/anim gid=
// 1733456277, weapon-summary gid=82321510), cross-referenced against src/Resources/ClientItemDefinitions.json
// by exact Comment-field match for real item ids. This REPLACES the previous file's bug: every "of <Special>"
// weapon tier (Mallet/Hammer/Anvil/Drill/Atlas) shared ONE static BrawlerWeapon object per special type, so 5
// completely different real weapons (e.g. every "of Sweeps" hammer from L1 to L16) all showed the exact same
// "Leg Sweep" damage number despite the wiki-sourced spreadsheet having 5 distinct real per-tier numbers. Every
// one of the 30 real "of <Special>" items (75030-75059) now gets its own real per-tier melee+special damage
// pair via a per-TYPE factory function (LegSweep/Rumble/Pummel/Roundhouse/SuckerPunch/KickDirt/Slam/HammerToss/
// Enrage/Knockout), same pattern as MedicWeaponAbilities.Triage(...)/Vitals(...)/etc. Also fixed along the way:
// three novelty items (plain-Comment "Exploding Hammer"/"Golden Hammer"/"Torque Trasher", real ids 9034/13657/
// 55364, 13658/55365, 13659/55335) were previously mapped to Rumble/Slam/Pummel kits by pure theme-guessing;
// the spreadsheet's matching "Old School <X> Hammer"/"Old School Torque Trasher" rows show all three are
// actually Leg-Sweep-type specials (3083/10792) — corrected below. Same fix for the "Variable"-tier novelty
// weapons (Bellringer/Gleam Energy Fist/Gloam Energy Fist/Hole-In-One Golf Driver), which were previously
// reusing an unrelated real kit (Knockout/SuckerPunch/Pummel/HammerToss) purely because their OWN real ability
// names/numbers hadn't been sourced yet — each now gets its own distinct kit with its own real sheet numbers
// (using the sheet's top/L16 bracket, same convention MedicWeaponAbilities used for its own Power Fist).
//
// CONFIRMED vs PENDING: the spreadsheet marks each weapon-summary row CONFIRMED (trustworthy exact tooltip
// read) or PENDING (real number, but the exact ability-name NUMBERED VARIANT — e.g. "Glancing Blow 7" vs bare
// "Glancing Blow" — is uncertain, marked "(?)" in the sheet). Every PENDING row's NUMBER is used here anyway
// (it's real data, not a guess) — only the melee ICON can be affected by the variant ambiguity, and where the
// sheet gives no specific variant a same-tier icon is reused as a sensible stand-in (flagged per group below)
// rather than left blank. Many low/mid-tier novelty items (Kendama Hammer/Log Hammer/Anvil Hammer/Crystal
// Hammer/Driller plain-name dye clusters, Berserk/Buff Bruiser Hammer, Frenzy/Alley Fighter Hammer, etc.) only
// have ONE spreadsheet row for potentially dozens of dye-tint item ids — same "dye doesn't change stats"
// convention as Medic's Mega Saw range, handled via foreach loops in the static ctor below.
//
// ANIM/FX per special TYPE are UNCHANGED from the previous pass (already real, extracted from AnimationGroups.
// xml/AnimationTypes.xml + ActorCompositeEffectDefinitions.xml and cross-checked against this same spreadsheet
// — the icons/anim tab's EffectId/Icon IMAGE_ID values for Pummel/Leg Sweep/Roundhouse/Ready to Rumble/Sucker
// Punch/Kick Dirt/Knockout/Hammer Toss/Enrage/Slam all match the already-verified constants byte-for-byte).
// melee swing = com_h2h_attack(1000) for fists / com_2hp_attack(1080) for 2h hammers (picked per weapon);
// 2h-hammer specials = com_2hp_special_01..08 (1091-1098), auto-translated to the com_h2h_special_01..08
// (1011-1018) fist equivalent for FistWeaponDefIds weapons via FistSpecialAnimFor. EffectId = impact FX on the
// TARGET; CastEffectId = FX on the CASTER.
// ICONS are REAL: the abil_brawler_* Small IMAGE_IDs from client Resources/Images (ImageSets.txt ->
// ImageSetMappings.txt type 5) for the 10 core specials, PLUS (new this pass) real per-weapon-tier MELEE icons
// straight from the icons/anim tab's Basic Attacks section, matched by each weapon's exact ability-instance
// name (e.g. "Glancing Blow 6" for Brawler's Hammer of Sweeps) — same technique as Medic's Icon4254/Icon3961.
// Trait NAME/DESC locale ids still want reversing (left as-is, out of scope for this pass).
public sealed record BrawlerWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class BrawlerWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int BrawlerProfileId = 43;

    // Melee swing anims (AnimationGroups.xml): fists punch, 2h hammers swing. Picked per equipped weapon.
    private const int FistMeleeAnim = 1000;    // com_h2h_attack
    private const int HammerMeleeAnim = 1080;  // com_2hp_attack
    private const int MeleeHitFx = 7;          // PFX_Hit_Flash — generic impact flash.

    // REAL ability icons — the abil_brawler_* image sets' Small (type-5) IMAGE_IDs, resolved from the client
    // Resources/Images (ImageSets.txt set id -> ImageSetMappings.txt type 5). The basic-attack slot (bare/
    // unarmed) uses bum_rush (a charging punch) since there's no dedicated "attack" icon.
    private const int MeleeIcon = 22581;       // abil_brawler_bum_rush

    // Real per-WEAPON-TIER melee icons from the spreadsheet's icons/anim tab (Basic Attacks section), matched
    // by the exact numbered ability-instance name each real tier's weapon-summary row uses (e.g. "Glancing Blow
    // 7" for the Mallet tier, "Glancing Blow 6"/"Wallop 4"/"Pound 4"/"Smack 4" all sharing 4236 at the Hammer
    // tier). Confirmed: every tier's basic-attack variants (Glancing Blow/Wallop/Pound/Smack/Wild Swing/Whack/
    // Smash/Crush/Thump/Bash) share the SAME Icon IMAGE_ID within a tier — a real client mechanic (one icon per
    // weapon MODEL, not per move name), not a coincidence.
    private const int IconMallet = 4242;  // Mallet tier (L1): Glancing Blow 7, Wallop 5
    private const int IconHammerT = 4236; // Hammer tier (L4/5): Glancing Blow 6, Wallop 4, Pound 4, Smack 4
    private const int IconAnvilT = 4200;  // Anvil Hammer tier (L8): Glancing Blow 3, Wallop, Pound, Smack, Wild Swing, Whack
    private const int IconDrillT = 4224;  // Drill Hammer tier (L12): Glancing Blow 5, Wallop 3, Pound 3, Smack 3, Wild Swing 3, Whack 3, Smash 2, Crush 2
    private const int IconAtlasT = 4212;  // Atlas Hammer tier (L16): Glancing Blow 4/10, Wallop 2, Pound 2, Smack 2, Whack 2, Smash, Crush, Bash, Thump

    // Extra named-variant melee icons for the dye-cluster/novelty items below (real Icon IMAGE_IDs from the
    // sheet's Basic Attacks section, matched by their exact numbered variant name).
    private const int IconGB2 = 14253;  // Glancing Blow 2 — plain "Anvil Hammer" item
    private const int IconGB9 = 496;    // Glancing Blow 9 / Glancing Blow 13 (sheet gives both the same id) — Spunky/Strong Scrapper Hammer, Crystal Hammer (Level 1)
    private const int IconGB11 = 14277; // Glancing Blow 11 — "Driller" item (also reused for the Drill-named novelty hammers below)
    private const int IconGB12 = 14301; // Glancing Blow 12 — "Exploding Hammer"/"Golden Hammer" novelty items
    private const int IconGB14 = 14127; // Glancing Blow 14 / Glancing Blow 15 (sheet gives both the same id) — Student Brawler Hammer, "Torque Trasher"

    // IcoPummel/Roundhouse/Rumble/SuckerPunch/KickDirt/Knockout/HammerToss/Slam all come from the same
    // abil_brawler_<name> image-set block (sets 4879-4886, ImageSets.txt lines 4199-4206 -> ImageSetMappings.txt
    // type 5). IcoLegSweep/IcoEnrage look numerically out of place next to that block but ARE real dedicated
    // icons too — they just live in a SEPARATE, earlier-allocated image-set block: "abil_brawler_fight_leg_sweep"
    // (set 2653) and "abil_brawler_fight_enrage" (set 2652), ImageSets.txt lines 1973-1974 -> ImageSetMappings.txt
    // gives Small=11636 / 11633 respectively (verified 2026-07-25, and re-confirmed against the spreadsheet's own
    // Icon IMAGE_ID columns this pass — 22926/11637(~11636)/22932/22929/22938/22920/22923/22917/11633/22935 all
    // match). Not a bug — just two different ID ranges.
    private const int IcoPummel = 22926, IcoLegSweep = 11636, IcoRoundhouse = 22932, IcoRumble = 22929,
        IcoSuckerPunch = 22938, IcoKickDirt = 22920, IcoKnockout = 22923, IcoHammerToss = 22917,
        IcoEnrage = 11633, IcoSlam = 22935,
        // IcoPowerRain: confirmed NO dedicated abil_brawler_power_rain / abil_brawler_power_fist image set
        // exists (grepped ImageSets.txt for "power fist", "power rain", "brawler_power", "punch", "fist" on
        // 2026-07-25 — only item-model icons like item_fist_ar_ag_weapon_* turned up, no ability icon). The
        // spinattack fallback (set 870, abil_brawler_spinattack) stays the closest available generic icon; also
        // reused below as the generic-AoE stand-in for Gleam Explosion/Gloam Fire, which have no icon at all.
        IcoPowerRain = 3373; // spinattack (no dedicated Power Rain/Power Fist icon exists in the client tables)

    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    // Fist-model weapons swing h2h; everything else (hammers/clubs/axes) swings 2hp.
    private static readonly HashSet<int> FistWeaponDefIds = new() { 13659, 55335, 78197, 78712, 78713 };
    private static int MeleeAnimFor(int weaponDefId) => FistWeaponDefIds.Contains(weaponDefId) ? FistMeleeAnim : HammerMeleeAnim;

    // com_2hp_special_01..08 (1091-1098, AnimationTypes.xml) <-> com_h2h_special_01..08 (1011-1018) — a
    // confirmed, exact -80 id offset for every one of the 8 slots (verified against AnimationTypes.xml
    // 2026-07-25). Used to translate a hammer-special kit's Animation to the fist-special equivalent when a
    // FistWeaponDefIds weapon is equipped.
    private const int FistSpecialAnimOffset = -80;
    private static int FistSpecialAnimFor(int hammerSpecialAnim) =>
        hammerSpecialAnim is >= 1091 and <= 1098 ? hammerSpecialAnim + FistSpecialAnimOffset : hammerSpecialAnim;

    public static readonly WeaponAbility BareMelee = new("Punch", MeleeIcon, 150, HammerMeleeAnim, MeleeHitFx);

    // ── TRAITS ── (effects from the ZAM job page; magnitudes ours):
    //   L5 Bruising Strikes · L10 Savvy · L15 Toughness · L20 Resilience.
    public const int BruisingStrikesLevel = 5;
    public const int SavvyLevel = 10;
    public const int ToughnessLevel = 15;
    public const int ResilienceLevel = 20;

    // Gameplay magnitudes (ours to tune):
    // Bruising Strikes: an unlocked Brawler rolls crits on its hits at base + this bonus chance.
    public const int BaseCritChancePercent = 5;
    public const int BruisingStrikesCritChanceBonus = 15;
    // Savvy: a crit does 2x normally, +this once Savvy is unlocked (-> 2.75x).
    public const float BaseCritMultiplier = 2.0f;
    public const float SavvyCritBonus = 0.75f;
    // Toughness: take 15% less damage before being knocked out.
    public const float ToughnessDamageReduction = 0.15f;
    // Resilience: restore this much health each time you're hit.
    public const int ResilienceHealPerHit = 120;

    // REAL name/desc Global.Text ids (reversed from en_us_data) + real trait icons (abil_brawler_* Small
    // IMAGE_IDs; Savvy/Resilience have no dedicated art -> moxie/vitality). NameId/DescId/IconId/Level.
    private static readonly JobTraits.Trait[] TraitData =
    [
        new(420938, 420962, 22578, BruisingStrikesLevel), // Bruising Strikes
        new(420939, 420963, 3461,  SavvyLevel),           // Savvy (moxie icon)
        new(76943,  420964, 22584, ToughnessLevel),       // Toughness
        new(420941, 420965, 2056,  ResilienceLevel),      // Resilience (vitality icon)
    ];

    public static List<AbilityExperience> BuildTraitEntries(int rank) => JobTraits.Build(TraitData, rank, BrawlerProfileId);

    public static bool HasTrait(Player player, int traitLevel) =>
        player.ActiveProfileId == BrawlerProfileId && player.ActiveProfile.Rank >= traitLevel;

    // ── SPECIALS (10 real "of <Special>" types + Enrage/Knockout at the top tier) ── one factory function per
    // TYPE, parameterized by (meleeIcon, meleeDmg, specialDmg) and called ONCE PER REAL WEAPON ITEM ID with
    // that item's own real numbers — NOT a single shared kit object reused across every weapon that carries a
    // given special (that was the bug: every "of Sweeps" hammer from L1 to L16 showed the identical "Leg
    // Sweep" 7500 number). AoeRadius > 0 => hits every hostile within range of the caster. Special anim/FX/icon
    // per TYPE are unchanged from the previous (already-verified) pass — see file header.
    private static BrawlerWeapon LegSweep(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Glancing Blow", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Leg Sweep", IcoLegSweep, specialDmg, 1092, 5275, CastEffectId: 16195, AoeRadius: 10f));

    private static BrawlerWeapon Rumble(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Wallop", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Ready to Rumble", IcoRumble, specialDmg, 1094, 16212));

    private static BrawlerWeapon Pummel(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Pound", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Pummel", IcoPummel, specialDmg, 1091, 5252, CastEffectId: 5257)); // star-rings on hands + pummel land

    private static BrawlerWeapon Roundhouse(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Smack", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Roundhouse Kick", IcoRoundhouse, specialDmg, 1093, 5315, CastEffectId: 16194, AoeRadius: 10f));

    private static BrawlerWeapon SuckerPunch(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Wild Swing", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Sucker Punch", IcoSuckerPunch, specialDmg, 1095, 16198, CastEffectId: 16198));

    private static BrawlerWeapon KickDirt(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Whack", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Kick Dirt", IcoKickDirt, specialDmg, 1096, 16206, CastEffectId: 16206, AoeRadius: 10f));

    private static BrawlerWeapon Slam(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Smash", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Slam", IcoSlam, specialDmg, 1092, 5252)); // heavy impact, reused from Pummel — no dedicated FX exists

    private static BrawlerWeapon HammerToss(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Crush", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Hammer Toss", IcoHammerToss, specialDmg, 1098, 15203, CastEffectId: 16289, CastEffectStopMs: 1500));

    private static BrawlerWeapon Knockout(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Thump", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Knockout", IcoKnockout, specialDmg, 1097, 16200)); // knockout orange on target

    // Enrage's special damage is unresolved on BOTH real items that carry it (the sheet lists "-" for Atlas
    // Hammer of Rage) — only the MELEE number below is real/per-item; specialDmg stays the pre-existing 8000
    // estimate (same un-sourced-number convention as Medic's Nurse!/Vitamins).
    private static BrawlerWeapon Enrage(int meleeIcon, int meleeDmg) => new(
        new("Bash", meleeIcon, meleeDmg, HammerMeleeAnim, MeleeHitFx),
        new("Enrage", IcoEnrage, 8000, 1091, MeleeHitFx, CastEffectId: 16145)); // enrage cast aura

    // ── Novelty "Variable"-tier kits ── each of these is its OWN distinct real item with its OWN real ability
    // names (previously mis-mapped to an unrelated core kit purely by theme-guessing — see file header). Numbers
    // use the sheet's top/L16 bracket for the "Variable" per-rank tables, same convention MedicWeaponAbilities
    // used for its own Power Fist kit.
    private static readonly BrawlerWeapon PowerFistKit = new(
        // Brawler's Power Fist (item 78197). Sheet row is PENDING ("Power Smash (?)"/"Power Rain (?)") but the
        // per-rank table's L16 bracket (2372/8302) is real data — replaces the previous flat 1800/6000 guess.
        new("Power Smash", MeleeIcon, 2372, FistMeleeAnim, MeleeHitFx),
        new("Power Rain", IcoPowerRain, 8302, 1091, MeleeHitFx, CastEffectId: 16084, AoeRadius: 8f));

    private static readonly BrawlerWeapon TollingBellKit = new(
        // Bellringer (item 78717). Real ability names are "Ringing Smash"/"Tolling Bell" (NOT Knockout — the
        // previous file reused KnockoutKit here purely on a "ring the bell -> see stars" theme guess). Neither
        // name has a dedicated icon/FX in either sheet tab; Atlas tier's melee icon + Knockout's special
        // icon/anim are reused as the closest available stand-ins.
        new("Ringing Smash", IconAtlasT, 2372, HammerMeleeAnim, MeleeHitFx),
        new("Tolling Bell", IcoKnockout, 8302, 1097, MeleeHitFx));

    private static readonly BrawlerWeapon GleamKit = new(
        // Gleam Energy Fist (item 78712). Real names "Gleam Smash"/"Gleam Explosion" (previous file reused
        // SuckerPunchKit). No dedicated icon/FX for either name; spinattack (IcoPowerRain) reused as the
        // generic-AoE stand-in. Note Gleam Explosion's own L16 bracket (9132) differs from the standard 8302
        // most other top-tier specials use — real sheet number, not a mistake.
        new("Gleam Smash", IconAtlasT, 2372, FistMeleeAnim, MeleeHitFx),
        new("Gleam Explosion", IcoPowerRain, 9132, 1091, MeleeHitFx));

    private static readonly BrawlerWeapon GloamKit = new(
        // Gloam Energy Fist (item 78713). Real names "Gloam Smash"/"Gloam Fire" (previous file reused
        // PummelKit). Same no-icon/FX + 9132 L16-bracket note as Gleam above.
        new("Gloam Smash", IconAtlasT, 2372, FistMeleeAnim, MeleeHitFx),
        new("Gloam Fire", IcoPowerRain, 9132, 1091, MeleeHitFx));

    private static readonly BrawlerWeapon GolfDriverKit = new(
        // Hole-In-One Golf Driver (item 79020). Real names "Strong Layup"/"Power Drive" (previous file reused
        // HammerTossKit). "Strong Layup" has a real dedicated melee icon (45890); "Power Drive" has none —
        // Pummel's icon reused as the closest physical-impact stand-in.
        new("Strong Layup", 45890, 2372, HammerMeleeAnim, MeleeHitFx),
        new("Power Drive", IcoPummel, 8302, 1091, MeleeHitFx));

    private static readonly BrawlerWeapon MatchMakerKit = new(
        // Match-Maker Mallet (item 76471) — CONFIRMED row, not previously mapped at all. Real names "Tough
        // Love"/"Heart Breaker", both with real dedicated icons from the sheet (30197/30190).
        new("Tough Love", 30197, 2372, HammerMeleeAnim, MeleeHitFx),
        new("Heart Breaker", 30190, 6575, 1094, MeleeHitFx));

    private static readonly BrawlerWeapon CandyCaneKit = new(
        // Candy Cane Hammer (item 76558) — CONFIRMED row, not previously mapped. Real names "Season's
        // Beating"/"Candy Hurricane", both with real dedicated icons (28456/27727).
        new("Season's Beating", 28456, 2372, HammerMeleeAnim, MeleeHitFx),
        new("Candy Hurricane", 27727, 8302, 1093, MeleeHitFx));

    private static readonly BrawlerWeapon BitBasherKit = new(
        // Bit Basher (item 78558) — PENDING row, not previously mapped. Real names "Bonk!"/"Megabonk!". Bonk!'s
        // own L16 melee number (5218) is a genuinely different scale than the standard progression every other
        // kit in this file uses (254/444/776/1357/2372) — real sheet data, not a typo. Neither name has a
        // dedicated icon; Hammer-tier melee icon + Hammer Toss's special icon reused as stand-ins (both are
        // "big swinging hammer" moves).
        new("Bonk!", IconHammerT, 5218, HammerMeleeAnim, MeleeHitFx),
        new("Megabonk!", IcoHammerToss, 8302, 1098, MeleeHitFx));

    private static readonly BrawlerWeapon BalloonHammerKit = new(
        // Balloon Hammer (Reward/Coin-Shop/Gifting-Pinata versions all share one Comment in this item catalog;
        // uses the Reward Version's CONFIRMED numbers for all of them, same ambiguity-handling as Medic's own
        // Balloon Saw). Real names "Birthday Bash"/"Party Crasher", both with real dedicated icons (30996/291).
        new("Birthday Bash", 30996, 2372, HammerMeleeAnim, MeleeHitFx),
        new("Party Crasher", 291, 8453, 1093, MeleeHitFx));

    // weapon def id -> kit. Real client Brawler weapons — the 75030-75059 "of <Special>" item series, ids
    // verified directly against ClientItemDefinitions.json, numbers verified against the spreadsheet's exact
    // per-weapon ability-instance suffix (see the tier Icon consts' header comment above).
    //   Tiered "of X" set: Sweeps=Leg Sweep · Rumbling=Ready to Rumble · Pummeling=Pummel · Roundhouse=Roundhouse
    //   Kick · Cheapshot=Sucker Punch · Dirt Kick=Kick Dirt · Slammage=Slam · Chucking=Hammer Toss · Rage=Enrage ·
    //   Stars=Knockout.
    private static readonly Dictionary<int, BrawlerWeapon> _byWeaponDefId = new()
    {
        // Mallet (L1) — "Glancing Blow 7 (?)"/"Wallop 5"
        [75030] = LegSweep(IconMallet, 254, 889), [75031] = Rumble(IconMallet, 279, 640),
        // Hammer (L4/5) — "Glancing Blow 6"/"Wallop 4"/"Pound 4"/"Smack 4"
        [75032] = LegSweep(IconHammerT, 444, 1554), [75033] = Rumble(IconHammerT, 488, 1118),
        [75034] = Pummel(IconHammerT, 488, 659), [75035] = Roundhouse(IconHammerT, 444, 1554),
        // Anvil Hammer (L8) — "Glancing Blow 3"/"Wallop"/"Pound"/"Smack"/"Wild Swing"/"Whack"
        [75036] = LegSweep(IconAnvilT, 776, 2716), [75037] = Rumble(IconAnvilT, 853, 1955),
        [75038] = Pummel(IconAnvilT, 853, 1152), [75039] = Roundhouse(IconAnvilT, 776, 2716),
        [75040] = SuckerPunch(IconAnvilT, 776, 1955), [75041] = KickDirt(IconAnvilT, 776, 1955), // Dirt Kick special name is PENDING ("Kick Dirt (?)"), number is real
        // Drill Hammer (L12) — "Glancing Blow 5"/"Wallop 3"/"Pound 3"/"Smack 3"/"Wild Swing 3"/"Whack 3"/"Smash 2"/"Crush 2"
        [75042] = LegSweep(IconDrillT, 1357, 4750), [75043] = Rumble(IconDrillT, 1492, 3420),
        [75044] = Pummel(IconDrillT, 1492, 2015), [75045] = Roundhouse(IconDrillT, 1357, 4750),
        [75046] = SuckerPunch(IconDrillT, 1357, 3419), [75047] = KickDirt(IconDrillT, 1357, 3420),
        [75048] = Slam(IconDrillT, 1357, 4750), [75049] = HammerToss(IconDrillT, 1357, 4750),
        // Atlas Hammer (L16) — "Glancing Blow 4"/"Wallop 2"/"Pound 2"/"Smack 2"/"Wild Swing 2"/"Whack 2"/"Smash"/"Crush"/"Bash"/"Thump" — all 10 specials
        [75050] = LegSweep(IconAtlasT, 2372, 8302), [75051] = Rumble(IconAtlasT, 2609, 5977),
        [75052] = Pummel(IconAtlasT, 2609, 3522), [75053] = Roundhouse(IconAtlasT, 2372, 8302),
        [75054] = SuckerPunch(IconAtlasT, 2372, 5977), [75055] = KickDirt(IconAtlasT, 2372, 5977),
        [75056] = Slam(IconAtlasT, 2372, 8302), [75057] = HammerToss(IconAtlasT, 2372, 8302),
        [75058] = Enrage(IconAtlasT, 2372), [75059] = Knockout(IconAtlasT, 2372, 10674),

        // ── Novelty / coin-shop / reward Brawler weapons ── real item ids looked up by exact Comment match
        // against ClientItemDefinitions.json, real ability data from the spreadsheet's weapon-summary tab.
        [78197] = PowerFistKit,   // Brawler's Power Fist (fist_bikerfist)
        [78717] = TollingBellKit, // Bellringer
        [78712] = GleamKit,       // Gleam Energy Fist (fist)
        [78713] = GloamKit,       // Gloam Energy Fist (fist)
        [79020] = GolfDriverKit,  // Hole-In-One Golf Driver
        [76471] = MatchMakerKit,  // Match-Maker Mallet
        [76558] = CandyCaneKit,   // Candy Cane Hammer
        [78558] = BitBasherKit,   // Bit Basher

        // "Exploding Hammer"/"Golden Hammer"/"Torque Trasher" — plain-Comment novelty items matching the
        // sheet's "Old School Exploding/Golden Hammer"/"Old School Torque Trasher" rows. FIXED this pass: all
        // three were previously mapped to an unrelated kit (Rumble/Slam/Pummel) by theme-guessing; the sheet
        // shows all three are actually Leg-Sweep-type specials with real matching numbers (3083/10792).
        [9034] = LegSweep(IconGB12, 3083, 10792), [13657] = LegSweep(IconGB12, 3083, 10792), [55364] = LegSweep(IconGB12, 3083, 10792), // Exploding Hammer ("Glancing Blow 12")
        [13658] = LegSweep(IconGB12, 3083, 10792), [55365] = LegSweep(IconGB12, 3083, 10792), // Golden Hammer (PENDING variant, same numbers)
        [13659] = LegSweep(IconGB14, 3083, 10792), [55335] = LegSweep(IconGB14, 3083, 10792), // Torque Trasher, fist model ("Glancing Blow 15")

        // Single/small-id-count real items, mapped by exact Comment match. Most are PENDING rows (numbers real,
        // exact melee-variant icon uncertain) — a same-tier-scale icon is reused where the sheet gives none.
        [7604] = LegSweep(IconGB14, 254, 889),     // Student Brawler Hammer — CONFIRMED "Glancing Blow 14"
        [8099] = LegSweep(IconMallet, 254, 889),   // Amateur Brawler Hammer — PENDING
        [8023] = LegSweep(IconAtlasT, 2372, 8302), // All-Star Brawler Hammer — PENDING
        [22212] = LegSweep(IconAnvilT, 1357, 4750),  // Venom's Crush — PENDING
        [22111] = LegSweep(IconHammerT, 444, 1554),  // Nature's Stem — PENDING
        [37430] = LegSweep(IconHammerT, 444, 1554),  // Forest Stem — PENDING
        [22214] = LegSweep(IconGB11, 2372, 8302),    // Illuminating Drill — PENDING
        [22215] = LegSweep(IconGB11, 2372, 8302),    // Blazing Drill — PENDING
        [27939] = LegSweep(IconGB11, 2372, 8302),    // Frostflame Drill — PENDING
        [37537] = LegSweep(IconGB11, 2372, 8302),    // Fiery Drill — PENDING
        [23023] = LegSweep(IconAtlasT, 2372, 8302),  // Smokey Hammer — PENDING
        [27933] = LegSweep(IconAtlasT, 2372, 8302),  // Glacial Hammer — PENDING
        [29934] = LegSweep(IconAtlasT, 2372, 8302),  // Luminous Hammer — PENDING
        [48227] = LegSweep(IconAnvilT, 2372, 8302),  // Winged Anvil — PENDING
        [55820] = LegSweep(IconAtlasT, 2372, 8302),  // Magical Essence Crystal Hammer — PENDING
        [48320] = LegSweep(IconAtlasT, 2372, 8302),  // The Rumbler — PENDING
        [37493] = LegSweep(IconAnvilT, 776, 2716),   // Soapy Kendama Hammer — PENDING
        [48197] = LegSweep(IconAnvilT, 776, 2716),   // Carbon Kendama — PENDING
        [48185] = LegSweep(IconAnvilT, 776, 2716),   // Confetti Kendama Hammer — PENDING
        [48179] = LegSweep(IconAnvilT, 776, 2716),   // Gemstone Kendama Hammer — PENDING
        [48173] = LegSweep(IconAnvilT, 776, 2716),   // Jeweled Kendama Hammer — PENDING
        [48167] = LegSweep(IconAtlasT, 2372, 8302),  // Monarch Log Hammer — PENDING
        [49606] = LegSweep(IconAtlasT, 3083, 10792), // Coin Flow Mega Hammer — PENDING
    };

    // Large dye/tint-variant id ranges and small novelty clusters — real items, but the sheet only gives one
    // set of numbers per base weapon name (dye color doesn't change stats), so every variant in a cluster maps
    // to the same kit. Field initializers run before this ctor body, so AllWeaponDefIds (snapshotted at the
    // end) picks these up too. Same pattern as MedicWeaponAbilities' Mega Saw range.
    static BrawlerWeaponAbilities()
    {
        // "Anvil Hammer" (plain Comment, distinct from "Brawler's Anvil Hammer of X") — CONFIRMED row
        // "Glancing Blow 2"/776, Leg Sweep/2716. 69 dye-tint ids across 3 id ranges.
        foreach (var id in new[] {
            7977,7978,7979,7980,7981,7984,7985,7986,7987,7988,7989,7990,7991,7992,7993,7994,7995,7996,7997,7998,
            7999,8000,8001,8002,8003,8004,8005,8006,8007,8008,8009,8010,8011,
            30100,30101,30102,30103,30104,30105,30106,30107,30108,30109,
            37498,37499,37500,37501,37503,37504,37506,37507,37509,37511,37513,37514,37516,37517,37519,37520,
            37521,37523,37524,37526,37527,37529,37531,37532,37533,37535 })
            _byWeaponDefId[id] = LegSweep(IconGB2, 776, 2716);

        // "Crystal Hammer" (plain Comment) — THREE real spreadsheet rows share this name: Level 1 (444/1554,
        // "Glancing Blow 9"), Level 16 (PENDING, same numbers as Level 19) and Level 19 (2372/8302, CONFIRMED
        // "Glancing Blow 10"). Bucketed by each id's own MinProfileRank field (1 vs 16/19/20) rather than
        // guessed — only ONE id (8032) is actually the Level-1 variant.
        _byWeaponDefId[8032] = LegSweep(IconGB9, 444, 1554); // Crystal Hammer (Level 1)
        foreach (var id in new[] {
            8012,8013,8014,8015,8016,8019,8020,8021,8022,8024,8025,8026,8027,8028,8029,8030,8031,8033,8034,8035,
            8036,8037,8038,8039,8040,8041,8042,8043,8044,8045,8046,
            30220,30221,30223,30224,30225,30226,30227,30228,30229,
            37574,37575,37576,37577,37579,37580,37582,37583,37585,37586,37588,37589,37591,37592,37594,37595,
            37596,37598,37599,37601,37602,37603,37605,37606,37607,37609 })
            _byWeaponDefId[id] = LegSweep(IconAtlasT, 2372, 8302); // Crystal Hammer (Level 16/19/20)

        // "Driller" (plain Comment, distinct from "Brawler's Drill Hammer of X") — CONFIRMED row "Glancing
        // Blow 11"/1357, Leg Sweep/4750. 54 dye-tint ids.
        foreach (var id in new[] {
            8047,8048,8049,8051,8052,8053,8054,8055,8056,8057,8058,8059,8060,8061,8062,8063,8064,8065,8066,8067,
            8068,8069,8070,8071,8072,8073,8074,8075,8076,8077,8078,8079,8080,8081,
            30160,30161,30162,30163,30164,30165,30166,30167,30168,30169,
            37540,37543,37546,37549,37552,37555,37559,37563,37567,37571 })
            _byWeaponDefId[id] = LegSweep(IconGB11, 1357, 4750);

        // "Kendama Hammer" — PENDING row, 444/1554 (tier-4 scale). 68 dye-tint ids.
        foreach (var id in new[] {
            8082,8083,8084,8085,8086,8089,8090,8091,8092,8093,8094,8095,8096,8097,8098,8100,8101,8102,8103,8104,
            8105,8106,8107,8108,8109,8110,8111,8112,8113,8114,8115,8116,
            30040,30041,30042,30043,30044,30045,30046,30047,30048,30049,
            37461,37462,37463,37464,37466,37467,37469,37470,37472,37473,37475,37476,37478,37479,37481,37482,
            37483,37485,37486,37488,37489,37490,37492,37494,37495,37497 })
            _byWeaponDefId[id] = LegSweep(IconHammerT, 444, 1554);

        // "Log Hammer" — PENDING row, 254/889 (tier-1 scale). 69 dye-tint ids.
        foreach (var id in new[] {
            8117,8118,8119,8120,8121,8124,8125,8126,8127,8128,8129,8130,8131,8132,8133,8134,8135,8136,8137,8138,
            8139,8140,8141,8142,8143,8144,8145,8146,8147,8148,8149,8150,8151,
            29980,29981,29982,29983,29984,29985,29986,29987,29988,29989,
            37409,37411,37413,37414,37417,37419,37421,37422,37425,37426,37429,37432,37434,37435,37438,37439,
            37442,37444,37446,37448,37449,37451,37454,37456,37457,37460 })
            _byWeaponDefId[id] = LegSweep(IconMallet, 254, 889);

        // Spunky Scrapper Hammer — CONFIRMED "Glancing Blow 9"/254, Leg Sweep 2/889.
        foreach (var id in new[] { 30280,30281,30282,30283,30284,30285,30286,30287,30288,30289 })
            _byWeaponDefId[id] = LegSweep(IconGB9, 254, 889);

        // Strong Scrapper Hammer — CONFIRMED "Glancing Blow 13"/2372, Leg Sweep/8302.
        foreach (var id in new[] { 30460,30461,30462,30463,30464,30465,30466,30467,30468,30469 })
            _byWeaponDefId[id] = LegSweep(IconGB9, 2372, 8302);

        // Balloon Hammer (Reward/Coin-Shop versions share this Comment in the item catalog).
        foreach (var id in new[] { 16345,16346,16347,16348,16349,77445 })
            _byWeaponDefId[id] = BalloonHammerKit;

        // Berserk Bruiser Hammer — PENDING, 2372/8302.
        foreach (var id in new[] { 37578,37581,37584,37587,37590,37593,37597,37600,37604,37608 })
            _byWeaponDefId[id] = LegSweep(IconAtlasT, 2372, 8302);

        // Buff Bruiser Hammer — PENDING, 2372/8302.
        foreach (var id in new[] { 37541,37544,37547,37550,37553,37556,37560,37564,37568,37572 })
            _byWeaponDefId[id] = LegSweep(IconAtlasT, 2372, 8302);

        // Alley Fighter Hammer — PENDING, 1357/4750.
        foreach (var id in new[] { 30400,30401,30402,30403,30404,30405,30406,30407,30408,30409 })
            _byWeaponDefId[id] = LegSweep(IconDrillT, 1357, 4750);

        // Frenzy Fighter Hammer — PENDING, 2372/8302.
        foreach (var id in new[] { 30520,30521,30522,30523,30524,30525,30526,30527,30528,30529 })
            _byWeaponDefId[id] = LegSweep(IconAtlasT, 2372, 8302);

        // Rowdy Rumbler Hammer — PENDING, 444/1554.
        foreach (var id in new[] { 30340,30341,30342,30343,30344,30345,30346,30347,30348,30349 })
            _byWeaponDefId[id] = LegSweep(IconHammerT, 444, 1554);

        // Ring Rumbler Hammer — PENDING, 1357/4750.
        foreach (var id in new[] { 37502,37505,37508,37512,37515,37518,37522,37525,37530,37534 })
            _byWeaponDefId[id] = LegSweep(IconDrillT, 1357, 4750);

        // Street Scrapper Hammer — PENDING, 776/2716.
        foreach (var id in new[] { 37465,37468,37471,37474,37477,37480,37484,37487,37491,37496 })
            _byWeaponDefId[id] = LegSweep(IconAnvilT, 776, 2716);

        // Star Flow Mega Hammer — PENDING, 3083/10792.
        foreach (var id in new[] { 49376, 49596 })
            _byWeaponDefId[id] = LegSweep(IconAtlasT, 3083, 10792);

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, BrawlerWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

    // REAL ability name Global.Text ids — reversed from the client en_us_data (Jenkins lookup2 of
    // "Global.Text.<id>"). Fills the AbilitiesScreen Attack/Special columns. Ability descriptions aren't
    // mined yet (DescId 0 -> blank tooltip). Novelty-weapon ability names (Bellringer/Gleam/Gloam/Golf Driver/
    // Match-Maker/Candy Cane/Bit Basher/Balloon Hammer) have no resolved NameId yet — fall back to 0 (blank
    // column) same as any other unmined name, not previously present since these kits didn't exist before.
    private static readonly IReadOnlyDictionary<string, int> AbilityNameIds = new Dictionary<string, int>
    {
        // specials
        ["Pummel"] = 24097, ["Leg Sweep"] = 24133, ["Roundhouse Kick"] = 421038, ["Ready to Rumble"] = 420391,
        ["Sucker Punch"] = 410416, ["Kick Dirt"] = 422816, ["Knockout"] = 421299, ["Hammer Toss"] = 421275,
        ["Enrage"] = 21374, ["Slam"] = 73160, ["Power Rain"] = 438137,
        // melee (basic attack) flavor names
        ["Punch"] = 34043, ["Pound"] = 420598, ["Glancing Blow"] = 420172, ["Smack"] = 421036, ["Wallop"] = 420389,
        ["Wild Swing"] = 38633, ["Whack"] = 421241, ["Thump"] = 421287, ["Crush"] = 141485, ["Bash"] = 421286,
        ["Smash"] = 40430, ["Power Smash"] = 437177,
    };

    public static (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot)
    {
        ByWeaponDefId.TryGetValue(weaponDefId, out var weapon);
        var ability = weapon is null ? BareMelee : (slot == 1 ? weapon.Special : weapon.Melee);
        var nameId = AbilityNameIds.TryGetValue(ability.Name, out var id) ? id : 0;
        return (nameId, 0, ability.IconImageId);
    }

    // The two ability entries (slot 0 = Attack, 1 = Special) for a weapon's item def — feeds the columns.
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

    public static BrawlerWeapon? GetEquippedWeapon(Player player)
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

        if (slot <= 0)
            return weapon.Melee with { Animation = MeleeAnimFor(defId) };

        // Every kit's Special.Animation is authored as a com_2hp_special_XX (2h-hammer) clip. PummelKit-type,
        // SuckerPunch-type and PowerFistKit specials are also handed out to FIST weapons (FistWeaponDefIds —
        // see ByWeaponDefId above), so for those equips translate to the real com_h2h_special_XX fist-clip
        // equivalent (FistSpecialAnimFor) instead of playing a 2-handed hammer swing on bare/clawed fists.
        //
        // Known gap: 11 named specials share only 8 numbered anim slots (com_2hp_special_01..08 /
        // com_h2h_special_01..08), and neither AnimationGroups.xml nor AnimationTypes.xml labels which slot
        // is "supposed" to belong to which named ability — the slots are anonymous. So Enrage/Power Rain/Gleam
        // Explosion/Gloam Fire/Power Drive sharing Pummel's slot (1091) and Slam sharing Leg Sweep's slot
        // (1092) are an UNAVOIDABLE, not a confirmed-wrong, consequence of that 8-slot ceiling; there's no
        // retail source that says which of the 8 clips a given special "really" uses, so no reassignment here
        // would be any less of a guess.
        return weapon.Special with { Animation = FistWeaponDefIds.Contains(defId) ? FistSpecialAnimFor(weapon.Special.Animation) : weapon.Special.Animation };
    }

    public const int SpecialEnergyCost = 100;

    public static AbilityPacketSetDefinition BuildToolbar(Player player, IResourceManager resources)
    {
        var weapon = GetEquippedWeapon(player);

        if (weapon is null)
            return AbilityPacketSetDefinition.CreateEmpty(BrawlerProfileId);

        var nameId = 0;
        if (resources.ClientItemDefinitions.TryGetValue(player.GetEquippedWeaponDefinitionId(), out var weaponDef))
            nameId = weaponDef.NameId;

        var def = new AbilityPacketSetDefinition { ProfileId = BrawlerProfileId, SlotCount = 8 };

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
