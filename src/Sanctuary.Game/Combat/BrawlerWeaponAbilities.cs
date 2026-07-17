using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// BRAWLER (profile 43) — hammers + power fists, blunt melee. Weapon-driven like the ninja kit: the equipped
// "Brawler's <Hammer> of <Special>" weapon (or a novelty hammer/fist) grants a MELEE (slot 0) + a named
// SPECIAL (slot 1). Abilities/weapons from the Free Realms wiki (allakhazam ZAM + fandom).
//
// ★ NO item-def injection: the Brawler weapons are REAL client coin-shop items; seeding their shared item-def
// Abilities lists broke the client for everyone. So BrawlerJobKit.WeaponDefIds is EMPTY — this kit only drives
// the equipped-weapon TOOLBAR + traits + combat for a Brawler player, never the shared item defs.
//
// ANIM/FX are REAL (extracted from AnimationGroups.xml + ActorCompositeEffectDefinitions.xml):
//   melee swing = com_h2h_attack(1000) for fists / com_2hp_attack(1080) for 2h hammers (picked per weapon);
//   specials = com_2hp_special_01..08 (1091-1098); FX = the dedicated PFX_*brawler-* composite effects.
//   EffectId = impact FX on the TARGET; CastEffectId = FX on the CASTER (star-rings/aura/dirt).
// ICONS are REAL too: the abil_brawler_* Small IMAGE_IDs from the client Resources/Images (ImageSets.txt ->
// ImageSetMappings.txt type 5). TODO left: DAMAGE are estimates; trait NAME/DESC locale ids want reversing.
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
    // Resources/Images (ImageSets.txt set id -> ImageSetMappings.txt type 5). The basic-attack slot uses
    // bum_rush (a charging punch) since there's no dedicated "attack" icon.
    private const int MeleeIcon = 22581;       // abil_brawler_bum_rush
    private const int IcoPummel = 22926, IcoLegSweep = 11636, IcoRoundhouse = 22932, IcoRumble = 22929,
        IcoSuckerPunch = 22938, IcoKickDirt = 22920, IcoKnockout = 22923, IcoHammerToss = 22917,
        IcoEnrage = 11633, IcoSlam = 22935, IcoPowerRain = 3373; // spinattack

    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    // Fist-model weapons swing h2h; everything else (hammers/clubs/axes) swings 2hp.
    private static readonly HashSet<int> FistWeaponDefIds = new() { 13659, 55335, 78197, 78712, 78713 };
    private static int MeleeAnimFor(int weaponDefId) => FistWeaponDefIds.Contains(weaponDefId) ? FistMeleeAnim : HammerMeleeAnim;

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

    public static List<AbilityExperience> BuildTraitEntries(int rank) => JobTraits.Build(TraitData, rank);

    public static bool HasTrait(Player player, int traitLevel) =>
        player.ActiveProfileId == BrawlerProfileId && player.ActiveProfile.Rank >= traitLevel;

    // ── SPECIALS (10) ── melee (slot 0) + the named special (slot 1). Special anim = a com_2hp_special clip;
    // FX are the dedicated brawler composites. AoeRadius > 0 => hits every hostile within range of the caster.
    private static readonly BrawlerWeapon PummelKit = new(
        new("Pound",  MeleeIcon, 2600, HammerMeleeAnim, MeleeHitFx),
        new("Pummel", IcoPummel, 9000, 1091, 5252, CastEffectId: 5257)); // star-rings on hands + pummel land

    private static readonly BrawlerWeapon LegSweepKit = new(
        new("Glancing Blow", MeleeIcon, 2400, HammerMeleeAnim, MeleeHitFx),
        new("Leg Sweep",     IcoLegSweep, 7500, 1092, 5315, AoeRadius: 10f)); // kick-land FX, AoE

    private static readonly BrawlerWeapon RoundhouseKit = new(
        new("Smack",           MeleeIcon, 2500, HammerMeleeAnim, MeleeHitFx),
        new("Roundhouse Kick", IcoRoundhouse, 8000, 1093, 5315, AoeRadius: 10f)); // kick-land FX, AoE

    private static readonly BrawlerWeapon RumbleKit = new(
        new("Wallop",          MeleeIcon, 2700, HammerMeleeAnim, MeleeHitFx),
        new("Ready to Rumble", IcoRumble, 8500, 1094, MeleeHitFx, CastEffectId: 16212)); // fire-rocks on caster

    private static readonly BrawlerWeapon SuckerPunchKit = new(
        new("Wild Swing",   MeleeIcon, 2400, HammerMeleeAnim, MeleeHitFx),
        new("Sucker Punch", IcoSuckerPunch, 9500, 1095, MeleeHitFx, CastEffectId: 16198)); // spiral beam

    private static readonly BrawlerWeapon KickDirtKit = new(
        new("Whack",     MeleeIcon, 2300, HammerMeleeAnim, MeleeHitFx),
        new("Kick Dirt", IcoKickDirt, 7000, 1096, MeleeHitFx, CastEffectId: 16206)); // dirt cloud

    private static readonly BrawlerWeapon KnockoutKit = new(
        new("Thump",    MeleeIcon, 2900, HammerMeleeAnim, MeleeHitFx),
        new("Knockout", IcoKnockout, 11000, 1097, 16200)); // knockout orange on target

    private static readonly BrawlerWeapon HammerTossKit = new(
        new("Crush",       MeleeIcon, 2600, HammerMeleeAnim, MeleeHitFx),
        // 16289 is a LOOPING trail (PFX_..._loop_...) — play it via the effect-tag path and remove after the
        // throw window so it doesn't linger forever; 15203 earthquake lands on the target.
        new("Hammer Toss", IcoHammerToss, 8500, 1098, 15203, CastEffectId: 16289, CastEffectStopMs: 1500));

    private static readonly BrawlerWeapon EnrageKit = new(
        new("Bash",   MeleeIcon, 2700, HammerMeleeAnim, MeleeHitFx),
        new("Enrage", IcoEnrage, 8000, 1091, MeleeHitFx, CastEffectId: 16145)); // enrage cast aura

    private static readonly BrawlerWeapon SlamKit = new(
        new("Smash", MeleeIcon, 2800, HammerMeleeAnim, MeleeHitFx),
        new("Slam",  IcoSlam, 10500, 1092, 5252)); // heavy impact

    private static readonly BrawlerWeapon PowerFistKit = new(
        new("Power Smash", MeleeIcon, 1800, FistMeleeAnim, MeleeHitFx),
        new("Power Rain",  IcoPowerRain, 6000, 1091, MeleeHitFx, CastEffectId: 16084, AoeRadius: 8f));

    // weapon def id -> kit. Real client Brawler weapons.
    //   Tiered "of X" set: Sweeps=Leg Sweep · Rumbling=Ready to Rumble · Pummeling=Pummel · Roundhouse=Roundhouse
    //   Kick · Cheapshot=Sucker Punch · Dirt Kick=Kick Dirt · Slammage=Slam · Chucking=Hammer Toss · Rage=Enrage ·
    //   Stars=Knockout. Novelty hammers/fists themed to a fitting special.
    public static readonly IReadOnlyDictionary<int, BrawlerWeapon> ByWeaponDefId = new Dictionary<int, BrawlerWeapon>
    {
        // Mallet (L1)
        [75030] = LegSweepKit, [75031] = RumbleKit,
        // Hammer (L5)
        [75032] = LegSweepKit, [75033] = RumbleKit, [75034] = PummelKit, [75035] = RoundhouseKit,
        // Anvil Hammer (L8)
        [75036] = LegSweepKit, [75037] = RumbleKit, [75038] = PummelKit, [75039] = RoundhouseKit,
        [75040] = SuckerPunchKit, [75041] = KickDirtKit,
        // Drill Hammer (L12)
        [75042] = LegSweepKit, [75043] = RumbleKit, [75044] = PummelKit, [75045] = RoundhouseKit,
        [75046] = SuckerPunchKit, [75047] = KickDirtKit, [75048] = SlamKit, [75049] = HammerTossKit,
        // Atlas Hammer (L16) — all 10 specials
        [75050] = LegSweepKit, [75051] = RumbleKit, [75052] = PummelKit, [75053] = RoundhouseKit,
        [75054] = SuckerPunchKit, [75055] = KickDirtKit, [75056] = SlamKit, [75057] = HammerTossKit,
        [75058] = EnrageKit, [75059] = KnockoutKit,

        // ── Novelty / coin-shop Brawler weapons ──
        [78197] = PowerFistKit,                                   // Brawler's Power Fist (fist_bikerfist)
        [78717] = KnockoutKit,                                    // Bellringer (ring their bell -> see stars)
        [78712] = SuckerPunchKit,                                 // Gleam Energy Fist
        [78713] = PummelKit,                                      // Gloam Energy Fist
        [79020] = HammerTossKit,                                  // Hole-In-One Golf Driver (drive it far)
        [9034] = RumbleKit, [13657] = RumbleKit, [55364] = RumbleKit,   // Exploding Hammer
        [13658] = SlamKit, [55365] = SlamKit,                    // Golden Hammer
        [13659] = PummelKit, [55335] = PummelKit,                // Torque Trasher (fist)
    };

    public static readonly int[] AllWeaponDefIds = ByWeaponDefId.Keys.ToArray();

    // REAL ability name Global.Text ids — reversed from the client en_us_data (Jenkins lookup2 of
    // "Global.Text.<id>"). Fills the AbilitiesScreen Attack/Special columns. Ability descriptions aren't
    // mined yet (DescId 0 -> blank tooltip).
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

        return slot <= 0 ? weapon.Melee with { Animation = MeleeAnimFor(defId) } : weapon.Special;
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
