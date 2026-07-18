using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// COMBAT WIP: abilities are driven by the EQUIPPED WEAPON, the way Free Realms did it. Each ninja
// "Ninja's Shadow Blade of X" weapon (Resources/ClientItemDefinitions.json, ids 75110-75119) grants TWO
// abilities: Ability 1 = a melee sword technique (slot 0), Ability 2 = the named special (slot 1).
// Names + damage are from the Free Realms wiki (freerealms.fandom.com api.php).
//
// ICONS (cracked 2026-06-20): the ability-slot IconId is a flat IMAGE_ID, NOT an image-set id. The real
// ninja ability icons are the abil_ninja_* image sets' IMAGE_IDs (Client/Resources/Images/ImageSetMappings.txt,
// Small=type5). e.g. abil_ninja_shuriken_storm set 4902 -> Small IMAGE_ID 22986. (Sending the set id 4902 hit
// the food/fruit image #4902 instead.) FX ids match ActorCompositeEffectDefinitions.xml (confirmed live:
// id 1 = fire). Animations: 1099 com_swing is proven; per-ability anims TBD via !anim probe.
// EffectId = impact FX played on the TARGET (AttackProcessed). CastEffectId = FX played on the CASTER during
// StartCasting (the projectile/aura/ground-AoE you see come off the ninja); 0 = none. For projectile specials
// (Shuriken throw, Dragonstrike) CastEffectId = the launch/trail and EffectId = the land/impact; for ground-AoE
// specials the same id works for both. (Usage per drafts/ninja-special-anim-fx-research.md §FINDINGS iter 4.)
// SummonCount > 0 => this special also spawns that many temporary "shadow clone" NPCs around the caster
// (Shadow Army). See StartingZone.SummonShadowClones.
//
// SwordEffectId > 0 => bind this composite effect to the caster's WEAPON slot (item slot 7) via
//   PlayerUpdatePacketSlotCompositeEffectOverride on cast — i.e. the FX rides on the SWORD, not the body.
//   Used by abilities that "empower the weapon" (Mysticism / Mystical Blade).
// CasterEndEffectId > 0 => play this composite effect ON THE CASTER (at the player's position/feet) at the
//   END of the animation (after the cast delay) via PlayerUpdatePacketPlayCompositeEffect — for abilities
//   whose FX should land on the caster's feet when the move finishes (Dragonstrike).
// EnemyExtraEffectId > 0 => play this ADDITIONAL composite effect on the TARGET (on top of EffectId) at the
//   end of the cast — e.g. a ring overlaid on the enemy (Soul Power's purple ring). 0 = none.
// AoeRadius > 0 => the special is an AREA attack: it hits EVERY live hostile within this radius of the
//   CASTER (not just the selected target). Matches the real server, whose AoE specials land as a sub-0.1s
//   burst of one HitPointModification per victim in the 04-01 capture.
// CastEffectStopMs > 0 => the CastEffectId is a LINGERING/looping effect (e.g. a PRJ_* projectile
//   trail, which retail kills with its projectile — we have no projectiles yet): play it via an
//   effect TAG (op35/sub41) and remove it (op35/sub42) after this many ms. Such effects are also
//   excluded from the toolbar FX warm-up (an unstoppable loop under the map snows forever —
//   user-sighted with the Bow of Blizzards trail, 2026-07-10). 0 = a one-shot, plays normally.
// EnergyCost = what a NON-BASIC slot press drains from the 100-point energy bar (also the slot's
//   ManaCost, which drives the client grey-out). Weapon specials keep the live-decoded full bar
//   (100); extra job abilities can cost less so the bar supports more than one cast.
public sealed record WeaponAbility(string Name, int IconImageId, int Damage, int Animation, int EffectId, int CastEffectId = 0, int SummonCount = 0, int SwordEffectId = 0, int CasterEndEffectId = 0, int EnemyExtraEffectId = 0, float AoeRadius = 0f, int CastEffectStopMs = 0, int EnergyCost = 100);

public sealed record NinjaWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class NinjaWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int NinjaProfileId = 2;

    private const int MeleeAnimation = 1021; // com_1hs_attack_01 = real 1-hand SWORD swing (human_m_com_1hs_attack_01.gr2).
                                              // Was 1099 (com_swing) which is NOT in human_m.adr -> client fell back to a
                                              // bare-hand swing. 1021-1024 = com_1hs_attack_01..04; group 1020=com_1hs_attack
                                              // picks one at random (authentic variety) if preferred.
    private const int MeleeHitFx = 7;        // PFX_Hit_Flash — proven generic flash. (FR's real melee hit is
                                             // material-based PFX_Hit_<weaponMat>_vs_<targetMat>; metal-vs-flesh
                                             // = 5414, crit 5627, KO 5637 — a candidate to verify live with
                                             // `!atk 500 50000 5414`, NOT yet visually confirmed.)
    // Melee slot shows the weapon's SWORD icon: the shadow-blade item set (3152) Small IMAGE_ID = 14407.
    // (abil_ninja_shadow_blade's 22599 renders as a leaf in our client; the item sword image is correct.)
    private const int MeleeIcon = 14407;

    // PROVEN-castable AbilityDefinitionIds from the original capture (client renders + lets us cast these).
    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    // Live icon-probe override (set by "!ticon <melee> <special>"); null = use the ability's own icon.
    public static int? DebugMeleeIcon;
    public static int? DebugSpecialIcon;

    public static readonly WeaponAbility BareMelee = new("Strike", MeleeIcon, 150, MeleeAnimation, MeleeHitFx);

    // Resolve a client AbilityDefinition request (op36/12) for a ninja slot def id to the equipped
    // weapon's icon (the op36/13 reply that fills the AbilitiesScreen Attack / Special columns). Ninja ability
    // NAME ids aren't mined yet, so name stays 0 for now — the icon still shows. Null for a non-ours def id.
    public const int MeleeAbilityDefId = MeleeSlotDefId;
    public const int SpecialAbilityDefId = SpecialSlotDefId;

    // Ninja ability name ids for the AbilitiesScreen columns (reversed from en_us_data). Flame Flash / Lightning
    // Strike don't reverse cheaply yet, so those two weapons fall back to the tier-1 names below. Descriptions
    // aren't mined, so the column shows the name with an empty tooltip for now.
    private static readonly IReadOnlyDictionary<string, int> AbilityNameIds = new Dictionary<string, int>
    {
        ["Twisted Edge"] = 420638, ["Cinder Slash"] = 421045, ["Flame Wave"] = 421047, ["Shuriken Storm"] = 420984,
        ["Dragonstrike"] = 442457, ["Dark Assault"] = 421250, ["Ashen Strike"] = 421251, ["Fiery Slice"] = 421266,
        ["Mystic Rush"] = 421267, ["Flame Breath"] = 421278, ["Mystical Blade"] = 421279, ["Shadowslash"] = 421290,
        ["Hidden Strike"] = 421291, ["Mystical Drain"] = 421302, ["Fan of Blades"] = 421303, ["Shadow Army"] = 421248,
        ["Flaming Uppercut"] = 421249,
    };
    private const int FallbackMeleeNameId = 420638;    // Twisted Edge
    private const int FallbackSpecialNameId = 420984;  // Shuriken Storm

    // Name/desc/icon for a column (slot 0 = Attack/melee, 1 = Special) on the equipped sword.
    public static (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot)
    {
        ByWeaponDefId.TryGetValue(weaponDefId, out var weapon);
        var ability = weapon is null ? BareMelee : (slot == 1 ? weapon.Special : weapon.Melee);
        var nameId = AbilityNameIds.TryGetValue(ability.Name, out var id)
            ? id : (slot == 1 ? FallbackSpecialNameId : FallbackMeleeNameId);
        return (nameId, 0, ability.IconImageId);
    }

    // The two ability entries for a sword's item definition (slot 0 = melee, 1 = special) — feeds the columns.
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

    // ── TRAITS ──
    // The Ninja's four passive traits, unlocked by job level. Names/descs reversed from en_us_data (names
    // 420947-50, descs 420971-74); icons are the abil_ninja_* 64px art (Dragon's Boon has its own; the others
    // reuse a fitting ninja ability icon). Magnitudes below are our tuning.
    //   L5  Shrouded Armor — reduces damage taken when struck
    //   L10 Ninja's Grace  — faster run speed + health regen
    //   L15 Dragon's Boon  — a hit sometimes strikes the attacker with lightning
    //   L20 Instigation    — a crit pulls nearby enemies' aggro onto you
    public const int ShroudedArmorLevel = 5;
    public const int NinjasGraceLevel = 10;
    public const int DragonsBoonLevel = 15;
    public const int InstigationLevel = 20;

    // Shrouded Armor: -12% incoming damage once unlocked.
    public const float ShroudedArmorDamageReduction = 0.12f;
    // Ninja's Grace: +15% run speed (matches the archer's Reflexes bump).
    public const float NinjasGraceSpeedMultiplier = 1.15f;

    private static readonly JobTraits.Trait[] TraitData =
    [
        new(420947, 420971, 43,    ShroudedArmorLevel),
        new(420948, 420972, 40,    NinjasGraceLevel),
        new(420949, 420973, 26725, DragonsBoonLevel),
        new(420950, 420974, 11646, InstigationLevel),
    ];

    public static List<AbilityExperience> BuildTraitEntries(int rank) => JobTraits.Build(TraitData, rank, NinjaProfileId);

    // True when the player is a Ninja whose rank has unlocked the given trait level.
    public static bool HasTrait(Player player, int traitLevel) =>
        player.ActiveProfileId == NinjaProfileId && player.ActiveProfile.Rank >= traitLevel;

    // The 10 ninja SPECIALS, each a full kit (melee technique on slot 0 + the named "of X" special on slot 1).
    // IconImageId = the abil_ninja_* Small IMAGE_ID (real ability art). EffectId = the ability's REAL per-special
    // composite effect (CONFIRMED from ActorCompositeEffectDefinitions.xml — the game's own dedicated
    // `PFX_ninja_*` / `PFX_*_ninja-*` EffectDefinitions; see drafts/ninja-special-anim-fx-research.md §FINDINGS
    // iter 4). Animation: all wired from the client's own animation table — decoded from the player model's
    // actor-def `human_m.adr` (slot->clip records) + AnimationGroups.xml, VERIFIED against the client asset log
    // (1011043->weapon_throw) and user sight (1034/1035). The 1hs specials reuse the shared named motion clips
    // (flip_stab/flying_chop/overhand_spin/bum_rush/air_throw/weapon_throw/sweep); Flaming Uppercut + Mystical
    // Blade use their DEDICATED clips (com_h2h_special_07=1017, com_cast_special_11=weapon_power=1061141); Shadow
    // Army uses com_spawn=1404 (no dedicated summon clip). Full table: drafts/anim-NAMED-CLIPS-breakthrough.md.
    //
    // These animations play on the human_m BODY, so they're identical no matter which weapon MODEL grants the
    // special — which is why all five weapon tiers (training sword / blade / scythe / jagged scythe / shadow
    // blade) share these records below. (The melee SWING clip + icon are sword-styled; on a scythe that swing
    // reads a touch off — a cosmetic live-polish item, not a functional one.)

    private static readonly NinjaWeapon ShurikenStormKit = new(
        new("Twisted Edge",   MeleeIcon, 2870, MeleeAnimation, MeleeHitFx),
        new("Shuriken Storm",     22986, 8302, 1039, 4012, 0)); // LIVE-OBSERVED: anim com_1hs_special_09=inverted_flip_attack (frontflip + downward melee); FX 4012 PFX_Slashes_Three_Symbol ("3 scratch marks") on the ENEMY only (impact); no cast FX on caster

    private static readonly NinjaWeapon FlameWaveKit = new(
        new("Cinder Slash",   MeleeIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Flame Wave",         22974, 10674, 1032, 0, 16140)); // anim sweep; cast 16140 PFX_fire_orange_cog_ninja-flame-wave (ground AoE on caster, at feet); enemy impact REMOVED (user: FX only at my feet)

    private static readonly NinjaWeapon DragonstrikeKit = new(
        new("Flame Flash",    MeleeIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Dragonstrike",       22965, 10674, 1035, 0, 0, 0, 0, 16186)); // anim flying_chop (confirmed); launch 16014 REMOVED; the LAND FX 16186 now plays ON THE CASTER (feet) at the END of the anim (CasterEndEffectId); nothing on the enemy (user round-2)

    private static readonly NinjaWeapon ThousandStormsKit = new(
        new("Lightning Strike", MeleeIcon, 2372, MeleeAnimation, MeleeHitFx),
        new("1000 Storms",          22992, 8302, 1033, 0, 16088, AoeRadius: 12f)); // anim 1033 air_throw (jump-up + air-slam motion; same clip as Deception — user-chosen); cast 16088 PFX_lightning_blue_root_ninja-special on caster; enemy impact REMOVED (user: stop casting on target; FX at my sword at end of anim — sword placement TODO). AOE (user request 2026-07-03): hits the whole pack within 12u of the caster

    private static readonly NinjaWeapon ShadowArmiesKit = new(
        new("Dark Assault",   MeleeIcon, 2608, MeleeAnimation, MeleeHitFx),
        new("Shadow Army",        22989, 3000, 1061143, 21, 16483, 3)); // anim warcry; CAST 16483 PFX_summon_purple_cast (ONE-SHOT — 5276 was a _loop that never ended); impact 21 black smoke; SUMMONS 3 shadow clones

    private static readonly NinjaWeapon SolarFlareKit = new(
        new("Ashen Strike",     MeleeIcon, 2870, MeleeAnimation, MeleeHitFx),
        new("Flaming Uppercut",     22977, 8302, 1017, 0, 16119)); // anim flaming_uppercut (DEDICATED); cast 16119 PFX_ninja_flaming-uppercut on caster; enemy impact REMOVED (user: player is the only one with the FX, not the NPC)

    private static readonly NinjaWeapon DragonBreathKit = new(
        new("Fiery Slice",  MeleeIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Flame Breath",     22971, 10674, 1037, 0, 16129)); // anim bum_rush (WRONG — user to ID correct clip); cast 16129 PFX_fire_orange_mouth_ninja-flame-breath on caster; enemy impact REMOVED (user: FX played by me only, not any enemy)

    private static readonly NinjaWeapon MysticismKit = new(
        new("Mystic Rush",    MeleeIcon, 2608, MeleeAnimation, MeleeHitFx),
        new("Mystical Blade",     22980, 3000, 1061141, 0, 0, 0, 16169)); // anim weapon_power (DEDICATED); empowers the WEAPON: 16169 WFX_beam-trail_blue-purple_ninja-mystical-blade binds to the SWORD slot (SwordEffectId); NO body/enemy FX (user round-2: only on my sword, not on any bodies)

    private static readonly NinjaWeapon SoulPowerKit = new(
        new("Shadowslash",    MeleeIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Mystical Drain",     22983, 8302, 1034, 16180, 16180)); // anim flip_stab (confirmed); cast+impact 16180 PFX_beam_red_blue_circ_lg_AOE-drain

    private static readonly NinjaWeapon DeceptionKit = new(
        new("Hidden Strike", MeleeIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Fan of Blades",     22968, 5977, 1033, 0, 16185)); // anim air_throw; cast 16185 PFX_sparkles_multi_cog_ninja-fan-of-blades on caster; enemy impact REMOVED (user: FX only on the animation, not the targets — sword bone placement still TODO)

    // weapon def id -> kit. ALL 30 ninja weapons (5 model tiers) are wired now; each is named "Ninja's <weapon>
    // of X" and grants special X, so a lower tier just reuses the same tuned kit as the Shadow Blade (top tier).
    // Only the Shadow Blade set (75110-75119) covers all 10 specials; the lower tiers stop earlier (no
    // Soul Power / Deception below the Shadow Blade). Before this, only 75110-75119 were wired and the other 20
    // fell back to the bare "Strike" with no special.
    public static readonly IReadOnlyDictionary<int, NinjaWeapon> ByWeaponDefId = new Dictionary<int, NinjaWeapon>
    {
        // Training Sword (75090-75091, sword_ar_ag_weapon_trainingsword, rank 1)
        [75090] = DragonstrikeKit,
        [75091] = ThousandStormsKit,

        // Blade (75092-75095, sword_ar_ag_weapon_twistedblade)
        [75092] = DragonstrikeKit,
        [75093] = ThousandStormsKit,
        [75094] = ShurikenStormKit,
        [75095] = FlameWaveKit,

        // Scythe (75096-75101, axe_ar_ag_weapon_scythe)
        [75096] = DragonstrikeKit,
        [75097] = ThousandStormsKit,
        [75098] = ShurikenStormKit,
        [75099] = FlameWaveKit,
        [75100] = ShadowArmiesKit,
        [75101] = SolarFlareKit,

        // Jagged Scythe (75102-75109, axe_ar_ag_weapon_jaggedscythe)
        [75102] = DragonstrikeKit,
        [75103] = ThousandStormsKit,
        [75104] = ShurikenStormKit,
        [75105] = FlameWaveKit,
        [75106] = ShadowArmiesKit,
        [75107] = SolarFlareKit,
        [75108] = DragonBreathKit,
        [75109] = MysticismKit,

        // Shadow Blade (75110-75119, sword_ar_ag_weapon_shadowblade — top tier, all 10 specials)
        [75110] = DragonstrikeKit,
        [75111] = ThousandStormsKit,
        [75112] = ShurikenStormKit,
        [75113] = FlameWaveKit,
        [75114] = ShadowArmiesKit,
        [75115] = SolarFlareKit,
        [75116] = DragonBreathKit,
        [75117] = MysticismKit,
        [75118] = SoulPowerKit,
        [75119] = DeceptionKit,

        // Retail ninja swords (coin store / give) — themed to a fitting kit so they get real abilities.
        [13663] = DragonstrikeKit, [55337] = DragonstrikeKit, [70444] = DragonstrikeKit, [76470] = DragonstrikeKit, // Dragon Blade
        [9031] = ThousandStormsKit, [13669] = ThousandStormsKit, [55360] = ThousandStormsKit,                       // Storm Breaker
        [78715] = MysticismKit,     // Lunar Blade
        [79022] = SoulPowerKit,     // Precursor Energy Blade
        [48322] = DragonBreathKit,  // Molten Dragon Blade
    };

    public static readonly int[] AllWeaponDefIds = ByWeaponDefId.Keys.ToArray();

    public static NinjaWeapon? GetEquippedWeapon(Player player)
    {
        var defId = player.GetEquippedWeaponDefinitionId();
        return defId != 0 && ByWeaponDefId.TryGetValue(defId, out var weapon) ? weapon : null;
    }

    // slot 0 = melee, slot 1 = special.
    public static WeaponAbility ResolveAbility(Player player, int slot)
    {
        var weapon = GetEquippedWeapon(player);

        if (weapon is null)
            return BareMelee;

        return slot <= 0 ? weapon.Melee : weapon.Special;
    }

    // Build the 2-slot ability toolbar from the equipped ninja weapon. Slot icon = each ability's real
    // IMAGE_ID (overridable live via !ticon for probing).
    public static AbilityPacketSetDefinition BuildToolbar(Player player, IResourceManager resources)
    {
        var weapon = GetEquippedWeapon(player);

        if (weapon is null)
            return AbilityPacketSetDefinition.CreateEmpty(NinjaProfileId);

        var nameId = 0;
        if (resources.ClientItemDefinitions.TryGetValue(player.GetEquippedWeaponDefinitionId(), out var weaponDef))
            nameId = weaponDef.NameId;

        var def = new AbilityPacketSetDefinition { ProfileId = NinjaProfileId, SlotCount = 8 };

        def.Slots.Add(MakeSlot(MeleeSlotDefId, DebugMeleeIcon ?? weapon.Melee.IconImageId, nameId, manaCost: 0));
        // ENERGY (2026-07-03): the special costs the full 100 bar (ground-truthed from the 04-01 capture;
        // server gate lives in AbilityPacketClientRequestStartAbilityHandler). The slot's ManaCost is what
        // makes the CLIENT grey the button out while current energy (op38/sub13) is below the cost — with
        // 0 the client thinks it's free and the blocked presses just look dead.
        def.Slots.Add(MakeSlot(SpecialSlotDefId, DebugSpecialIcon ?? weapon.Special.IconImageId, nameId, manaCost: SpecialEnergyCost));

        return def;
    }

    // Energy cost of every slot-1 weapon special (the full bar — see the ability handler's gate).
    public const int SpecialEnergyCost = 100;

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
