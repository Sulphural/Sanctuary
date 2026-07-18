using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// WARRIOR (profile 32) — clubs, axes and hammers, heavy melee. Weapon-driven like the ninja/brawler kits: the
// equipped "Warrior's <Weapon> of <Special>" (or the Nature Claw) grants a MELEE (slot 0) + a named SPECIAL
// (slot 1). Abilities/weapons from the Free Realms wiki (allakhazam ZAM item pages).
//
// ANIM/FX are REAL (AnimationGroups.xml + ActorCompositeEffectDefinitions.xml):
//   melee swing picked per weapon — 1h (Cudgel/Axe) com_1hs_attack(1020), 2h (hammers/axes) com_2hp_attack(1080),
//   fist (Nature Claw) com_h2h_attack(1000); specials = com_2hp_special_01..08 (1091-1098) + com_2hs_special
//   (1051-1052); FX = the dedicated PFX_*warrior-* composites (impact on target = EffectId, on caster = CastEffectId).
// ICONS/NAMES are REAL: abil_warrior_* Small (type-5) IMAGE_IDs + name/desc Global.Text ids reversed from the
// client en_us_data (Jenkins lookup2 of "Global.Text.<id>"). DAMAGE are ours to tune.
public sealed record WarriorWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class WarriorWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int WarriorProfileId = 32;

    // Melee swing anims (AnimationGroups.xml). Picked per equipped weapon model.
    private const int FistMeleeAnim = 1000;    // com_h2h_attack (Nature Claw)
    private const int OneHandMeleeAnim = 1020; // com_1hs_attack (Cudgel, Axe)
    private const int TwoHandMeleeAnim = 1080; // com_2hp_attack (Battle Hammer, Double Axe, Warlord Axe)
    private const int MeleeHitFx = 7;          // PFX_Hit_Flash — generic impact flash.

    // REAL ability icons — abil_warrior_* Small (type-5) IMAGE_IDs. Basic-attack slot uses crushing_blows (a
    // heavy strike) since there's no dedicated "attack" icon.
    private const int MeleeIcon = 11657;       // abil_warrior_crushing_blows
    private const int IcoSpinning = 23007, IcoCleave = 23001, IcoQuake = 11663, IcoWarcry = 23013,
        IcoWhirlwind = 23016, IcoAxeThrow = 22995, IcoBerserk = 22998, IcoFrenzy = 23004,
        IcoCommand = 11654, IcoThunderclap = 23010;

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

    // ── SPECIALS (10) ── melee (slot 0) + the named special (slot 1). AoeRadius > 0 => hits every hostile in
    // range of the caster. Looping cast FX (Warcry/Berserk/Frenzy/Command shouts) use CastEffectStopMs.
    private static readonly WarriorWeapon SpinningKit = new(
        new("Sweeping Slash", MeleeIcon, 2500, TwoHandMeleeAnim, MeleeHitFx),
        new("Spinning Attack", IcoSpinning, 7500, 1091, 4009, CastEffectId: 4001, AoeRadius: 8f, CastEffectStopMs: 1200));

    private static readonly WarriorWeapon CleaveKit = new(
        new("Fierce Edge", MeleeIcon, 2500, TwoHandMeleeAnim, MeleeHitFx),
        new("Cleave", IcoCleave, 8000, 1092, 16226, CastEffectId: 16202, AoeRadius: 10f, CastEffectStopMs: 1000));

    private static readonly WarriorWeapon QuakeKit = new(
        new("Power Slash", MeleeIcon, 2600, TwoHandMeleeAnim, MeleeHitFx),
        new("Quake", IcoQuake, 8500, 1093, 16072, AoeRadius: 10f)); // rock-column on target

    private static readonly WarriorWeapon WarcryKit = new(
        new("Dual Strike", MeleeIcon, 2400, TwoHandMeleeAnim, MeleeHitFx),
        new("Warcry", IcoWarcry, 4000, 1094, MeleeHitFx, CastEffectId: 16199, CastEffectStopMs: 2000)); // shout loop

    private static readonly WarriorWeapon WhirlwindKit = new(
        new("Gale Axe", MeleeIcon, 2700, TwoHandMeleeAnim, MeleeHitFx),
        new("Whirlwind", IcoWhirlwind, 9000, 1095, 16107, CastEffectId: 16105, AoeRadius: 10f, CastEffectStopMs: 1200));

    private static readonly WarriorWeapon HurlingKit = new(
        new("Reckless Strike", MeleeIcon, 2600, TwoHandMeleeAnim, MeleeHitFx),
        new("Axe Throw", IcoAxeThrow, 9500, 1096, 5316)); // thrown-axe impact

    private static readonly WarriorWeapon BerserkKit = new(
        new("Hack 'n' Slash", MeleeIcon, 2800, TwoHandMeleeAnim, MeleeHitFx),
        new("Berserk", IcoBerserk, 5000, 1097, MeleeHitFx, CastEffectId: 16232, CastEffectStopMs: 2500)); // red-blades aura

    private static readonly WarriorWeapon FrenzyKit = new(
        new("Crushing Blow", MeleeIcon, 2700, TwoHandMeleeAnim, MeleeHitFx),
        new("Frenzy", IcoFrenzy, 8500, 1098, MeleeHitFx, CastEffectId: 16245, CastEffectStopMs: 2000)); // frenzy sparkles

    private static readonly WarriorWeapon CommandKit = new(
        new("Dizzying Blow", MeleeIcon, 2600, TwoHandMeleeAnim, MeleeHitFx),
        new("Commanding Shout", IcoCommand, 4000, 1051, MeleeHitFx, CastEffectId: 15233, AoeRadius: 12f, CastEffectStopMs: 2500));

    private static readonly WarriorWeapon ThunderKit = new(
        new("Rampage", MeleeIcon, 2900, TwoHandMeleeAnim, MeleeHitFx),
        new("Thunderclap", IcoThunderclap, 10500, 1052, 16122, CastEffectId: 16280, AoeRadius: 10f)); // lightning p2p

    // weapon def id -> kit. Real client Warrior weapons (Cudgel L1, Axe L5, Battle Hammer L8, Double Axe L12,
    // Warlord Axe L16) + the coin-shop / player-studio novelty weapons (themed to a fitting special by name).
    private static readonly Dictionary<int, WarriorWeapon> _byWeaponDefId = new()
    {
        // Cudgel (L1)
        [75120] = SpinningKit, [75121] = CleaveKit,
        // Axe (L5)
        [75122] = SpinningKit, [75123] = CleaveKit, [75124] = QuakeKit, [75125] = WarcryKit,
        // Battle Hammer (L8)
        [75126] = SpinningKit, [75127] = CleaveKit, [75128] = QuakeKit, [75129] = WarcryKit,
        [75130] = WhirlwindKit, [75131] = HurlingKit,
        // Double Axe (L12)
        [75132] = SpinningKit, [75133] = CleaveKit, [75134] = QuakeKit, [75135] = WarcryKit,
        [75136] = WhirlwindKit, [75137] = HurlingKit, [75138] = BerserkKit, [75139] = FrenzyKit,
        // Warlord Axe (L16) — all 10 specials
        [75140] = SpinningKit, [75141] = CleaveKit, [75142] = QuakeKit, [75143] = WarcryKit,
        [75144] = WhirlwindKit, [75145] = HurlingKit, [75146] = BerserkKit, [75147] = FrenzyKit,
        [75148] = CommandKit, [75149] = ThunderKit,

        // ── Novelty / coin-shop / player-studio weapons ──
        [78200] = BerserkKit,                     // Warrior's Nature Claw (fist) — berserker fury
        [7012] = SpinningKit, [29963] = SpinningKit,          // Student Warrior Cudgel (club) — starter spin
        [13671] = QuakeKit, [55363] = QuakeKit,              // Exploding Axe — ground-shattering Quake
        [78711] = CleaveKit,                      // Ice Axe — cleaving axe
        [79021] = CommandKit,                     // The Kingmaker (sword) — Commanding Shout
        [78714] = ThunderKit,                     // Lightning Blade (fist) — Thunderclap
        // Angro's Vanquisher (warlordsaxe) — berserker greataxe
        [9027] = BerserkKit, [13670] = BerserkKit, [30564] = BerserkKit, [38540] = BerserkKit,
    };

    // The Twin Crescent Axe ships as a big run of dye/tint variants (13672, 55333, 55430-55464); wire every one
    // to the spinning Whirlwind so whichever variant a player owns behaves the same. Populate the range here (so
    // AllWeaponDefIds, snapshotted at the end, includes it — field initializers run before this ctor body).
    static WarriorWeaponAbilities()
    {
        _byWeaponDefId[13672] = WhirlwindKit;
        _byWeaponDefId[55333] = WhirlwindKit;
        for (var id = 55430; id <= 55464; id++)
            _byWeaponDefId[id] = WhirlwindKit;

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, WarriorWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

    // REAL ability name Global.Text ids — reversed from the client en_us_data. Fills the AbilitiesScreen
    // Attack/Special columns. Ability descriptions aren't mined yet (DescId 0 -> blank tooltip).
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
