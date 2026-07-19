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
// ANIM/FX are REAL (AnimationGroups.xml + ActorCompositeEffectDefinitions.xml):
//   basic cast = com_cast_01 (1111); specials = com_cast_special_01..10 (1131-1140); FX = the dedicated
//   PFX_*wizard-* composites (impact on target = EffectId, on caster = CastEffectId).
// ICONS/NAMES are REAL: abil_wizard_* Small (type-5) IMAGE_IDs + name/desc Global.Text ids reversed from the
// client en_us_data. DAMAGE are ours to tune (higher than melee jobs — ranged glass cannon).
public sealed record WizardWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class WizardWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int WizardProfileId = 12;

    // All wands cast the same way (AnimationGroups.xml): basic com_cast_01, specials com_cast_special_01..10.
    private const int CastAnim = 1111;         // com_cast_01
    private const int CastHitFx = 7;           // PFX_Hit_Flash — generic impact flash (basic).

    // REAL ability icons — abil_wizard_* Small (type-5) IMAGE_IDs. Basic-cast slot uses fireburst.
    private const int MeleeIcon = 280;         // abil_wizard_fireburst (basic cast)
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
    // in range. Protective Barrier's shield FX loops on the caster (CastEffectStopMs).
    private static readonly WizardWeapon ShockKit = new(
        new("Zap", MeleeIcon, 2000, CastAnim, CastHitFx),
        new("Lightning Blast", IcoLightningBlast, 8000, 1131, 16305)); // electricity burst

    private static readonly WizardWeapon GlaciersKit = new(
        new("Chill", MeleeIcon, 2000, CastAnim, CastHitFx),
        new("Ice Nova", IcoIceNova, 9000, 1132, 16172, AoeRadius: 10f)); // ice explosion

    private static readonly WizardWeapon FirestormKit = new(
        new("Burn", MeleeIcon, 2100, CastAnim, CastHitFx),
        new("Firestorm", IcoFirestorm, 10000, 1133, 16026, AoeRadius: 10f));

    private static readonly WizardWeapon TsunamiKit = new(
        new("Splash", MeleeIcon, 2000, CastAnim, CastHitFx),
        new("Tsunami", IcoTsunami, 9500, 1134, 16187, AoeRadius: 10f));

    private static readonly WizardWeapon VortexKit = new(
        new("Blast", MeleeIcon, 2100, CastAnim, CastHitFx),
        new("Energy Vortex", IcoEnergyVortex, 8500, 1135, 16151, AoeRadius: 8f));

    private static readonly WizardWeapon LightningKit = new(
        new("Shock", MeleeIcon, 2100, CastAnim, CastHitFx),
        new("Chain Lightning", IcoChainLightning, 9000, 1136, 16291, AoeRadius: 8f)); // lightning trail, chains

    private static readonly WizardWeapon ArcaneFireKit = new(
        new("Scorch", MeleeIcon, 2200, CastAnim, CastHitFx),
        new("Arcane Chain", IcoArcaneChain, 9500, 1137, 16041, CastEffectId: 16036, CastEffectStopMs: 1200));

    private static readonly WizardWeapon EnergyKit = new(
        new("Freeze", MeleeIcon, 2000, CastAnim, CastHitFx),
        new("Protective Barrier", IcoProtBarrier, 3000, 1138, CastHitFx, CastEffectId: 16124, CastEffectStopMs: 3000));

    private static readonly WizardWeapon ChaosKit = new(
        new("Boom", MeleeIcon, 2300, CastAnim, CastHitFx),
        new("Chaos Explosion", IcoChaos, 12000, 1139, 16126, CastEffectId: 16125, AoeRadius: 10f, CastEffectStopMs: 1000));

    private static readonly WizardWeapon TransmuteKit = new(
        new("Flare", MeleeIcon, 2200, CastAnim, CastHitFx),
        new("Mass Transfigure", IcoMassTransfig, 10000, 1140, 16261, AoeRadius: 10f));

    // weapon def id -> kit. Real client Wizard wands (Sparkle Twig L1, Wand L5, Bone Wand L8, Jewel Wand L12,
    // Ornate Wand L16) + the coin-shop / player-studio novelty wands (themed to a fitting special by name).
    private static readonly Dictionary<int, WizardWeapon> _byWeaponDefId = new()
    {
        // Sparkle Twig (L1)
        [75150] = ShockKit, [75151] = GlaciersKit,
        // Wand (L5)
        [75152] = ShockKit, [75153] = GlaciersKit, [75154] = FirestormKit, [75155] = TsunamiKit,
        // Bone Wand (L8)
        [75156] = ShockKit, [75157] = GlaciersKit, [75158] = FirestormKit, [75159] = TsunamiKit,
        [75160] = VortexKit, [75161] = LightningKit,
        // Jewel Wand (L12)
        [75162] = ShockKit, [75163] = GlaciersKit, [75164] = FirestormKit, [75165] = TsunamiKit,
        [75166] = VortexKit, [75167] = LightningKit, [75168] = ArcaneFireKit, [75169] = EnergyKit,
        // Ornate Wand (L16) — all 10 specials
        [75170] = ShockKit, [75171] = GlaciersKit, [75172] = FirestormKit, [75173] = TsunamiKit,
        [75174] = VortexKit, [75175] = LightningKit, [75176] = ArcaneFireKit, [75177] = EnergyKit,
        [75178] = ChaosKit, [75179] = TransmuteKit,

        // ── Novelty / coin-shop / player-studio wands ──
        [78201] = TransmuteKit,                    // Wizard's Nature Wand — nature transfiguration
        [4914] = ShockKit, [30003] = ShockKit,     // Student Wizard Wand (crude) — starter shock (also the L1 default)
        [13674] = VortexKit, [55339] = VortexKit,  // Orbital Wand (diadem) — orbiting Energy Vortex
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
