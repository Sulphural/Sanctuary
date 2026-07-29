using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// WIZARD (profile 12) — wands, ranged elemental caster (glass cannon). Weapon-driven like the other kits: the
// equipped "Wizard's <Wand> of <Special>" (or a themed/novelty wand) grants a BASIC cast (slot 0) + a named
// SPECIAL (slot 1). Rebuilt 2026-07-29 from the OSFR community combat spreadsheet
// (docs.google.com/spreadsheets/d/1_p8Wxy-ZCBCqveDlm8MyH4HEdMa-eSNHuyI8ImYx2bE) — same treatment as
// MedicWeaponAbilities.cs: this fixes the bug where every weapon tier that carried a given special (e.g. every
// "Shock" wand from a level-1 Sparkle Twig to a level-16 Ornate Wand) shared ONE formula-scaled Kit object
// (Scale(ShockKit, TierLx)), so the numbers were an interpolated curve, not the real per-item tooltip value.
// Every real weapon item below now gets its own factory call with its OWN spreadsheet-sourced numbers — and
// several turned out to not even follow the tier curve the old formula assumed (e.g. Wizard's Jewel Wand of
// Shock/Vortex use 1492 melee at L12 while every other Jewel Wand special uses 1357; Gem Wand's generic PENDING
// bracket is 1492/6107, NOT the 2609/10674 every other L12 generic wand uses) — real, faithfully preserved
// anomalies, not typos.
//
// ICONS: the old file's "no per-element basic icon ships, MeleeIcon(fireburst) covers every kit" claim was
// WRONG — the spreadsheet's icons/anim tab has a full BASIC ATTACKS section with a dedicated, numbered-variant
// Icon IMAGE_ID for every "Zap N"/"Chill N"/"Burn N"/etc. row, and different real weapons in the SAME element
// line use DIFFERENT variants (e.g. "Zap 6" vs "Zap 7" vs "Zap 10" all appear on different Shock-line wands).
// Per-weapon melee icons below are matched against the EXACT variant that weapon's own spreadsheet row lists,
// same mechanic as Medic's per-weapon melee icons. MeleeIcon(fireburst,280) is kept only as the unarmed/no-wand
// BareMelee fallback now, not reused across every real kit.
// SPECIAL ANIM: the old header's "10 slots for exactly Wizard's 10 named specials, 1131-1140, RE-VERIFIED"
// claim doesn't match the spreadsheet's own per-ability Animation ID column, which gives DIFFERENT (and
// non-sequential) real ids: Firestorm 1018, Ice Nova 1139, Lightning Blast 1138, Tsunami 1137, Energy Vortex
// 1017, Protective Barrier 1132, Chaos Explosion 1140 (sheet flags this one ambiguous vs. 1061141). All marked
// "PENDING" (community-sourced) in the sheet's own Anim Status column, same caveat Medic's anim ids carry — but
// real sourced data beats the old file's un-sourced sequential guess, same precedent as Medic. Chain
// Lightning/Arcane Chain/Mass Transfigure have no anim value in the sheet at all (UNKNOWN) — those 3 keep the
// old file's own sequential-guess values, now clearly the weaker fallback of the group.
// FX: unchanged from the previous pass — already verified against ActorCompositeEffectDefinitions.xml and
// matches the spreadsheet's own FX EffectDef Name column exactly for every special that has one.
// DAMAGE: every CONFIRMED row's numbers are used verbatim. PENDING rows (generic dye/reward-wheel wands whose
// exact basic-attack variant is marked "(?)" in the sheet) still carry a REAL tooltip number — just not tied to
// one specific icon variant — so they're grouped by their real PENDING damage bracket and given the bare
// Zap/Lightning Blast icon as the closest defensible stand-in (documented simplification, not invented data),
// same convention as Medic's dye-range treatment for Mega Saw.
public sealed record WizardWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class WizardWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int WizardProfileId = 12;

    // All wands cast the same way (AnimationGroups.xml): basic com_cast_01, specials per-ability (see header).
    private const int CastAnim = 1111;         // com_cast_01
    private const int CastHitFx = 7;           // PFX_Hit_Flash — generic impact flash (basic + iconless fallback).

    // Unarmed/no-wand fallback icon only now (see header) — abil_wizard_fireburst.
    private const int MeleeIcon = 280;

    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    private static int MeleeAnimFor(int weaponDefId) => CastAnim; // every wand casts

    public static readonly WeaponAbility BareMelee = new("Zap", MeleeIcon, 200, CastAnim, CastHitFx);

    // ── Basic-cast "bolt" CastEffectIds — the projectile trail that plays regardless of which exact ability-name
    // variant is granted; doesn't vary by weapon tier, only by element family (same convention as the special
    // FX below). Real ids, unchanged from the previous pass.
    private const int BoltLightning = 5492;   // PRJ_lightning_ball_light-blue_1 — Zap/Shock(basic) bolt
    private const int BoltFreeze = 16110;     // freezing bolt — Chill/Freeze bolt
    private const int BoltFire = 5479;        // fireball bolt — Burn/Scorch/Boom bolt
    private const int BoltWater = 15610;      // water trail bolt — Splash bolt
    private const int BoltArcane = 16188;     // arcane sparkles bolt — Blast/Flare bolt

    // ── Real per-weapon-instance BASIC-cast icons (icons/anim tab, BASIC ATTACKS section) — the exact numbered
    // variant used varies weapon-by-weapon within the same element line, e.g. one Shock wand grants "Zap 6"
    // while another grants "Zap 10"; each _byWeaponDefId entry below is matched against its own row's variant.
    private const int IcoZap = 294, IcoZap2 = 14956, IcoZap3 = 14433, IcoZap4 = 14463, IcoZap5 = 14457,
        IcoZap6 = 14439, IcoZap7 = 4347, IcoZap8 = 14451, IcoZap9 = 4359, IcoZap10 = 4339;
    private const int IcoChill = 14433, IcoChill2 = 4347, IcoChill3 = 14451, IcoChill4 = 4359, IcoChill5 = 4339;
    private const int IcoBurn = 14433, IcoBurn2 = 14451, IcoBurn3 = 4359, IcoBurn4 = 4339;
    private const int IcoSplash = 14433, IcoSplash2 = 4359, IcoSplash3 = 4339, IcoSplash4 = 14451;
    private const int IcoBlast = 14451, IcoBlast2 = 4359, IcoBlast3 = 4339;
    // "Shock" is Chain Lightning's OWN basic-cast name — distinct from the "Zap"/Lightning Blast kit above,
    // despite the coincidental name overlap with the special-slot "Shock Paddles"-style naming in other jobs.
    private const int IcoShockBasic = 14451, IcoShockBasic2 = 4359, IcoShockBasic3 = 4339;
    private const int IcoScorch = 14451, IcoScorch2 = 4359;
    private const int IcoFreeze = 14451, IcoFreeze2 = 4359;
    private const int IcoBoom = 14451;
    private const int IcoFlare = 14451;

    // ── Real SPECIAL icons (icons/anim tab, SUPER ATTACKS section). Lightning Blast is the only special whose
    // icon varies by numbered variant (others repeat the same icon across all their variants) — CONFIRMED
    // exact-name matches, unchanged from the previous pass except IcoLightningBlast itself: was 295 (an
    // unconfirmed "closest guess"), the sheet's own Icon IMAGE_ID column gives 294 exactly (same value as the
    // bare "Zap" basic-cast icon — a real, confirmed coincidence in the client's own data, not a copy/paste
    // mistake on this end).
    private const int IcoLightningBlast = 294, IcoLightningBlast2 = 2236, IcoLightningBlast4 = 23006;
    private const int IcoIceNova = 283, IcoFirestorm = 22611, IcoTsunami = 23034, IcoEnergyVortex = 23025,
        IcoChainLightning = 23019, IcoArcaneChain = 22608, IcoProtBarrier = 23031, IcoChaos = 23022,
        IcoMassTransfig = 23028;

    // Novelty-kit icons (Wooing Wand / Red Ryder Rod / Balloon Wand / Wizard's Nature Wand / Orbital Wand) — all
    // real, exact-name Icon IMAGE_IDs from the same sheet (Charm/Heart Breaker/Jingle Spells/Candy
    // Hurricane/Party Tricks/Party Crasher/Feral Blast/Feral Spirit/Starshower/Orbital Strike rows all list a
    // real icon, unlike Snowflake Wand's real pairing below which has none).
    private const int IcoCharm = 30209, IcoHeartBreaker = 30190, IcoJingleSpells = 28516, IcoCandyHurricane = 27727,
        IcoPartyTricks = 31020, IcoPartyCrasher = 291, IcoFeralBlast = 39217, IcoFeralSpirit = 39237,
        IcoStarshower = 14445, IcoOrbitalStrike = 294;

    // ── Real SPECIAL FX (ActorCompositeEffectDefinitions.xml, cross-checked against the sheet's FX EffectDef
    // Name column) — unchanged from the previous pass, doesn't vary by weapon tier.
    private const int LightningBlastFx = 16305;   // PFX_electricity_fwd_circ_lg_wizard-lightning-blast
    private const int IceNovaFx = 16172;           // PFX_ice_white_explosion_lg_wizard-ice-nova
    private const int FirestormFx = 16026;         // PFX_wizard_firestorm_level-5
    private const int TsunamiFx = 16187;           // PFX_sparkles_blue_root_wizard_tsunami
    private const int EnergyVortexFx = 16151;      // PFX_sparkles-smoke_purple_cog_wizard-energy-vortex
    private const int ChainLightningFx = 16291;    // PRJ_lightning_blue_trail_loop_wizard-chain-lightning (not a true impact asset, see below)
    private const int ArcaneChainFx = 16041, ArcaneChainCastFx = 16036;   // p2p beam / cast-hands
    private const int ProtBarrierCastFx = 16124;   // PFX_shield_purple_lg_loop_wizard-protective-barrier (self-buff, plays on caster)
    private const int ChaosFx = 16126, ChaosCastFx = 16125;               // launch / cast-hands
    // Mass Transfigure has no dedicated match in either the sheet or ActorCompositeEffectDefinitions.xml (the
    // sheet's own column is blank); 16170 = a genuine "transfiguration-land" composite (not name-matched to
    // Mass Transfigure specifically) kept from the previous pass as the closest available real asset.
    private const int MassTransfigFx = 16170;

    // ── Real SPECIAL anims (icons/anim tab Animation ID column, PENDING status — see header). Chain
    // Lightning/Arcane Chain/Mass Transfigure have no sheet value (UNKNOWN) and keep the old file's own
    // sequential-guess ids as the weaker fallback.
    private const int FirestormAnim = 1018;
    private const int IceNovaAnim = 1139;
    private const int LightningBlastAnim = 1138;
    private const int TsunamiAnim = 1137;
    private const int EnergyVortexAnim = 1017;
    private const int ProtBarrierAnim = 1132;
    private const int ChaosAnim = 1140;            // sheet flags ambiguous vs 1061141 (that id belongs to a different job's dedicated clip family)
    private const int ChainLightningAnim = 1136;   // UNKNOWN in the sheet — old sequential-guess fallback
    private const int ArcaneChainAnim = 1137;      // UNKNOWN in the sheet — old sequential-guess fallback
    private const int MassTransfigAnim = 1140;     // UNKNOWN in the sheet — old sequential-guess fallback (coincides with Chaos's real value above, not an error)

    // ── TRAITS ── (effects from the ZAM job page; magnitudes ours) — unchanged, not part of this pass's scope.
    //   L5 Ice Armor · L10 Genius · L15 Magical Shielding · L20 Arcane Flare.
    public const int IceArmorLevel = 5;
    public const int GeniusLevel = 10;
    public const int MagicalShieldingLevel = 15;
    public const int ArcaneFlareLevel = 20;

    // Gameplay magnitudes (ours to tune):
    // Genius: an unlocked Wizard rolls crits on its hits at base + this bonus chance.
    public const int BaseCritChancePercent = 5;
    public const int GeniusCritChanceBonus = 15;
    public const float BaseCritMultiplier = 2.0f;
    // Magical Shielding: take this share less damage.
    public const float MagicalShieldingDamageReduction = 0.20f;
    // Arcane Flare: a crit absorbs a little energy back (chance on crit).
    public const int ArcaneFlareEnergyRestore = 10;
    // Ice Armor (freeze-on-hit) has no server-side stun model yet -> passive/display only.

    // REAL name/desc Global.Text ids + real trait icons (abil_wizard_* Small IMAGE_IDs). NameId/DescId/IconId/Level.
    private static readonly JobTraits.Trait[] TraitData =
    [
        new(420954, 420978, 289,   IceArmorLevel),        // Ice Armor (shielding)
        new(420955, 420979, 22614, GeniusLevel),          // Genius (intensity)
        new(4048,   420980, 22617, MagicalShieldingLevel),// Magical Shielding (magic_barrier)
        new(420957, 420981, 26727, ArcaneFlareLevel),     // Arcane Flare (arcane_flair)
    ];

    public static List<AbilityExperience> BuildTraitEntries(int rank) => JobTraits.Build(TraitData, rank, WizardProfileId);

    public static bool HasTrait(Player player, int traitLevel) =>
        player.ActiveProfileId == WizardProfileId && player.ActiveProfile.Rank >= traitLevel;

    // ── SPECIALS (10 standard element lines) ── one factory function per special TYPE, called once PER REAL
    // WEAPON ITEM with that item's own spreadsheet-CONFIRMED (or bracket-grouped PENDING) numbers — NOT a
    // single shared Kit object interpolated across tiers (that was the bug, see file header). AoeRadius > 0 =>
    // hits every hostile in range/cone.
    private static WizardWeapon Shock(int meleeIcon, int meleeDmg, int specialIcon, int specialDmg) => new(
        new("Zap", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltLightning, CastEffectStopMs: 1200),
        new("Lightning Blast", specialIcon, specialDmg, LightningBlastAnim, LightningBlastFx, BoltLightning, CastEffectStopMs: 1200));

    private static WizardWeapon Glaciers(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Chill", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltFreeze, CastEffectStopMs: 1200),
        new("Ice Nova", IcoIceNova, specialDmg, IceNovaAnim, IceNovaFx, AoeRadius: 10f));

    private static WizardWeapon Firestorm(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Burn", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltFire, CastEffectStopMs: 1200),
        new("Firestorm", IcoFirestorm, specialDmg, FirestormAnim, FirestormFx, AoeRadius: 10f));

    private static WizardWeapon Tsunami(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Splash", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltWater, CastEffectStopMs: 1200),
        new("Tsunami", IcoTsunami, specialDmg, TsunamiAnim, TsunamiFx, AoeRadius: 10f));

    private static WizardWeapon Vortex(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Blast", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltArcane, CastEffectStopMs: 1200),
        new("Energy Vortex", IcoEnergyVortex, specialDmg, EnergyVortexAnim, EnergyVortexFx, AoeRadius: 8f));

    // Chain-jump mechanic (target-to-target) isn't modeled — treated as single-target, same documented gap as
    // the old file (AoeRadius stays 0; see header for the anim id source change).
    private static WizardWeapon ChainLightning(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Shock", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltLightning, CastEffectStopMs: 1200),
        new("Chain Lightning", IcoChainLightning, specialDmg, ChainLightningAnim, ChainLightningFx, AoeRadius: 8f));

    // Also a chain-jump special (Arcane Chain) — same single-target simplification as Chain Lightning above.
    private static WizardWeapon ArcaneFire(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Scorch", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltFire, CastEffectStopMs: 1200),
        new("Arcane Chain", IcoArcaneChain, specialDmg, ArcaneChainAnim, ArcaneChainFx, CastEffectId: ArcaneChainCastFx, CastEffectStopMs: 1200));

    // Protective Barrier is a self/party SHIELD, not a damage nuke — modeled as a flat "hit" like every other
    // special here for lack of a buff-application path in this file (same documented limitation as before);
    // EffectId stays 0 (no target-impact FX for a self-buff), CastEffectId is the real shield loop on the caster.
    private static WizardWeapon Energy(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Freeze", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltFreeze, CastEffectStopMs: 1200),
        new("Protective Barrier", IcoProtBarrier, specialDmg, ProtBarrierAnim, 0, CastEffectId: ProtBarrierCastFx, CastEffectStopMs: 3000));

    private static WizardWeapon Chaos(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Boom", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltFire, CastEffectStopMs: 1200),
        new("Chaos Explosion", IcoChaos, specialDmg, ChaosAnim, ChaosFx, CastEffectId: ChaosCastFx, AoeRadius: 10f, CastEffectStopMs: 1000));

    private static WizardWeapon Transmute(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Flare", meleeIcon, meleeDmg, CastAnim, CastHitFx, BoltArcane, CastEffectStopMs: 1200),
        new("Mass Transfigure", IcoMassTransfig, specialDmg, MassTransfigAnim, MassTransfigFx, AoeRadius: 10f));

    // ── NOVELTY / THEMED KITS ── real names/icons/damage from the same sheet, own distinct ability names (not
    // variants of the 10 standard lines above). No FX/anim data exists for Charm/Heart Breaker or Jingle
    // Spells/Candy Hurricane or Party Tricks/Party Crasher/Feral Blast/Feral Spirit/Starshower/Orbital Strike in
    // either sheet (blank columns) — all use the generic cast/hit fallback, flagged per kit, same convention as
    // Medic's own novelty weapons.
    private static readonly WizardWeapon WooingKit = new(
        // Wooing Wand (item 76811). Real damage/icons, no FX/anim listed.
        new("Charm", IcoCharm, 2372, CastAnim, CastHitFx),
        new("Heart Breaker", IcoHeartBreaker, 6575, CastAnim, CastHitFx));

    private static readonly WizardWeapon RedRyderKit = new(
        // Red Ryder Rod (item 76562). "Variable" level item — uses the sheet's top-tier (Lvl 16) numbers, same
        // convention Medic's PowerFistKit used for its own variable-scaling weapon. No FX/anim listed.
        new("Jingle Spells", IcoJingleSpells, 2372, CastAnim, CastHitFx),
        new("Candy Hurricane", IcoCandyHurricane, 8302, CastAnim, CastHitFx));

    private static readonly WizardWeapon BalloonKit = new(
        // Balloon Wand (items 16365-16369 reward-wheel dye variants + 77449 "Reward Version", per the sheet's
        // own "Three variants; both combat versions share ability data" note — same treatment as Medic's own
        // Balloon Saw). No FX/anim listed.
        new("Party Tricks", IcoPartyTricks, 2372, CastAnim, CastHitFx),
        new("Party Crasher", IcoPartyCrasher, 7685, CastAnim, CastHitFx));

    private static readonly WizardWeapon NatureKit = new(
        // Wizard's Nature Wand (item 78201). Real icons/damage (top-tier, Lvl 16, of a "Variable" scaling
        // item) — supersedes the previous pass's thematic reuse of Mass Transfigure's icon/numbers, now that
        // Feral Blast/Feral Spirit both resolve to their own real icons in the sheet. No FX/anim listed.
        new("Feral Blast", IcoFeralBlast, 2372, CastAnim, CastHitFx),
        new("Feral Spirit", IcoFeralSpirit, 9132, CastAnim, CastHitFx));

    private static readonly WizardWeapon OrbitalKit = new(
        // Orbital Wand (items 13674/55339). Real pairing per the sheet's "New School Orbital Wand" row
        // (Starshower/Orbital Strike, top-tier Lvl 16 numbers) — supersedes the previous pass's WebSearch-only
        // citation and its Energy-Vortex-kit reuse, now that both abilities resolve to real icons in the sheet.
        // No FX/anim listed.
        new("Starshower", IcoStarshower, 2372, CastAnim, CastHitFx),
        new("Orbital Strike", IcoOrbitalStrike, 8302, CastAnim, CastHitFx));

    // Snowflake Wand's REAL pairing (per the sheet) is Magic Snowball/Ice Storm, NOT Ice Nova — but neither
    // ability has an icon, FX, or anim id anywhere in either sheet (truly blank columns, not just PENDING), so
    // implementing the real pair would mean inventing assets with no source. Kept as a Glaciers()-kit call
    // below (reusing Ice Nova's real visuals) but now with Magic Snowball/Ice Storm's OWN real top-tier damage
    // (2372/9132) instead of the old tier-scaled formula number — same "real number, reused/thematic visuals"
    // honesty convention as Medic's Triage-FX-on-a-thematic-match case.

    // weapon def id -> kit. Real client Wizard wands (Sparkle Twig L1, Wand L4/5, Bone Wand L8, Jewel Wand L12,
    // Ornate Wand L16) — ids verified directly against ClientItemDefinitions.json's Comment field, numbers
    // against the spreadsheet's weapon-summary tab (CONFIRMED rows unless noted). Weapons whose spreadsheet name
    // has no matching item anywhere in ClientItemDefinitions.json (content this server build doesn't have —
    // "Old School Forked/Orbital/Shard Wand", "New School Forked/Shard Wand", "Wizard's Tentacle Wand of
    // Riptide", "Wizard's Forged Wand of Cunning", "Wizard's Awakened Wand of Forbidden Magic") are left
    // unmapped rather than guessed, per the same rule Medic's file follows.
    private static readonly Dictionary<int, WizardWeapon> _byWeaponDefId = new()
    {
        // ── Sparkle Twig (L1) ──
        [75150] = Shock(IcoZap7, 279, IcoLightningBlast, 1257),      // Wizard's Sparkle Twig of Shock — "Zap 7"/"Lightning Blast 3"
        [75151] = Glaciers(IcoChill2, 254, 704),                     // Wizard's Sparkle Twig of Glaciers — "Chill 2"
        [48171] = Shock(IcoZap6, 279, IcoLightningBlast, 1143),      // Monarch Wand — "Zap 6"
        [4914] = Shock(IcoZap6, 279, IcoLightningBlast4, 1143),      // Student Wizard Wand (starter) — "Zap 6"/"Lightning Blast 4"
        [30003] = Shock(IcoZap6, 279, IcoLightningBlast4, 1143),     // Student Wizard Wand (2nd id)

        // ── Wand (L4/L5 — spreadsheet lists Shock/Glaciers/Firestorm at real level 4, Tsunami at real level 5) ──
        [75152] = Shock(IcoZap3, 488, IcoLightningBlast, 2197),      // Wizard's Wand of Shock — "Zap 3"/"Lightning Blast 3"
        [75153] = Glaciers(IcoChill, 444, 1230),                     // Wizard's Wand of Glaciers
        [75154] = Firestorm(IcoBurn, 444, 885),                      // Wizard's Wand of Firestorm
        [75155] = Tsunami(IcoSplash, 444, 1998),                     // Wizard's Wand of Tsunami (real level 5)
        [48189] = Shock(IcoZap3, 488, IcoLightningBlast, 1998),      // Confetti Wand
        [48219] = Shock(IcoZap3, 488, IcoLightningBlast, 1998),      // Gravel Wand
        [48195] = Shock(IcoZap3, 488, IcoLightningBlast, 1998),      // Party Wand
        [48207] = Shock(IcoZap3, 488, IcoLightningBlast, 1998),      // Sunlit Wand

        // ── Bone Wand (L8) ──
        [75156] = Shock(IcoZap10, 853, IcoLightningBlast, 3841),     // Wizard's Bone Wand of Shock — "Zap 10"/"Lightning Blast 3"
        [75157] = Glaciers(IcoChill5, 776, 1955),                    // Wizard's Bone Wand of Glaciers — "Chill 5"
        [75158] = Firestorm(IcoBurn4, 776, 1548),                    // Wizard's Bone Wand of Firestorm — "Burn 4"
        [75159] = Tsunami(IcoSplash3, 776, 3492),                    // Wizard's Bone Wand of Tsunami — "Splash 3"
        [75160] = Vortex(IcoBlast3, 853, 3841),                      // Wizard's Bone Wand of Vortex — "Blast 3"
        [75161] = ChainLightning(IcoShockBasic3, 776, 3492),         // Wizard's Bone Wand of Lightning — "Shock 3"
        [22204] = Shock(IcoZap2, 1492, IcoLightningBlast2, 6107),    // Venom's Touch — "Zap 2"/"Lightning Blast 2"

        // ── Jewel Wand (L12) — NOTE the real per-item split: Shock/Vortex use 1492 melee, every other special
        // in this tier uses 1357 — a genuine spreadsheet anomaly, kept faithfully rather than normalized. ──
        [75162] = Shock(IcoZap9, 1492, IcoLightningBlast, 6717),     // Wizard's Jewel Wand of Shock — "Zap 9"/"Lightning Blast 3"
        [75163] = Glaciers(IcoChill4, 1357, 3762),                   // Wizard's Jewel Wand of Glaciers — "Chill 4"
        [75164] = Firestorm(IcoBurn3, 1357, 2707),                   // Wizard's Jewel Wand of Firestorm — "Burn 3"
        [75165] = Tsunami(IcoSplash2, 1357, 6717),                   // Wizard's Jewel Wand of Tsunami — "Splash 2"
        [75166] = Vortex(IcoBlast2, 1492, 6717),                     // Wizard's Jewel Wand of Vortex — "Blast 2"
        [75167] = ChainLightning(IcoShockBasic2, 1357, 6717),        // Wizard's Jewel Wand of Lightning — "Shock 2"
        [75168] = ArcaneFire(IcoScorch2, 1357, 6717),                // Wizard's Jewel Wand of Arcane Fire — "Scorch 2"/"Arcane Chain 2"
        [75169] = Energy(IcoFreeze2, 1357, 1330),                    // Wizard's Jewel Wand of Energy — "Freeze 2"
        [38672] = Shock(IcoZap2, 2609, IcoLightningBlast2, 10674),   // Aqua Wand — "Zap 2"/"Lightning Blast 2"
        [22207] = Shock(IcoZap4, 2609, IcoLightningBlast2, 10674),   // Fiery Wand — "Zap 4"/"Lightning Blast 2"
        [38693] = Shock(IcoZap4, 2609, IcoLightningBlast2, 10674),   // Fiery Wand (dye variant)
        [48147] = Shock(IcoZap4, 2609, IcoLightningBlast2, 10674),   // Fiery Wand (dye variant)
        [4962] = Shock(IcoZap, 2609, IcoLightningBlast2, 10674),     // All-Star Wizard Wand — bare "Zap"/"Lightning Blast 2"

        // ── Spectrum Wand (L13 — real level per the sheet, despite the 853/3492 numbers matching the L8
        // bracket rather than a scaled-up L12/13 number; kept faithful). ──
        [48243] = Shock(IcoZap10, 853, IcoLightningBlast, 3492),     // Spectrum Wand — "Zap 10"

        // ── Ornate Wand (L16) — all 10 specials ──
        [75170] = Shock(IcoZap8, 2609, IcoLightningBlast, 11741),    // Wizard's Ornate Wand of Shock — "Zap 8"/"Lightning Blast 3"
        [75171] = Glaciers(IcoChill3, 2372, 5977),                   // Wizard's Ornate Wand of Glaciers — "Chill 3"
        [75172] = Firestorm(IcoBurn2, 2372, 4732),                   // Wizard's Ornate Wand of Firestorm — "Burn 2"/"Firestorm 2"
        [75173] = Tsunami(IcoSplash4, 2372, 10674),                  // Wizard's Ornate Wand of Tsunami — "Splash 4"/"Tsunami 2"
        [75174] = Vortex(IcoBlast, 2609, 11741),                     // Wizard's Ornate Wand of Vortex — bare "Blast"
        [75175] = ChainLightning(IcoShockBasic, 2372, 10674),        // Wizard's Ornate Wand of Lightning — bare "Shock"/"Chain Lightning 2"
        [75176] = ArcaneFire(IcoScorch, 2372, 10674),                // Wizard's Ornate Wand of Arcane Fire — bare "Scorch"
        [75177] = Energy(IcoFreeze, 2372, 2324),                     // Wizard's Ornate Wand of Energy — bare "Freeze"
        [75178] = Chaos(IcoBoom, 2372, 2557),                        // Wizard's Ornate Wand of Chaos — "Boom"
        [75179] = Transmute(IcoFlare, 2372, 6575),                   // Wizard's Ornate Wand of Transmutation — "Flare"
        [48319] = Firestorm(IcoBurn3, 2372, 4732),                   // Wand of Spectral Fire (L20) — "Burn 3"/"Firestorm", same numbers as the Ornate tier

        // ── Novelty / themed wands (own distinct ability pairs, see the kit definitions above) ──
        [76811] = WooingKit,                          // Wooing Wand
        [76562] = RedRyderKit,                         // Red Ryder Rod
        [78201] = NatureKit,                           // Wizard's Nature Wand
        [13674] = OrbitalKit, [55339] = OrbitalKit,    // Orbital Wand
        [78718] = Glaciers(IcoChill3, 2372, 9132),     // Snowflake Wand — real pairing is Magic Snowball/Ice Storm (no assets exist for either, see comment above); real top-tier damage, Ice Nova visuals reused
        [13673] = ChainLightning(IcoShockBasic, 2372, 10674),   // Forked Wand — no surviving spreadsheet row for the plain (non "Old/New School") item; reuses the confirmed Ornate-tier Chain Lightning numbers as the closest sourced analogue
        [55332] = ChainLightning(IcoShockBasic, 2372, 10674),   // Forked Wand (2nd id)
    };

    // Large dye/tint/reward-wheel id ranges whose spreadsheet row marks the exact basic-attack variant
    // uncertain ("Zap (?)"/"Lightning Blast (?)") but still carries a REAL PENDING damage number — grouped by
    // that real bracket, all using the bare Zap/Lightning Blast icon as the closest defensible stand-in (see
    // file header). Field initializers run before this ctor body, so AllWeaponDefIds (snapshotted at the end)
    // picks these up too.
    static WizardWeaponAbilities()
    {
        // Forked Wand's own 34-id dye/tint range (13673/55332 above are the two "flagship" ids; this is the rest).
        for (var id = 55779; id <= 55813; id++)
            _byWeaponDefId[id] = ChainLightning(IcoShockBasic, 2372, 10674);

        // L1 bracket (279/1143) — Twig Wand's big dye/reward range + Amateur Wizard Wand/Comet Infused
        // Wand/Power Charmed Wand/Butterfly Wand (all real ids, same real PENDING number).
        var l1Pending = Shock(IcoZap, 279, IcoLightningBlast, 1143);
        foreach (var id in new[] {
            177,1198,1199,1200,1201,1202,4902,4903,4904,4905,4906,4907,4908,4909,4910,4911,4912,4913,4915,4916,
            4917,4918,4919,4920,4921,4922,4923,4924,4925,4926,4927,4928,4929,4930,4931,4932,4933,4934,4935,4936,
            9204,9205,9206,9207,30000,30001,30002,30004,30005,30006,30007,30008,30009,38610,38612,38614,38616,
            38618,38620,38622,38624,38626,38628 }) // Twig Wand
            _byWeaponDefId[id] = l1Pending;
        foreach (var id in new[] { 9208 }) // Amateur Wizard Wand
            _byWeaponDefId[id] = l1Pending;
        foreach (var id in new[] { 30300,38611,38615,38617,38619,38621,38623,38625,38627 }) // Comet Infused Wand
            _byWeaponDefId[id] = l1Pending;
        foreach (var id in new[] { 30301,30302,30303,30304,30305,30306,30307,30308,30309,38609 }) // Power Charmed Wand
            _byWeaponDefId[id] = l1Pending;
        foreach (var id in new[] { 48165 }) // Butterfly Wand
            _byWeaponDefId[id] = l1Pending;

        // Diadem bracket (3391/13876) — Star Flow / Coin Flow Diadem Wand.
        var diademPending = Shock(IcoZap, 3391, IcoLightningBlast, 13876);
        foreach (var id in new[] { 49380, 49600, 49610 })
            _byWeaponDefId[id] = diademPending;

        // L4/L5 bracket (488/1998) — Bolt Wand's big dye/reward range + Lunar Enchanted/Pro Wizard/Solar
        // Charmed/Gemstone/Carbon/Jeweled Wand + Forest Twig/Nature's Twig (both real level-1 items that
        // anomalously use this bracket's numbers instead of the L1 279/1143 bracket — kept faithful).
        var l4Pending = Shock(IcoZap, 488, IcoLightningBlast, 1998);
        foreach (var id in new[] {
            7175,7176,7177,7178,7179,7181,7182,7183,7184,7185,7187,7188,7189,7190,7191,7192,7193,7194,7195,7196,
            7197,7198,7199,7200,7201,7202,7203,7204,7205,7206,7207,7208,7209,9209,9210,9211,9212,9213,30060,
            30061,30062,30063,30064,30065,30066,30067,30068,30069,38631,38633,38634,38635,38637,38639,38641,
            38643,38645,38647,38649 }) // Bolt Wand
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 7186 }) // Lunar Enchanted Wand
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 7380 }) // Pro Wizard Wand
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 30362, 30366, 38630, 38632, 38636, 38638, 38640, 38644, 38646, 38648 }) // Solar Charmed Wand
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 48183 }) // Gemstone Wand
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 48201 }) // Carbon Wand
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 48177 }) // Jeweled Wand (real level 5, not 12 — see spreadsheet)
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 22105 }) // Nature's Twig
            _byWeaponDefId[id] = l4Pending;
        foreach (var id in new[] { 38629 }) // Forest Twig
            _byWeaponDefId[id] = l4Pending;

        // L8-A bracket (853/3492) — Bone Wand's big dye/reward range + Batty/Rainbow Wand.
        var l8APending = Shock(IcoZap, 853, IcoLightningBlast, 3492);
        foreach (var id in new[] {
            7210,7211,7212,7213,7214,7217,7218,7219,7220,7221,7222,7223,7224,7225,7226,7227,7228,7229,7230,7231,
            7232,7233,7234,7235,7236,7237,7238,7239,7240,7241,7242,7243,7244,9214,9215,9216,9217,9218,9219,9220,
            30120,30121,30122,30123,30124,30125,30126,30127,30128,30129,38652,38654,38656,38658,38660,38662,
            38664,38666,38668,38670 }) // Bone Wand
            _byWeaponDefId[id] = l8APending;
        foreach (var id in new[] { 48225 }) // Batty Wand
            _byWeaponDefId[id] = l8APending;
        foreach (var id in new[] { 48237 }) // Rainbow Wand
            _byWeaponDefId[id] = l8APending;

        // L8-B bracket (1492/6107) — Eclipse Infused/Juiced/Storm Charmed/Tidal Wand + Gem Wand's big dye/reward
        // range (Gem Wand's real PENDING numbers anomalously match this L8 bracket, not the 2609/10674 every
        // other L12 generic wand uses — kept faithful, see file header).
        var l8BPending = Shock(IcoZap, 1492, IcoLightningBlast, 6107);
        foreach (var id in new[] { 30420, 30421, 30422, 30423, 30424, 38659, 38663, 38665, 38667, 38669 }) // Eclipse Infused Wand
            _byWeaponDefId[id] = l8BPending;
        foreach (var id in new[] { 22103 }) // Juiced Wand
            _byWeaponDefId[id] = l8BPending;
        foreach (var id in new[] { 30425, 30426, 30427, 30428, 30429, 38651, 38653, 38655, 38657, 38661 }) // Storm Charmed Wand
            _byWeaponDefId[id] = l8BPending;
        foreach (var id in new[] { 22205 }) // Tidal Wand
            _byWeaponDefId[id] = l8BPending;
        foreach (var id in new[] {
            7347,7348,7349,7350,7351,7354,7355,7356,7357,7358,7359,7360,7361,7362,7363,7364,7365,7366,7367,7368,
            7369,7370,7371,7372,7373,7374,7375,7376,7377,7378,7379,7381,9221,9222,9223,9224,9225,9226,30180,
            30181,30182,30183,30184,30185,30186,30187,30188,30189,38674,38676,38678,38680,38682,38684,38686,
            38688,38690,38692 }) // Gem Wand
            _byWeaponDefId[id] = l8BPending;

        // L12/L16 bracket (2609/10674) — Ornate Wand's big dye/reward range + Illuminating/Frostflame/Tempest
        // Woven/Toxic Touch/Smokey/Luminous/Nether Enchanted/Void Woven/Magical Essence Ornate Wand.
        var l12l16Pending = Shock(IcoZap, 2609, IcoLightningBlast, 10674);
        foreach (var id in new[] {
            4937,4938,4939,4940,4941,4944,4945,4946,4947,4948,4949,4950,4951,4952,4953,4954,4955,4956,4957,4958,
            4959,4960,4961,4963,4964,4965,4966,4967,4968,4969,4970,4971,9227,9228,9229,9230,9231,9232,30240,
            30241,30242,30243,30244,30245,30246,30247,30248,30249,38695,38697,38699,38701,38703,38705,38707,
            38709,38711,38713 }) // Ornate Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 22206 }) // Illuminating Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 27937 }) // Frostflame Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 30480, 30482, 30483, 30484, 30485, 30486, 30488, 38675, 38687, 38691 }) // Tempest Woven Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 38671 }) // Toxic Touch
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 23021 }) // Smokey Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 29932 }) // Luminous Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 30540, 30547, 30549, 38696, 38698, 38700, 38702, 38706, 38708, 38712 }) // Nether Enchanted Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 30541, 30542, 30543, 30544, 30545, 30546, 30548, 38694, 38704, 38710 }) // Void Woven Wand
            _byWeaponDefId[id] = l12l16Pending;
        foreach (var id in new[] { 55821 }) // Magical Essence Ornate Wand
            _byWeaponDefId[id] = l12l16Pending;

        // Balloon Wand — reward-wheel dye variants + the "Reward Version" id.
        foreach (var id in new[] { 16365, 16366, 16367, 16368, 16369, 77449 })
            _byWeaponDefId[id] = BalloonKit;

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, WizardWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

    // REAL ability name Global.Text ids — reversed from the client en_us_data. Fills the AbilitiesScreen
    // Attack/Special columns. Novelty-kit ability names (Charm/Heart Breaker/Jingle Spells/Candy
    // Hurricane/Party Tricks/Party Crasher/Feral Blast/Feral Spirit/Starshower/Orbital Strike) aren't mined
    // yet — SlotNameIcon falls back to NameId 0 (blank column) for those specifically, same as before this pass.
    // Ability descriptions aren't mined yet either (DescId 0 -> blank tooltip).
    private static readonly IReadOnlyDictionary<string, int> AbilityNameIds = new Dictionary<string, int>
    {
        // specials
        ["Lightning Blast"] = 442459, ["Ice Nova"] = 23679, ["Firestorm"] = 420995, ["Tsunami"] = 421073,
        ["Energy Vortex"] = 421256, ["Chain Lightning"] = 421257, ["Arcane Chain"] = 421282,
        ["Protective Barrier"] = 421283, ["Chaos Explosion"] = 421306, ["Mass Transfigure"] = 421307,
        // basic-cast flavor names
        ["Zap"] = 17071, ["Chill"] = 420539, ["Burn"] = 7999, ["Splash"] = 421067, ["Blast"] = 421258,
        ["Shock"] = 421259, ["Scorch"] = 421270, ["Freeze"] = 104321, ["Boom"] = 421294, ["Flare"] = 421295,
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

    public static WizardWeapon? GetEquippedWeapon(Player player)
    {
        var defId = player.GetEquippedWeaponDefinitionId();
        return defId != 0 && ByWeaponDefId.TryGetValue(defId, out var weapon) ? weapon : null;
    }

    // slot 0 = basic cast, slot 1 = special. Every wand uses the same cast anim.
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
            return AbilityPacketSetDefinition.CreateEmpty(WizardProfileId);

        var nameId = 0;
        if (resources.ClientItemDefinitions.TryGetValue(player.GetEquippedWeaponDefinitionId(), out var weaponDef))
            nameId = weaponDef.NameId;

        var def = new AbilityPacketSetDefinition { ProfileId = WizardProfileId, SlotCount = 8 };

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
