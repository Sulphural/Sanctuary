using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// WIZARD (profile 12) — wands, ranged elemental caster (glass cannon). Weapon-driven like the other kits: the
// equipped "Wizard's <Wand> of <Special>" (or the Nature Wand) grants a BASIC cast (slot 0) + a named SPECIAL
// (slot 1). Abilities/weapons from the Free Realms wiki (allakhazam ZAM item pages).
//
// ANIM: RE-VERIFIED 2026-07-25 against the client's own tables (not a guess — the prior audit's doubt was
//   unfounded). AnimationTypes.xml has a `com_cast_special_01..10` AnimationSlot family at ids 1131-1140
//   (exactly 10 entries, immediately followed by a SEPARATE 1061141+ "com_cast_special_11..25" block used by
//   other jobs' dedicated clips — see NinjaWeaponAbilities' Mystical Blade), and AnimationGroups.xml wraps each
//   in a same-named AnimationGroup at the same id. 10 slots for exactly Wizard's 10 named specials is the
//   correct family (basic cast stays com_cast_01/1111).
// FX: RE-VERIFIED 2026-07-25 against ActorCompositeEffectDefinitions.xml. EVERY special below now cites the
//   dedicated `PFX_wizard_*` / `PFX_*_wizard-*` composite it plays (grep for "wizard" in that file — all ids
//   16026-16305 in the table are the game's own purpose-built Wizard-special effects, not reused placeholders).
//   Protective Barrier's EffectId was fixed (see its kit below) — a self-buff has no "enemy hit", so the
//   generic PFX_Hit_Flash it played on the target is gone.
// ICONS/NAMES are REAL: abil_wizard_* Small (type-5) IMAGE_IDs (RE-VERIFIED 2026-07-25 against
//   Images/ImageSets.txt + ImageSetMappings.txt — every special icon below resolves to an `abil_wizard_*` set
//   whose name matches the ability, except Lightning Blast, see its constant) + name/desc Global.Text ids
//   reversed from the client en_us_data.
// DAMAGE: base numbers (used as-is on the top L16 Ornate Wand) are ours to tune (higher than melee jobs —
//   ranged glass cannon); TIER SCALING by wand level (added 2026-07-25) mirrors ArcherWeaponAbilities' own
//   wiki-anchored curve shape/ratios (Wizard has no independent retail damage source of its own to anchor to)
//   — see Scale()/TierL* below.
public sealed record WizardWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class WizardWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int WizardProfileId = 12;

    // All wands cast the same way (AnimationGroups.xml): basic com_cast_01, specials com_cast_special_01..10.
    private const int CastAnim = 1111;         // com_cast_01
    private const int CastHitFx = 7;           // PFX_Hit_Flash — generic impact flash (basic).

    // REAL ability icons — abil_wizard_* Small (type-5) IMAGE_IDs, confirmed 2026-07-25 against
    // Images/ImageSets.txt (set name) + ImageSetMappings.txt (set id -> type5 flat IMAGE_ID). Basic-cast slot
    // uses fireburst for every kit (no per-element basic icon ships).
    private const int MeleeIcon = 280;         // set 918 abil_wizard_fireburst, type5 -> 280 (basic cast)
    // IcoIceNova: set 919 abil_wizard_icenova -> 283. IcoFirestorm: set 4802 abil_wizard_firestorm -> 22611.
    // IcoTsunami: set 4918 abil_wizard_tsunami -> 23034. IcoEnergyVortex: set 4915 abil_wizard_energy_vortex ->
    // 23025. IcoChainLightning: set 4913 abil_wizard_chain_lightning -> 23019. IcoArcaneChain: set 4801
    // abil_wizard_arcane_chain -> 22608. IcoProtBarrier: set 4917 abil_wizard_protective_barrier -> 23031.
    // IcoChaos: set 4914 abil_wizard_chaos_explosion -> 23022 (name matches "Chaos Explosion" exactly; the
    // OLDER set 2664 abil_wizard_chaos -> 11669 is a legacy/unused duplicate, not used here).
    // IcoMassTransfig: set 4916 abil_wizard_mass_transfigure -> 23028. All six of these are exact name matches
    // to their ability — CONFIRMED, unchanged from before.
    // IcoLightningBlast: NO dedicated "lightning_blast" set exists in the client (only abil_wizard_zap/922->295
    // and abil_wizard_chain_lightning/4913->23019, already used by Chain Lightning). 295 (zap) is the closest
    // real match — Lightning Blast and the Shock kit's basic "Zap" are the same lightning-bolt visual family —
    // so it's left as a documented REUSE, not a fabricated id. Flagged honestly, not "fixed" for lack of a
    // better source.
    private const int IcoLightningBlast = 295, IcoIceNova = 283, IcoFirestorm = 22611, IcoTsunami = 23034,
        IcoEnergyVortex = 23025, IcoChainLightning = 23019, IcoArcaneChain = 22608, IcoProtBarrier = 23031,
        IcoChaos = 23022, IcoMassTransfig = 23028;

    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    private static int MeleeAnimFor(int weaponDefId) => CastAnim; // every wand casts

    public static readonly WeaponAbility BareMelee = new("Zap", MeleeIcon, 200, CastAnim, CastHitFx);

    // ── TRAITS ── (effects from the ZAM job page; magnitudes ours):
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

    // ── SPECIALS (10) ── basic cast (slot 0) + the named special (slot 1). AoeRadius > 0 => hits every hostile
    // in range. Damage numbers below are the L16 (Ornate Wand) full-power anchor — Scale() below tunes them
    // down for the lower wand tiers. FX citations verified 2026-07-25 against ActorCompositeEffectDefinitions.xml.
    private static readonly WizardWeapon ShockKit = new(
        new("Zap", MeleeIcon, 2000, CastAnim, CastHitFx, 5492, CastEffectStopMs: 1200), // lightning ball bolt (PRJ_lightning_ball_light-blue_1)
        new("Lightning Blast", IcoLightningBlast, 8000, 1131, 16305, 5492, CastEffectStopMs: 1200)); // EffectId 16305 = PFX_electricity_fwd_circ_lg_wizard-lightning-blast — CONFIRMED dedicated match

    private static readonly WizardWeapon GlaciersKit = new(
        new("Chill", MeleeIcon, 2000, CastAnim, CastHitFx, 16110, CastEffectStopMs: 1200), // freezing bolt
        new("Ice Nova", IcoIceNova, 9000, 1132, 16172, AoeRadius: 10f)); // EffectId 16172 = PFX_ice_white_explosion_lg_wizard-ice-nova — CONFIRMED dedicated match

    private static readonly WizardWeapon FirestormKit = new(
        new("Burn", MeleeIcon, 2100, CastAnim, CastHitFx, 5479, CastEffectStopMs: 1200), // fireball bolt
        new("Firestorm", IcoFirestorm, 10000, 1133, 16026, AoeRadius: 10f)); // EffectId 16026 = PFX_wizard_firestorm_level-5 — CONFIRMED dedicated match

    private static readonly WizardWeapon TsunamiKit = new(
        new("Splash", MeleeIcon, 2000, CastAnim, CastHitFx, 15610, CastEffectStopMs: 1200), // water trail bolt
        new("Tsunami", IcoTsunami, 9500, 1134, 16187, AoeRadius: 10f)); // EffectId 16187 = PFX_sparkles_blue_root_wizard_tsunami — CONFIRMED dedicated match

    private static readonly WizardWeapon VortexKit = new(
        new("Blast", MeleeIcon, 2100, CastAnim, CastHitFx, 16188, CastEffectStopMs: 1200), // arcane sparkles bolt
        new("Energy Vortex", IcoEnergyVortex, 8500, 1135, 16151, AoeRadius: 8f)); // EffectId 16151 = PFX_sparkles-smoke_purple_cog_wizard-energy-vortex — CONFIRMED dedicated match

    private static readonly WizardWeapon LightningKit = new(
        new("Shock", MeleeIcon, 2100, CastAnim, CastHitFx, 5492, CastEffectStopMs: 1200), // lightning ball bolt
        // EffectId 16291 = PRJ_lightning_blue_trail_loop_wizard-chain-lightning — the ONLY dedicated Chain
        // Lightning composite in the client (a trail/loop, not a separate "landing" variant); used as the
        // impact FX for lack of a better dedicated match — CONFIRMED real, but not a true impact asset.
        new("Chain Lightning", IcoChainLightning, 9000, 1136, 16291, AoeRadius: 8f));

    private static readonly WizardWeapon ArcaneFireKit = new(
        new("Scorch", MeleeIcon, 2200, CastAnim, CastHitFx, 5479, CastEffectStopMs: 1200), // fireball bolt
        // EffectId 16041 = PFX_wizard_arcane-chain_p2p_level-5 (point-to-point chain beam = the impact/link on
        // the target) + CastEffectId 16036 = PFX_wizard_arcane-chain_cast_hands_level-5 (the cast FX in the
        // caster's hands) — BOTH CONFIRMED dedicated matches, correctly split target-vs-caster.
        new("Arcane Chain", IcoArcaneChain, 9500, 1137, 16041, CastEffectId: 16036, CastEffectStopMs: 1200));

    private static readonly WizardWeapon EnergyKit = new(
        new("Freeze", MeleeIcon, 2000, CastAnim, CastHitFx, 16110, CastEffectStopMs: 1200), // freezing bolt
        // FIXED 2026-07-25: EffectId was CastHitFx (7, the generic enemy PFX_Hit_Flash) — wrong for a
        // SELF-BUFF; nothing is being hit, so it's now 0 (no target-impact FX plays at all; see the "impact FX
        // on the victim" gate in AbilityPacketClientRequestStartAbilityHandler, which no-ops when effectId<=0).
        // CastEffectId 16124 = PFX_shield_purple_lg_loop_wizard-protective-barrier — CONFIRMED the real,
        // dedicated shield FX (already correct before this pass) — it plays on the CASTER via the lingering
        // cast-FX tag (CastEffectStopMs). NOTE (honest limitation, left as-is — out of this file's scope): the
        // shared ranged-ability handler still routes any AoeRadius<=0 Wizard special through the
        // caster->target PROJECTILE path, so this shield loop currently flies at the selected enemy as a bolt
        // trail instead of staying purely on the caster; a real fix needs a "self-target/no-projectile" flag in
        // AbilityPacketClientRequestStartAbilityHandler.cs, which is out of scope for this pass.
        new("Protective Barrier", IcoProtBarrier, 3000, 1138, 0, CastEffectId: 16124, CastEffectStopMs: 3000));

    private static readonly WizardWeapon ChaosKit = new(
        new("Boom", MeleeIcon, 2300, CastAnim, CastHitFx, 5479, CastEffectStopMs: 1200), // fireball bolt
        // EffectId 16126 = PFX_wizard_chaos_explosion_lg_launch + CastEffectId 16125 = PFX_wizard_chaos_explosion_lg_cast_hands
        // — both CONFIRMED real dedicated Chaos Explosion composites. No distinct "landing/impact" variant
        // ships (only lg_cast_hands/lg_launch/sm_launch exist); "lg_launch" is the best available target-facing
        // burst, reused as EffectId for lack of a dedicated impact asset.
        new("Chaos Explosion", IcoChaos, 12000, 1139, 16126, CastEffectId: 16125, AoeRadius: 10f, CastEffectStopMs: 1000));

    private static readonly WizardWeapon TransmuteKit = new(
        new("Flare", MeleeIcon, 2200, CastAnim, CastHitFx, 16188, CastEffectStopMs: 1200), // arcane sparkles bolt
        // FIXED 2026-07-25: EffectId was 16261, which ActorCompositeEffectDefinitions.xml actually names
        // PFX_sparkles_multi_wizard-arcane-flare — that's the ARCANE FLARE TRAIT's effect (a wrong-ability
        // mismatch, not Mass Transfigure). Replaced with 16170 = PFX_swirl-flash_red_root_transfiguration-land
        // — a genuine "transfiguration-land" composite (also 5332, an older/duplicate purple variant; 16170
        // picked as it sits in the same 16000+ id block as the other dedicated specials above).
        new("Mass Transfigure", IcoMassTransfig, 10000, 1140, 16170, AoeRadius: 10f));

    // ── DAMAGE SCALING BY WAND TIER (added 2026-07-25) ── Wizard had NO tier scaling at all before this: a
    // level-1 Sparkle Twig hit exactly as hard as a level-16 Ornate Wand. Wizard has no independent retail
    // damage source of its own (no wiki numbers per wand), so this MIRRORS ArcherWeaponAbilities' own
    // wiki-anchored tier curve — same shape/ratios, not new wizard-specific retail data. Archer's anchors
    // (ArcherWeaponAbilities.cs damage-per-tier comment): special dmg L1 640✓, L5 1554✓, L8 2100*, L12 2707✓,
    // L16 4750✓ (✓ = wiki-anchored, * = interpolated). Ratio to the L16 anchor: L1 0.135, L5 0.327, L8 0.442,
    // L12 0.570, L16 1.000. Applied here to each kit's existing numbers (already tuned as the L16/Ornate Wand
    // full-power anchor) to derive the lower tiers, same way Archer derives its 5 bow tiers from one curve.
    private const float TierL1 = 0.135f;
    private const float TierL5 = 0.327f;
    private const float TierL8 = 0.442f;
    private const float TierL12 = 0.570f;

    private static WizardWeapon Scale(WizardWeapon full, float tier) => new(
        full.Melee with { Damage = System.Math.Max(1, (int)(full.Melee.Damage * tier)) },
        full.Special with { Damage = System.Math.Max(1, (int)(full.Special.Damage * tier)) });

    // weapon def id -> kit. Real client Wizard wands (Sparkle Twig L1, Wand L5, Bone Wand L8, Jewel Wand L12,
    // Ornate Wand L16) + the coin-shop / player-studio novelty wands (themed to a fitting special by name).
    // Damage is tier-scaled via Scale() above; the Ornate Wand (L16) entries use the kit's raw (full-power) damage.
    private static readonly Dictionary<int, WizardWeapon> _byWeaponDefId = new()
    {
        // Sparkle Twig (L1)
        [75150] = Scale(ShockKit, TierL1), [75151] = Scale(GlaciersKit, TierL1),
        // Wand (L5)
        [75152] = Scale(ShockKit, TierL5), [75153] = Scale(GlaciersKit, TierL5), [75154] = Scale(FirestormKit, TierL5), [75155] = Scale(TsunamiKit, TierL5),
        // Bone Wand (L8)
        [75156] = Scale(ShockKit, TierL8), [75157] = Scale(GlaciersKit, TierL8), [75158] = Scale(FirestormKit, TierL8), [75159] = Scale(TsunamiKit, TierL8),
        [75160] = Scale(VortexKit, TierL8), [75161] = Scale(LightningKit, TierL8),
        // Jewel Wand (L12)
        [75162] = Scale(ShockKit, TierL12), [75163] = Scale(GlaciersKit, TierL12), [75164] = Scale(FirestormKit, TierL12), [75165] = Scale(TsunamiKit, TierL12),
        [75166] = Scale(VortexKit, TierL12), [75167] = Scale(LightningKit, TierL12), [75168] = Scale(ArcaneFireKit, TierL12), [75169] = Scale(EnergyKit, TierL12),
        // Ornate Wand (L16) — all 10 specials, full power (this tier IS the anchor — no scaling)
        [75170] = ShockKit, [75171] = GlaciersKit, [75172] = FirestormKit, [75173] = TsunamiKit,
        [75174] = VortexKit, [75175] = LightningKit, [75176] = ArcaneFireKit, [75177] = EnergyKit,
        [75178] = ChaosKit, [75179] = TransmuteKit,

        // ── Novelty / coin-shop / player-studio wands ── not part of the level-tier progression above, so left
        // at full power (same treatment ArcherWeaponAbilities gives its own epic Molten Bow), except the
        // Student Wand which is explicitly a starter item.
        [78201] = TransmuteKit,                    // Wizard's Nature Wand — nature transfiguration
        [4914] = Scale(ShockKit, TierL1), [30003] = Scale(ShockKit, TierL1), // Student Wizard Wand (crude) — starter shock, scaled as L1 (it IS the L1 default)
        // Orbital Wand — WebSearch (freerealms.fandom.com/wiki/Orbital_Wand, 2026-07-25) found this wand's REAL
        // documented pair is Starshower (basic, AoE) / Orbital Strike (special, AoE) with its own wiki damage
        // table (Orbital Strike: L1 889, L4 1554, L8 2716, L12 4750, L16 8302) — NOT Energy Vortex. Left as
        // VortexKit anyway: no `abil_wizard_starshower`/`orbital_strike` icon set or dedicated composite effect
        // exists anywhere in the client's ImageSets.txt / ActorCompositeEffectDefinitions.xml, so implementing
        // the real pair would mean inventing an icon/FX/anim id with no source — exactly what this pass is
        // supposed to avoid. Flagged honestly instead of silently "fixed".
        [13674] = VortexKit, [55339] = VortexKit,
        // Snowflake Wand / Forked Wand — WebSearch found no ability-pairing page for either (freerealms.fandom
        // wiki lists them only in category indexes, no dedicated item page content surfaced). Left as-is
        // (Ice Nova / Chain Lightning respectively) per the "leave as-is if unconfirmed" rule.
        [78718] = GlaciersKit,                     // Snowflake Wand — Ice Nova
    };

    // The Forked Wand ships as a big run of dye/tint variants (13673, 55332, 55779-55813); wire every one to the
    // forked Chain Lightning. Populate here so AllWeaponDefIds (snapshotted at the end) includes the range.
    static WizardWeaponAbilities()
    {
        _byWeaponDefId[13673] = LightningKit;
        _byWeaponDefId[55332] = LightningKit;
        for (var id = 55779; id <= 55813; id++)
            _byWeaponDefId[id] = LightningKit;

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, WizardWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

    // REAL ability name Global.Text ids — reversed from the client en_us_data. Fills the AbilitiesScreen
    // Attack/Special columns. Ability descriptions aren't mined yet (DescId 0 -> blank tooltip).
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
