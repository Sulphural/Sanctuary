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
// ANIM/FX are REAL (extracted from AnimationGroups.xml/AnimationTypes.xml + ActorCompositeEffectDefinitions.xml):
//   melee swing = com_h2h_attack(1000) for fists / com_2hp_attack(1080) for 2h hammers (picked per weapon);
//   2h-hammer specials = com_2hp_special_01..08 (1091-1098). AnimationTypes.xml (searched 2026-07-25) ALSO has
//   a parallel com_h2h_special_01..08 family (ids 1011-1018, exact -80 offset from the 2hp ids) that retail uses
//   for FIST specials — wired below (FistSpecialAnimFor) so PummelKit/SuckerPunchKit/PowerFistKit play a real
//   fist-special clip instead of the 2h-hammer clip when a FistWeaponDefIds weapon is equipped.
//   FX = the dedicated PFX_*brawler-* composite effects WHERE ONE EXISTS (searched ActorCompositeEffectDefinitions.xml
//   for every named special on 2026-07-25 — see per-kit comments below for what was found vs. still a placeholder).
//   EffectId = impact FX on the TARGET; CastEffectId = FX on the CASTER (star-rings/aura/dirt).
// ICONS are REAL too: the abil_brawler_* Small IMAGE_IDs from the client Resources/Images (ImageSets.txt ->
// ImageSetMappings.txt type 5). DAMAGE: mostly still estimates — see per-kit comments for what a 2026-07-25
// WebSearch of the FreeRealms fandom wiki / MMORPG.com preview article could and couldn't confirm (direct
// WebFetch of freerealms.fandom.com returned HTTP 402 in this environment, so these are search-snippet-sourced,
// not read first-hand — treat with a little less confidence than the anim/FX/icon finds above).
// Trait NAME/DESC locale ids still want reversing.
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
    // IcoPummel/Roundhouse/Rumble/SuckerPunch/KickDirt/Knockout/HammerToss/Slam all come from the same
    // abil_brawler_<name> image-set block (sets 4879-4886, ImageSets.txt lines 4199-4206 -> ImageSetMappings.txt
    // type 5). IcoLegSweep/IcoEnrage look numerically out of place next to that block but ARE real dedicated
    // icons too — they just live in a SEPARATE, earlier-allocated image-set block: "abil_brawler_fight_leg_sweep"
    // (set 2653) and "abil_brawler_fight_enrage" (set 2652), ImageSets.txt lines 1973-1974 -> ImageSetMappings.txt
    // gives Small=11636 / 11633 respectively (verified 2026-07-25). Not a bug — just two different ID ranges.
    private const int IcoPummel = 22926, IcoLegSweep = 11636, IcoRoundhouse = 22932, IcoRumble = 22929,
        IcoSuckerPunch = 22938, IcoKickDirt = 22920, IcoKnockout = 22923, IcoHammerToss = 22917,
        IcoEnrage = 11633, IcoSlam = 22935,
        // IcoPowerRain: confirmed NO dedicated abil_brawler_power_rain / abil_brawler_power_fist image set
        // exists (grepped ImageSets.txt for "power fist", "power rain", "brawler_power", "punch", "fist" on
        // 2026-07-25 — only item-model icons like item_fist_ar_ag_weapon_* turned up, no ability icon). The
        // spinattack fallback (set 870, abil_brawler_spinattack) stays the closest available generic icon.
        IcoPowerRain = 3373; // spinattack (no dedicated Power Rain/Power Fist icon exists in the client tables)

    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    // Fist-model weapons swing h2h; everything else (hammers/clubs/axes) swings 2hp.
    private static readonly HashSet<int> FistWeaponDefIds = new() { 13659, 55335, 78197, 78712, 78713 };
    private static int MeleeAnimFor(int weaponDefId) => FistWeaponDefIds.Contains(weaponDefId) ? FistMeleeAnim : HammerMeleeAnim;

    // com_2hp_special_01..08 (1091-1098, AnimationTypes.xml) <-> com_h2h_special_01..08 (1011-1018) — a
    // confirmed, exact -80 id offset for every one of the 8 slots (verified against AnimationTypes.xml
    // 2026-07-25). Used to translate a hammer-special kit's Animation to the fist-special equivalent when a
    // FistWeaponDefIds weapon is equipped (PummelKit/SuckerPunchKit/PowerFistKit all get shared with fist
    // weapons in ByWeaponDefId below).
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

    // ── SPECIALS (10) ── melee (slot 0) + the named special (slot 1). Special anim = a com_2hp_special clip
    // (auto-translated to the com_h2h_special equivalent for fist weapons via FistSpecialAnimFor in
    // ResolveAbility). FX are the dedicated brawler composites WHERE ONE EXISTS — see per-kit comments.
    // AoeRadius > 0 => hits every hostile within range of the caster.
    //
    // DAMAGE: only 2 of the 11 kits below could be updated from a real source (Knockout, Kick Dirt — a
    // 2026-07-25 WebSearch of the FreeRealms fandom wiki turned up numbers for those two weapon-tier specials
    // in the same 1000s-scale our numbers already use). Pummel/Leg Sweep/Ready to Rumble/Enrage turned up real
    // retail numbers too, but they're BASE-ability numbers (tens, not thousands) from a pre-launch preview
    // article and don't sit on the same scale as this file's weapon-tier estimates, so extrapolating a
    // matching thousands-scale number from them would be a guess, not a source — left untouched, cited in a
    // comment instead. Roundhouse Kick/Sucker Punch/Hammer Toss/Slam/Power Rain turned up nothing at all.
    private static readonly BrawlerWeapon PummelKit = new(
        new("Pound",  MeleeIcon, 2600, HammerMeleeAnim, MeleeHitFx),
        // Damage unchanged (still an estimate) — retail data found (freerealms fandom wiki, via WebSearch
        // 2026-07-25, not independently fetched) is a MULTI-HIT base ability ("3 consecutive blows for 29 max
        // damage each") plus two weapon-tier points — Anvil Hammer of Pummeling 1152 dmg (L8), Drill Hammer of
        // Pummeling 2015 dmg (L12) — with no Atlas-tier (L16, what this kit represents) figure to anchor to,
        // so extrapolating one further would be a guess rather than a source.
        new("Pummel", IcoPummel, 9000, 1091, 5252, CastEffectId: 5257)); // star-rings on hands + pummel land

    private static readonly BrawlerWeapon LegSweepKit = new(
        new("Glancing Blow", MeleeIcon, 2400, HammerMeleeAnim, MeleeHitFx),
        // EffectId FIXED 2026-07-25: was 5315, which is actually "PFX_hit-flash_red_cog_warrior-kick-land" — a
        // cross-JOB (Warrior) effect, not Brawler's own. Real Brawler Leg Sweep landing FX found instead: a
        // tiered "...leg-sweep-land-1..5" family (ids 5275/15395/15397/15398/15400, blue->red,
        // ActorCompositeEffectDefinitions.xml); this kit isn't itself tiered per weapon level, so tier-1 (5275,
        // blue) is used as the single baseline. CastEffectId is new too: "PFX_beam_trail_foot-r_red_lg_leg-sweep"
        // (16195) is a real dedicated foot-trail effect for this exact move name (not shared with any other job).
        // Damage: retail base ability = a flat 50-per-target AoE ("sweeps all nearby enemy legs, causing 50
        // damage to each") — confirms the AoeRadius design here is retail-accurate; number left as our estimate
        // (same thousands-scale mismatch as Pummel above).
        new("Leg Sweep",     IcoLegSweep, 7500, 1092, 5275, CastEffectId: 16195, AoeRadius: 10f));

    private static readonly BrawlerWeapon RoundhouseKit = new(
        new("Smack",           MeleeIcon, 2500, HammerMeleeAnim, MeleeHitFx),
        // EffectId 5315 is KEPT but now correctly labeled: it's "PFX_hit-flash_red_cog_warrior-kick-land" — a
        // cross-job (Warrior) kick-impact effect, not a Brawler one. No dedicated Roundhouse-Kick LANDING/impact
        // effect exists in ActorCompositeEffectDefinitions.xml (searched 2026-07-25), so this is kept as a
        // placeholder (closest visual match — a kick landing — beats the fully generic hit-flash) rather than a
        // confirmed retail match. CastEffectId is new and IS a real, dedicated find: "PFX_beam_trail_foot-r_
        // green_lg_roundhouse-kick" (16194) is named for this exact move (foot-trail during the kick windup).
        new("Roundhouse Kick", IcoRoundhouse, 8000, 1093, 5315, CastEffectId: 16194, AoeRadius: 10f));

    private static readonly BrawlerWeapon RumbleKit = new(
        // EffectId/CastEffectId SWAPPED 2026-07-25: 16212 ("PFX_rocks_fire_orange_exp_lg_brawler-rumble") has
        // EVERY sub-effect gated on triggerName="ap_contact" in ActorCompositeEffectDefinitions.xml — i.e. it
        // only ever plays at the hit/contact moment, not on cast — so it belongs on the TARGET (EffectId), not
        // the caster. No separate caster-side cast effect was found for this move, so CastEffectId is left at 0.
        new("Wallop",          MeleeIcon, 2700, HammerMeleeAnim, MeleeHitFx),
        // Damage: likely (not certain) corresponds to "Rock Toss" in a pre-launch MMORPG.com preview article —
        // "launch rocks that stun 3 enemies and cause 53 damage each" — the name match isn't exact, but the FX
        // (rocks/fire/explosion, "-rumble" suffixed) lines up well enough to flag the correlation. Base-ability
        // number again, not on this file's thousands scale, so the estimate is left as-is.
        new("Ready to Rumble", IcoRumble, 8500, 1094, 16212));

    private static readonly BrawlerWeapon SuckerPunchKit = new(
        new("Wild Swing",   MeleeIcon, 2400, HammerMeleeAnim, MeleeHitFx),
        // EffectId FIXED 2026-07-25: was the generic MeleeHitFx. "PFX_beam_spiral_blue-orange_lg_brawler-
        // suckerpunch" (16198) is the real dedicated composite for this move AND it isn't cast-only — it has an
        // ap_contact_2-gated particle alongside its immediate ones, i.e. it carries its own impact-stage content
        // — so reusing the same id for both EffectId and CastEffectId is a sourced reading of the data, not a
        // guess. No distinct impact-only variant exists separately.
        new("Sucker Punch", IcoSuckerPunch, 9500, 1095, 16198, CastEffectId: 16198));

    private static readonly BrawlerWeapon KickDirtKit = new(
        new("Whack",     MeleeIcon, 2300, HammerMeleeAnim, MeleeHitFx),
        // EffectId FIXED 2026-07-25: was the generic MeleeHitFx; now the dedicated "PFX_brawler_kick-dirt_
        // brown_lg" (16206) composite, reused for CastEffectId too (same reasoning as Sucker Punch above — no
        // separate impact-only variant exists). Damage UPDATED from a WebSearch (2026-07-25, freerealms fandom
        // wiki via search snippet — direct WebFetch of the page 402'd in this environment, so treat as
        // slightly-less-certain than a first-hand read) that put a weapon-tier Kick Dirt special at 3420 damage,
        // on the same scale this file already uses. AoeRadius ADDED for the same reason: the sourced
        // description is "kicks up a blinding sandstorm, damaging all nearby opponents" — an AoE, matching the
        // Leg Sweep/Roundhouse Kick radius already used elsewhere in this file.
        new("Kick Dirt", IcoKickDirt, 3420, 1096, 16206, CastEffectId: 16206, AoeRadius: 10f));

    private static readonly BrawlerWeapon KnockoutKit = new(
        new("Thump",    MeleeIcon, 2900, HammerMeleeAnim, MeleeHitFx),
        // Damage UPDATED from a WebSearch (2026-07-25, freerealms fandom wiki via search snippet, same 402
        // caveat as Kick Dirt above): a weapon-tier Knockout special description ("unleashes your ultimate
        // attack, damaging all enemies in front of you") gave 10674 damage — close to, and now replacing, the
        // prior 11000 estimate.
        new("Knockout", IcoKnockout, 10674, 1097, 16200)); // knockout orange on target

    private static readonly BrawlerWeapon HammerTossKit = new(
        new("Crush",       MeleeIcon, 2600, HammerMeleeAnim, MeleeHitFx),
        // 16289 is a LOOPING trail (PFX_..._loop_...) — play it via the effect-tag path and remove after the
        // throw window so it doesn't linger forever; 15203 earthquake lands on the target. (Both already
        // dedicated Brawler-hammer-toss composites; no damage source found for this one.)
        new("Hammer Toss", IcoHammerToss, 8500, 1098, 15203, CastEffectId: 16289, CastEffectStopMs: 1500));

    private static readonly BrawlerWeapon EnrageKit = new(
        new("Bash",   MeleeIcon, 2700, HammerMeleeAnim, MeleeHitFx),
        // EffectId intentionally LEFT as the generic MeleeHitFx: "PFX_brawler_enrage_yellow_cast" (16145) is
        // exclusively an untagged, immediate cast-time effect (paired with a separate "..._persist" loop, 16147,
        // for the buff duration) with no ap_contact-gated content at all — i.e. it's a self-buff aura, not
        // designed to double as a target-impact effect the way Sucker Punch/Kick Dirt's composites are. Reusing
        // it for EffectId would be a guess this data doesn't support, so it's left alone.
        // Damage/mechanic note: a 2026-07-25 WebSearch (MMORPG.com preview article) describes retail Enrage as
        // a temporary SELF-BUFF — "rampage for 10 seconds, increasing melee damage by 8 and critical hit chance
        // by 1%" — not a direct-damage attack at all. Redesigning the mechanic is out of scope for this pass
        // (damage/FX/anim sourcing only), so this stays an instant-damage special; flagging the mismatch here
        // for whoever picks up the mechanic later. Damage number itself untouched (no comparable scale source).
        // Animation 1091 reuses Pummel's slot — see the ResolveAbility comment below on why (8 generic anim
        // slots split across 11 named specials means at least 3 must share; not a confirmed retail 1:1 map).
        new("Enrage", IcoEnrage, 8000, 1091, MeleeHitFx, CastEffectId: 16145)); // enrage cast aura

    private static readonly BrawlerWeapon SlamKit = new(
        new("Smash", MeleeIcon, 2800, HammerMeleeAnim, MeleeHitFx),
        // EffectId 5252 (Pummel's own landing flash) is KEPT but is a placeholder, not a confirmed match:
        // searched ActorCompositeEffectDefinitions.xml for "_slam", "slam_", and "smash" on 2026-07-25 and found
        // zero dedicated composites for this move. Animation (1092) is likewise a reuse of Leg Sweep's slot —
        // see the ResolveAbility comment below on why that's not fully fixable either. No damage source found.
        new("Slam",  IcoSlam, 10500, 1092, 5252)); // heavy impact, reused from Pummel — no dedicated FX exists

    private static readonly BrawlerWeapon PowerFistKit = new(
        new("Power Smash", MeleeIcon, 1800, FistMeleeAnim, MeleeHitFx),
        // CastEffectId 16084 is KEPT but was mislabeled by the original comment: its real name is
        // "PFX_brawler_pummel_land_level-5" — a tier-5 variant of PUMMEL's OWN landing flash, not a Power Rain/
        // punch-rain/AoE effect at all. Searched ActorCompositeEffectDefinitions.xml for "rain", "flurry",
        // "multi-punch", "combo", "power-fist"/"power-rain", and the weapon's own model name ("bikerfist") on
        // 2026-07-25 and found nothing Power-Rain-specific, so this stays the closest available real Brawler
        // composite rather than falling back further to the fully generic hit-flash. No damage source found.
        // Animation 1091 also reuses Pummel's slot (fist-translated to 1011 at runtime, same as PummelKit on a
        // fist weapon) — see the ResolveAbility comment below; unavoidable with 8 slots for 11 named specials.
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

        if (slot <= 0)
            return weapon.Melee with { Animation = MeleeAnimFor(defId) };

        // Every kit's Special.Animation is authored as a com_2hp_special_XX (2h-hammer) clip. PummelKit,
        // SuckerPunchKit and PowerFistKit are also handed out to FIST weapons (FistWeaponDefIds — see
        // ByWeaponDefId above), so for those equips translate to the real com_h2h_special_XX fist-clip
        // equivalent (FistSpecialAnimFor) instead of playing a 2-handed hammer swing on bare/clawed fists.
        //
        // Known gap: 11 named specials share only 8 numbered anim slots (com_2hp_special_01..08 /
        // com_h2h_special_01..08), and neither AnimationGroups.xml nor AnimationTypes.xml labels which slot
        // is "supposed" to belong to which named ability — the slots are anonymous. So Enrage/Power Rain
        // sharing Pummel's slot (1091) and Slam sharing Leg Sweep's slot (1092) are an UNAVOIDABLE, not a
        // confirmed-wrong, consequence of that 8-slot ceiling; there's no retail source that says which of
        // the 8 clips a given special "really" uses, so no reassignment here would be any less of a guess.
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
