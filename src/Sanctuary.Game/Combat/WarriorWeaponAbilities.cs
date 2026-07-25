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
// ANIM are REAL (AnimationGroups.xml): melee swing picked per weapon — 1h (Cudgel/Axe) com_1hs_attack(1020),
//   2h (hammers/axes) com_2hp_attack(1080), fist (Nature Claw) com_h2h_attack(1000); specials = com_2hp_special_01..08
//   (1091-1098) + com_2hs_special (1051-1052).
// FX: every EffectId/CastEffectId below was looked up individually in the client's
//   ActorCompositeEffectDefinitions.xml (id -> <EffectDefinition name="..."> match) — see the per-kit citation
//   comments. Most resolve to a dedicated PFX_*_warrior-<special>* composite (impact on target = EffectId, on
//   caster/loop = CastEffectId); a few (Whirlwind, Axe Throw pre-fix) only resolved to a same-family or
//   flatly-mismatched composite — flagged where that's the case. DAMAGE: 5 of the 10 specials are now anchored to
//   the freerealms.fandom.com weapon-item pages (see per-kit comments); the rest are still ours to tune (no
//   number found).
// ICONS/NAMES are REAL: abil_warrior_* Small (type-5) IMAGE_IDs + name/desc Global.Text ids reversed from the
// client en_us_data (Jenkins lookup2 of "Global.Text.<id>").
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
    //
    // FX citations below are id -> <EffectDefinition name="..."> in the client's ActorCompositeEffectDefinitions.xml.
    // Damage citations are the special's listed damage on the matching weapon's freerealms.fandom.com item page
    // (wiki numbers are per-weapon-tier, e.g. a level-12 Double Axe; our single shared value is used across every
    // tier that carries the kit, same simplification the file already had for every other number).
    private static readonly WarriorWeapon SpinningKit = new(
        // impact=4009 "PFX_Spinning_Blades_Land", cast=4001 "PFX_Spinning_Blades" — both real, exact name match.
        // Damage 2707: freerealms.fandom.com "Warrior's Double Axe of Spinning" (L12) Spinning Attack tooltip.
        new("Sweeping Slash", MeleeIcon, 2500, TwoHandMeleeAnim, MeleeHitFx),
        new("Spinning Attack", IcoSpinning, 2707, 1091, 4009, CastEffectId: 4001, AoeRadius: 8f, CastEffectStopMs: 1200));

    private static readonly WarriorWeapon CleaveKit = new(
        // impact=16226 "PFX_sparkles_blue_warrior-cleave", cast=16202 "WFX_beam-trail_blue_warrior-cleave" —
        // both real, exact name match (literally named "warrior-cleave"). Damage: no wiki number found, untouched.
        new("Fierce Edge", MeleeIcon, 2500, TwoHandMeleeAnim, MeleeHitFx),
        new("Cleave", IcoCleave, 8000, 1092, 16226, CastEffectId: 16202, AoeRadius: 10f, CastEffectStopMs: 1000));

    private static readonly WarriorWeapon QuakeKit = new(
        // impact=16072 "PFX_rock-column_brown_warrior-quake" — real, exact name match. Damage: no wiki number
        // found (no "Battle Hammer/Double Axe of Quake" page turned up), untouched.
        new("Power Slash", MeleeIcon, 2600, TwoHandMeleeAnim, MeleeHitFx),
        new("Quake", IcoQuake, 8500, 1093, 16072, AoeRadius: 10f));

    private static readonly WarriorWeapon WarcryKit = new(
        // cast=16199 "PFX_sound_blue_head_warrior-warcry_loop" — real, exact name match (the caster-side shout
        // loop). EffectId was MeleeHitFx (generic PFX_Hit_Flash) despite AoeRadius==0 — Warcry only ever hits the
        // single locked target (if any), so it behaves like a self/target buff-cry, not an AoE strike; a bare
        // melee "hit flash" doesn't fit that. Set to 0 (no target impact FX) and let the real warcry_loop aura
        // (already on CastEffectId) carry the ability's look, same as Berserk/Frenzy below.
        // Damage 3420: freerealms.fandom.com "Warrior's Double Axe of Warcry" (L12) Warcry tooltip.
        new("Dual Strike", MeleeIcon, 2400, TwoHandMeleeAnim, MeleeHitFx),
        new("Warcry", IcoWarcry, 3420, 1094, 0, CastEffectId: 16199, CastEffectStopMs: 2000));

    private static readonly WarriorWeapon WhirlwindKit = new(
        // impact=16107 "WFX_wind_white_warrior-air-attack_aoe", cast=16105 "WFX_beam_white_warrior-air-attack" —
        // both real ids, but the composite is literally named "warrior-air-attack", not "whirlwind"; no
        // dedicated warrior-whirlwind composite exists in this file, so this is the closest thematic match
        // (spin/air AOE), not a confirmed exact pairing — flagging per audit rather than reclaiming it as REAL.
        // Damage 2716: freerealms.fandom.com "Warrior's Battle Hammer of Whirlwind" (L8) Whirlwind tooltip (wiki
        // also lists a knockdown, which we don't implement — out of scope, FX/damage only).
        new("Gale Axe", MeleeIcon, 2700, TwoHandMeleeAnim, MeleeHitFx),
        new("Whirlwind", IcoWhirlwind, 2716, 1095, 16107, CastEffectId: 16105, AoeRadius: 10f, CastEffectStopMs: 1200));

    private static readonly WarriorWeapon HurlingKit = new(
        // EffectId was 5316 "PFX_hit-flash-rings_red_toe-r_warrior-kick" — that id IS real but it's a KICK
        // impact flash, unrelated to a thrown axe; flatly mismatched, not just "unfitting". Replaced with
        // 15490 "PRJ_battleaxe_sparkles" (impact) + CastEffectId 16177 "PRJ_flaming_orange_battleaxe_trail"
        // (flight trail) — both real PRJ_battleaxe_* composites, the closest match this file has to an actual
        // thrown-axe FX pair. (Wiki note: the real client ability is named "Hurling", not "Axe Throw" — see
        // freerealms.fandom.com "Warrior's Battle Hammer/Double Axe/Warlord Axe of Hurling"; not renamed here,
        // out of scope, and no damage number was found on those pages.)
        new("Reckless Strike", MeleeIcon, 2600, TwoHandMeleeAnim, MeleeHitFx),
        new("Axe Throw", IcoAxeThrow, 9500, 1096, 15490, CastEffectId: 16177));

    private static readonly WarriorWeapon BerserkKit = new(
        // cast=16232 "PFX_warrior_berserk_red_blades" — real, exact name match (literally "warrior_berserk").
        // EffectId was MeleeHitFx despite AoeRadius==0 (single-target-only, same reasoning as Warcry above) —
        // set to 0, the real red-blades aura on CastEffectId already carries the look.
        // Damage: no wiki number found for a "...of Berserking" weapon page, untouched.
        new("Hack 'n' Slash", MeleeIcon, 2800, TwoHandMeleeAnim, MeleeHitFx),
        new("Berserk", IcoBerserk, 5000, 1097, 0, CastEffectId: 16232, CastEffectStopMs: 2500));

    private static readonly WarriorWeapon FrenzyKit = new(
        // cast=16245 "PFX_sparkles_added_warrior-frenzy" — real, exact name match. EffectId was MeleeHitFx
        // despite AoeRadius==0 (same reasoning as Warcry/Berserk) — set to 0, real frenzy-sparkles aura carries it.
        // Damage 1343: freerealms.fandom.com "Warrior's Double Axe of Frenzy" (L12) Frenzy tooltip.
        new("Crushing Blow", MeleeIcon, 2700, TwoHandMeleeAnim, MeleeHitFx),
        new("Frenzy", IcoFrenzy, 1343, 1098, 0, CastEffectId: 16245, CastEffectStopMs: 2000));

    private static readonly WarriorWeapon CommandKit = new(
        // cast=15233 "PFX_moire-circles_multi_head_commanding-shout-level-5_loop" — real, exact match; it's the
        // level-5 (max) tier of a leveled commanding-shout-level-1..5 effect chain in the client file.
        // Unlike Warcry/Berserk/Frenzy, Commanding Shout DOES have AoeRadius=12f (hits every nearby hostile), so
        // per the audit it keeps a target-facing impact — but MeleeHitFx (generic PFX_Hit_Flash) still doesn't
        // fit a shout. Replaced with 4004 "PFX_waves_red_head_shout" — a real, one-shot (non-looping) "shout
        // wave" composite that plays on the target's head, a much closer thematic match for a shout knocking
        // into nearby enemies. Damage: freerealms.fandom.com "Warrior's Warlord Axe of Command" (L16) describes
        // Commanding Shout as a taunt ("challenges all nearby opponents to attack you") + self-buff (attack
        // power, invincibility) with NO listed damage number — the real ability may not deal damage at all.
        // We have no aggro/taunt system to redirect enemies onto the caster (same gap noted for the Instigation
        // trait above), so the existing placeholder Damage is left as-is rather than zeroed, since that would be
        // a mechanic change beyond FX/damage sourcing.
        new("Dizzying Blow", MeleeIcon, 2600, TwoHandMeleeAnim, MeleeHitFx),
        new("Commanding Shout", IcoCommand, 4000, 1051, 4004, CastEffectId: 15233, AoeRadius: 12f, CastEffectStopMs: 2500));

    private static readonly WarriorWeapon ThunderKit = new(
        // impact=16122 "PFX_lightning_blue_root_warrior_thunderclap-p2p", cast=16280
        // "PFX_lightning_blue_root_warrior_thunderclap" — both real, exact name match ("warrior_thunderclap").
        // Damage 5977: freerealms.fandom.com "Warrior's Warlord Axe of Thunder" (L16) Thunderclap tooltip (wiki
        // also lists an immobilize, which we don't implement — out of scope, FX/damage only).
        new("Rampage", MeleeIcon, 2900, TwoHandMeleeAnim, MeleeHitFx),
        new("Thunderclap", IcoThunderclap, 5977, 1052, 16122, CastEffectId: 16280, AoeRadius: 10f));

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
        [78200] = BerserkKit,                     // Warrior's Nature Claw (fist) — berserker fury (unconfirmed guess)
        [7012] = SpinningKit, [29963] = SpinningKit,          // Student Warrior Cudgel (club) — starter spin
                                                               // (no freerealms.fandom.com page found; unconfirmed)
        // Exploding Axe — checked freerealms.fandom.com/wiki/Exploding_Axe: its REAL specials are "Twilight
        // Strike" (melee) and "Vampiric Wrath" (AOE + lifesteal heal), neither of which is Quake or any other
        // kit this file catalogs (no lifesteal mechanic exists here). Kept as QuakeKit (closest we have — AOE
        // ground-type special) since remapping to a wholly new "Vampiric Wrath" ability is out of scope.
        [13671] = QuakeKit, [55363] = QuakeKit,
        [78711] = CleaveKit,                      // Ice Axe — cleaving axe (no matching wiki page found under
                                                   // "Ice Axe"; a "Glacial Axe" exists but isn't confirmed to be
                                                   // the same item — unconfirmed guess, left as-is)
        [79021] = CommandKit,                     // The Kingmaker (sword) — Commanding Shout (no FR-specific wiki
                                                   // page found; searches only returned Pathfinder: Kingmaker —
                                                   // unconfirmed guess, left as-is)
        [78714] = ThunderKit,                     // Lightning Blade (fist) — Thunderclap (no matching wiki page
                                                   // found; unconfirmed guess, left as-is)
        // Angro's Vanquisher (warlordsaxe) — checked freerealms.fandom.com/wiki/Angro's_Vanquisher (+ ZAM
        // fr_item:Angro's_Vanquisher): its REAL specials are "Angro Chop" (melee) and "Vanquish" ("Vanquish all
        // nearby opponents with Angro's secret technique", an AOE nuke), neither of which is literally "Berserk".
        // Kept as BerserkKit (closest we have — an AOE/rage-flavored special) since adding a dedicated "Vanquish"
        // kit is out of scope for this pass.
        [9027] = BerserkKit, [13670] = BerserkKit, [30564] = BerserkKit, [38540] = BerserkKit,
    };

    // The Twin Crescent Axe ships as a big run of dye/tint variants (13672, 55333, 55430-55464); wire every one
    // to the spinning Whirlwind so whichever variant a player owns behaves the same. Populate the range here (so
    // AllWeaponDefIds, snapshotted at the end, includes it — field initializers run before this ctor body).
    // Checked freerealms.fandom.com/wiki/Twin_Crescent_Axe: its REAL specials are "Ember Strike" (melee) and
    // "Ignite" (a 3-hit burning AOE), neither of which is "Whirlwind" and no fire/burn-DoT kit exists in this
    // file to remap it to — kept as WhirlwindKit (closest available AOE special), unconfirmed exact pairing.
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
