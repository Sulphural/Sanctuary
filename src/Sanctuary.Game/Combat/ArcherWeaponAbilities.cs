using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// COMBAT: the ARCHER weapon-ability kit — the bow twin of NinjaWeaponAbilities (same
// weapon-drives-abilities model). Every "Archer's <family> Bow of <special>" weapon
// (Resources/ClientItemDefinitions.json, ids 75000-75029) grants TWO abilities:
// slot 0 = the bow's basic shot, slot 1 = the named special.
//
// ★ GROUND TRUTH (2026-07-10, all from the ORIGINAL game's data):
//   * ability PAIRS per bow: the ZAM wiki mirror's FR item pages (wow.allakhazam.com/wiki/FR_Item:...)
//     list both abilities verbatim for every bow — Barrage/Volley, Icy Arrow/Blizzard Blast,
//     Charged Shot/Explosive Shot, Multi-Shot/Splitting Arrow, Power Shot/Stunning Shot,
//     Smoldering Shot/Flaming Arrow, Electric Arrow/Lightning Call, Sonic Arrow/Sonic Boom,
//     Cover Fire/Ricochet, Ember Arrow/Firebomb. The basic is tied to the SPECIAL's element
//     (an "of Blizzards" bow fires Icy Arrows), not to the bow family.
//   * DAMAGE: wiki-anchored — Blizzard Blast 640 (L1), Explosive Shot 1554 (L5), Barrage 1492 +
//     Volley 2707 (L12 Recurve), Electric Arrow 2372 (L16 Raptor), Splitting Arrow 4750 (L16 tier).
//     Unanchored tiers interpolate on that curve (basic ≈ 0.55 × special, same ratio as the L12
//     anchor). Bow families ARE the level tiers: Bow=L1, Horse=L5, Composite=L8, Recurve=L12,
//     Raptor=L16.
//   * ICONS: the abil_archer_* image sets' Small (type 5) IMAGE_IDs from the client's own
//     ImageSets.txt + ImageSetMappings.txt (flat image ids, NOT set ids — the ninja lesson).
//     The basic slot shows the equipped bow family's own item image (like the ninja's sword).
//   * FX: the client ships DEDICATED archer effects (ActorCompositeEffectDefinitions.xml):
//     PFX_arrows_rain_launch/1110 + PFX_arrows_rain_land_loop_archer-volley/16204 (Volley),
//     PRJ_archer_freezing-shot_trail/16110 + PFX_archer_freezing-arrow_land/16116 (Blizzards),
//     PFX_fire_orange_skel_explosive-arrow-land-1/15373 (Explosive Shot),
//     PRJ_archer_multishot_trail/16056 + multishot-arrow-land-1/5307 (Multi-Shot),
//     PRJ_magical_blue_split-arrow/15488 + split-arrow-land/5246 (Splitting Arrow),
//     PRJ_archer_stunning-shot_trail/16050 + stunning_shot hit-flash/16054 (Stunning Shot),
//     PFX_archer_fire-arrow/16121 (Flaming Arrow),
//     PFX_lightning_blue_root_archer-lightning-call/16117 (Lightning Call),
//     PRJ_archer_ricochet_trail_red/16214 + shrapnel_archer-ricochet/16215 (Ricochet),
//     PFX_fire_orange_root_archer-firebomb-MIRV/16118 (Firebomb).
//     Sonic Boom has NO dedicated archer FX in the client — 5710 (shockwave ground-pound) is the
//     placeholder (RE-CHECKED 2026-07-25: still nothing sonic/boom/shock-named fits better) until a live
//     probe finds the real one. Basic-shot impacts (9 of the 10 pairs) now use PFX_Hit_Wood_vs_Flesh/5447
//     (arrow-shaft-vs-target material hit, the same PFX_Hit_<mat>_vs_<mat> system the ninja file cites for
//     melee, e.g. metal-vs-flesh=5414) instead of the generic PFX_Hit_Flash/7 — Multi-Shot's basic already
//     had its own dedicated land FX (5307) and is unchanged.
//   * ANIMATIONS (RE-DONE 2026-07-25): the bow clips are the com_range_* slots (client AnimationTypes.xml +
//     AnimationGroups.xml — the same two files WizardWeaponAbilities.cs cites for com_cast_special_01..10 =
//     1131-1140): com_range_attack_01..05 = 1101-1105 (draw-and-fire), com_range_special_01..10 = 1106-1110
//     PLUS 1051111-1051115 (the client's own table jumps the 06-10 slots to far higher, non-contiguous ids —
//     the same pattern every other job's *_special_09/10+ slots show, e.g. com_h2h_special_10=1001020,
//     com_2hs_special_09/10=1021059/1021060). Basic uses 1102 (attack_02; 1101 carries hideSlotType=1). Each
//     of the 10 named specials below now gets its OWN clip from this real family (assigned in the
//     Volleys..Firebomb function order) instead of all ten sharing 1106. Which NAMED motion (quick-draw vs
//     power-draw etc.) backs each numeric id past 1110 is still unconfirmed — refine with the "!anim <id>"
//     live probe if a wrong one looks off, the same workflow that tuned the ninja clips.
// Sniper + Rain are PARKED (not on the bar): the retail bar carries exactly 2 ability slots (the
// 04-01 capture's set was 2 full + 6 empty even at max level; the UI's remaining slots are
// battle-ITEM slots, wire Type 2). The research is done — Sniper Shot (icon 22575, dedicated FX
// sniper-shot-land 15384, retail L15 ability) and Rain of Arrows (FX 1110/1111, caster AoE) —
// so they're kept here ready if we ever surface them elsewhere (e.g. a real ability-def route).
// SNIPER SHOT multiplier (RE-SOURCED 2026-07-25): the "Scoped Stalker Bow" FreeRealms wiki page (found via
// search snippet — direct WebFetch 402'd on freerealms.fandom.com every attempt, so this is search-result
// text, not a verified page read) lists Sniper Shot's OWN damage table: 889/1554/2716/4750/8302 at levels
// 1/4/8/12/16. Lined up against our own wiki-anchored special-tier numbers (640 @L1, 2707 @L12, 4750 @L16),
// that's ~1.39x / 1.755x / 1.748x the same-tier special — clearly not the old unsourced 1.15x guess. The two
// higher (more-anchored) tiers agree closely, so updated to 1.75x. Still an approximation given the source
// access limits above.
// RAIN OF ARROWS (confirmed real 2026-07-25, user-verified): a distinct archer ability - a volley of arrows
// falls from the sky and hits every enemy in range, not the same thing as Volley despite the earlier wiki-
// search doubt below. The current shape (caster-centered AoE burst, CastEffectId 1110 launch-up + EffectId
// 1111 land-dust per victim) already matches that mechanic correctly. The 0.75x damage multiplier and 10m
// radius are still unsourced numbers, not the ability's existence/shape - refine those if a real source
// surfaces, but the "might just be Volley" doubt from the wiki search below is resolved: it's real, distinct
// content. (Original note, kept for context: repeated targeted wiki searches for "Rain of Arrows" turned up
// nothing FR-specific under that exact name — every source found instead named the Scoped Stalker Bow's AoE
// ability "Volley".)
public sealed record ArcherWeapon(WeaponAbility Basic, WeaponAbility Special, WeaponAbility Sniper, WeaponAbility Rain);

public static class ArcherWeaponAbilities
{
    // Profiles.json "Archer" (job category 2, combat).
    public const int ArcherProfileId = 35;

    // Auto-target reach for an unselected bow shot. Arrows work at range — the melee
    // kit's 7u cap would make an archer walk into bite range to hit anything.
    public const float BowReach = 30f;

    // ── TRAITS ───────────────────────────────────────────────────────────────────────────────────────
    // The Archer's four passive traits (freerealms wiki / ZAM), unlocked purely by job level. No cost, no
    // choice — every Archer gets all four by level 20. They're passive, so they have no cast FX / anim / sound
    // (the client ships only their icons); the effects apply to BOTH the basic shot and the specials. The wiki
    // gives no numbers, so the magnitudes below are our tuning (mirrors how the weapon damage tables were set).
    //   L5  Precision    — more damage + higher crit CHANCE
    //   L10 Marksmanship — crits hit harder (crit MULTIPLIER)
    //   L15 Reflexes     — faster run speed + dodge chance
    //   L20 Lucky Shot   — a hit sometimes restores a little energy
    public const int PrecisionLevel = 5;
    public const int MarksmanshipLevel = 10;
    public const int ReflexesLevel = 15;
    public const int LuckyShotLevel = 20;

    // Precision: +8% flat damage and +12 percentage-points of crit chance once unlocked.
    public const float PrecisionDamageBonus = 0.08f;
    public const int PrecisionCritChanceBonus = 12;
    // Base crit chance for an archer with no traits (so Precision is an increase, not the whole thing).
    public const int BaseCritChancePercent = 5;
    // Marksmanship: a crit does +75% on top of the normal 2× (i.e. 2.75×) once unlocked.
    public const float MarksmanshipCritBonus = 0.75f;
    public const float BaseCritMultiplier = 2.0f;
    // Reflexes: +15% run speed (8.0 -> 9.2) and a 15% chance to dodge an incoming enemy attack.
    public const float ReflexesSpeedMultiplier = 1.15f;
    public const int ReflexesDodgePercent = 15;
    // Lucky Shot: 20% chance per landed hit to restore 8 energy.
    public const int LuckyShotChancePercent = 20;
    public const int LuckyShotEnergyRestore = 8;

    // True when the player is an Archer whose active job rank has unlocked the given trait level.
    public static bool HasTrait(Player player, int traitLevel) =>
        player.ActiveProfileId == ArcherProfileId && player.ActiveProfile.Rank >= traitLevel;

    // The four traits for the AbilitiesScreen's Traits section — real client ids. NameId/DescriptionId were
    // reversed from en_us_data via Jenkins lookup2 (names 420934-37, descriptions 420958-61); IconId from
    // ImageSetMappings.txt type6/64px art (Precision 33, Marksmanship 31; Reflexes reuses evasion 22570, Lucky
    // Shot reuses advantage 39861 — no dedicated art ships for those two).
    private static readonly (int NameId, int DescId, int IconId, int Level)[] TraitData =
    [
        (420934, 420958, 33,    PrecisionLevel),
        (420935, 420959, 31,    MarksmanshipLevel),
        (420936, 420960, 22570, ReflexesLevel),
        (420937, 420961, 39861, LuckyShotLevel),
    ];

    // The four Archer traits as passive AbilityExperience entries (IsActivateable=false, gated by
    // RequiredLevel) for the profile's ability list — this is what fills the AbilitiesScreen Traits panel.
    // The list ends with a Present=0 terminator (the profile reader stops there).
    // ★ Present MUST be DISTINCT per entry (we use the NameId): an earlier version set Present=1 on all four,
    // which crashed the client on connect — live-bisected 2026-07-14 (distinct ids parse cleanly at any count;
    // the "fixed buffer overflow" theory was wrong). Present is the record Id / list control (0 = terminator).
    public static List<AbilityExperience> BuildTraitEntries(int rank)
    {
        var list = new List<AbilityExperience>(TraitData.Length + 1);
        AppendTraits(list, rank);
        list.Add(new AbilityExperience { Present = 0 }); // terminator
        return list;
    }

    private static void AppendTraits(List<AbilityExperience> list, int rank)
    {
        foreach (var t in TraitData)
        {
            // The padlock is driven by the ability's RANK (this Level field), NOT a compare to RequiredLevel —
            // live-verified 2026-07-15: forcing Rank>0 on every entry unlocked ALL of them regardless of their
            // (higher) RequiredLevel. So: Rank 1 once the job level reaches the trait's unlock level, else 0
            // (locked). RequiredLevel is only the "Unlocked at level N" caption.
            var unlocked = rank >= t.Level;
            list.Add(new AbilityExperience
            {
                Present = t.NameId,          // DISTINCT, non-zero record id (duplicate ids crash the client)
                IsActivateable = false,      // passive => shown in the Traits section
                NameId = t.NameId,
                DescriptionId = t.DescId,
                IconId = t.IconId,
                Level = unlocked ? 1 : 0,    // Rank: >0 = unlocked (padlock off), 0 = locked
                RequiredLevel = t.Level,     // "Unlocked at level N" caption
            });
        }
    }

    // ── ACTIVE ABILITIES (the AbilitiesScreen's non-Traits rows) ──────────────────────────────────────────
    // The screen's active-ability rows are the ACTIVATABLE (IsActivateable=true) entries of the same profile
    // ability list. With none present the client rendered the rows as "undefined"; giving each the equipped
    // bow's real ability name/desc/icon fixes that. Name/DescriptionId reversed from en_us_data via Jenkins
    // lookup2 (the same 4209xx "Global.Text" block the traits came from). "Stunning Shot"'s NAME id lives deep
    // in the id space (not cheaply reversible), so it falls back to the generic "Special Attack" label; a few
    // higher-tier descriptions weren't mined and stay 0 (name is what the row shows — desc is only the tooltip).
    private static readonly IReadOnlyDictionary<string, (int NameId, int DescId)> AbilityText = new Dictionary<string, (int, int)>
    {
        ["Barrage"]        = (420256, 420257),
        ["Volley"]         = (420258, 420259),
        ["Icy Arrow"]      = (420384, 420385),
        ["Blizzard Blast"] = (420386, 420387),
        ["Charged Shot"]   = (420567, 420568),
        ["Explosive Shot"] = (420584, 420585),
        ["Multi-Shot"]     = (421006, 421007),
        ["Splitting Arrow"]= (421008, 421009),
        ["Power Shot"]     = (421184, 421185),
        // Name id not cheaply reversible -> generic "Special Attack" fallback. RE-CHECKED 2026-07-25: the ZAM
        // mirror's "Archer's Composite Bow of Stunning" page confirms the pair is genuinely named "Power Shot"
        // / "Stunning Shot" verbatim (no alternate in-game name found) — the id gap is a real reversal gap,
        // not a misidentified ability name.
        ["Stunning Shot"]  = (426588, 421192),
        ["Smoldering Shot"]= (421245, 0),
        ["Flaming Arrow"]  = (421244, 0),
        ["Electric Arrow"] = (421260, 0),
        ["Lightning Call"] = (421272, 0),
        ["Sonic Arrow"]    = (421261, 0),
        ["Sonic Boom"]     = (421273, 0),
        ["Cover Fire"]     = (421284, 0),
        ["Ricochet"]       = (421296, 0),
        ["Ember Arrow"]    = (421285, 0),
        ["Firebomb"]       = (421297, 0),
    };

    private static AbilityExperience? BuildActiveEntry(WeaponAbility ability)
    {
        if (!AbilityText.TryGetValue(ability.Name, out var text))
            return null; // unmapped name (e.g. the bare "Shoot") — skip rather than render "undefined"

        return new AbilityExperience
        {
            Present = text.NameId,       // DISTINCT record id (basic != special names, so no collision)
            IsActivateable = true,       // activatable => shown as an ability row, not a trait
            NameId = text.NameId,
            DescriptionId = text.DescId,
            IconId = ability.IconImageId,
            Level = 1,                   // rank 1 = owned/usable (unlocked)
            RequiredLevel = 0,
        };
    }

    // The full profile ability list for an Archer: the equipped bow's two active abilities (basic +
    // special) followed by the four traits, ending with the Present=0 terminator. Feeds the AbilitiesScreen —
    // which only refreshes on a profile (re)send, so equipping a different bow updates the rows on next relog
    // / job-swap, not instantly (same limitation the Traits panel has).
    public static List<AbilityExperience> BuildProfileAbilityList(int rank, int weaponDefId)
    {
        var list = new List<AbilityExperience>(TraitData.Length + 3);

        // Resolve the equipped bow to its ability pair; ANY unmapped/absent bow (e.g. the starter "Student
        // Archer Bow" 4266, which isn't in the tiered kit) falls back to the tier-1 Barrage/Volley pair so the
        // Attack / Special Attack columns still populate instead of rendering "undefined".
        var weapon = weaponDefId != 0 && ByWeaponDefId.TryGetValue(weaponDefId, out var w) ? w : FallbackWeapon;

        var basic = BuildActiveEntry(weapon.Basic);
        if (basic is not null) list.Add(basic);
        var special = BuildActiveEntry(weapon.Special);
        if (special is not null) list.Add(special);

        AppendTraits(list, rank);
        list.Add(new AbilityExperience { Present = 0 }); // terminator
        return list;
    }

    // Panel display pair for an archer whose equipped bow isn't in the tiered kit — tier-1
    // Barrage/Volley (the starter abilities). Only drives the AbilitiesScreen columns; unmapped bows still
    // FIRE the bare shot in combat (a separate concern — map the bow into ByWeaponDefId to unify them).
    private static readonly ArcherWeapon FallbackWeapon = Volleys(BowIcon, 350, 506);

    // Resolve a client AbilityDefinition request (op36/12) for one of the archer's slot ability-def ids
    // to the equipped bow's real name/icon — this is what fills the AbilitiesScreen's Attack / Special Attack
    // COLUMNS (the op36/13 reply; NOT the Traits section, which is the ability-experience list). An unmapped bow
    // (bare "Shoot", not in AbilityText) falls back to the tier-1 Barrage/Volley name so the column isn't
    // "undefined". Returns null for a def id that isn't one of ours.
    public static (int NameId, int DescId, int IconId)? ResolveDefinition(Player player, int abilityDefId)
    {
        var slot = SlotForDefId(abilityDefId);
        if (slot < 0)
            return null;

        return SlotNameIcon(player.GetEquippedWeaponDefinitionId(), slot);
    }

    // The slot ability-def ids the client requests for the AbilitiesScreen columns (BasicSlotDefId 4895
    // = Attack, SpecialSlotDefId 4899 = Special Attack). -1 if not one of ours.
    public static int SlotForDefId(int abilityDefId) => abilityDefId switch
    {
        BasicSlotDefId => 0,
        SpecialSlotDefId => 1,
        SniperSlotDefId => 2,
        RainSlotDefId => 3,
        _ => -1,
    };

    public const int BasicAbilityDefId = BasicSlotDefId;
    public const int SpecialAbilityDefId = SpecialSlotDefId;

    // The equipped bow's ability entries for its ClientItemDefinition.Abilities list — THIS is what the
    // AbilitiesScreen reads to fill the Attack (slot 0) / Special Attack (slot 1) columns: each entry's Id is the
    // ability the screen looks up in the client's def map (we seed 4895/4899), and IconId is the column icon. An
    // empty list (our bows' default) made the screen ask for def id 0 and render "undefined".
    public static List<ItemDefinition.ItemAbilityEntry> BuildItemAbilityEntries(int weaponDefId)
    {
        var (_, _, basicIcon) = SlotNameIcon(weaponDefId, 0);
        var (_, _, specialIcon) = SlotNameIcon(weaponDefId, 1);
        return new List<ItemDefinition.ItemAbilityEntry>
        {
            new() { Slot = 0, Id = BasicSlotDefId,   IconId = basicIcon },
            new() { Slot = 1, Id = SpecialSlotDefId, IconId = specialIcon },
        };
    }

    // Name+icon for an ability slot on a given equipped bow — the tier-1 Barrage/Volley pair backs any
    // unmapped/absent bow so the AbilitiesScreen column never reads "undefined". Used for BOTH the op36/13 reply
    // and the profile's ability-slot list (they must agree).
    public static (int NameId, int DescId, int IconId) SlotNameIcon(int weaponDefId, int slot)
    {
        var weapon = weaponDefId != 0 && ByWeaponDefId.TryGetValue(weaponDefId, out var w) ? w : FallbackWeapon;
        var ability = slot == 1 ? weapon.Special : weapon.Basic;
        var iconId = ability.IconImageId;

        if (!AbilityText.TryGetValue(ability.Name, out var text))
        {
            var fb = slot == 1 ? FallbackWeapon.Special : FallbackWeapon.Basic;
            AbilityText.TryGetValue(fb.Name, out text);
            iconId = fb.IconImageId;
        }

        return (text.NameId, text.DescId, iconId);
    }

    private const int BasicShotAnim = 1102;   // com_range_attack_02 (draw + fire)
    // SpecialAnim = shared fallback pose (com_range_special_01), still used by the two PARKED level-abilities
    // (Sniper Shot / Rain of Arrows below) which aren't part of the named 10-special family.
    private const int SpecialAnim = 1106;     // com_range_special_01

    // The 10 named specials each get their OWN clip from the client's com_range_special_01..10 animation
    // family (AnimationTypes.xml/AnimationGroups.xml — see the header ANIMATIONS note). Assigned in the same
    // order the 10 per-family builder functions appear below (Volleys..Firebomb).
    private const int SpecialAnim01 = 1106;     // com_range_special_01 -> Volleys    (Barrage/Volley)
    private const int SpecialAnim02 = 1107;     // com_range_special_02 -> Blizzards  (Icy Arrow/Blizzard Blast)
    private const int SpecialAnim03 = 1108;     // com_range_special_03 -> Explosions (Charged Shot/Explosive Shot)
    private const int SpecialAnim04 = 1109;     // com_range_special_04 -> Splintering(Multi-Shot/Splitting Arrow)
    private const int SpecialAnim05 = 1110;     // com_range_special_05 -> Stunning   (Power Shot/Stunning Shot)
    private const int SpecialAnim06 = 1051111;  // com_range_special_06 -> Flame      (Smoldering Shot/Flaming Arrow)
    private const int SpecialAnim07 = 1051112;  // com_range_special_07 -> Lightning  (Electric Arrow/Lightning Call)
    private const int SpecialAnim08 = 1051113;  // com_range_special_08 -> Booming    (Sonic Arrow/Sonic Boom)
    private const int SpecialAnim09 = 1051114;  // com_range_special_09 -> Ricochet   (Cover Fire/Ricochet)
    private const int SpecialAnim10 = 1051115;  // com_range_special_10 -> Firebomb   (Ember Arrow/Firebomb)

    private const int BasicHitFx = 5447;      // PFX_Hit_Wood_vs_Flesh (ActorCompositeEffectDefinitions.xml) —
                                               // a real material-based impact (arrow shaft=wood, target=flesh),
                                               // the same "PFX_Hit_<mat>_vs_<mat>" system NinjaWeaponAbilities.cs
                                               // uses for melee (metal-vs-flesh=5414); replaces the generic
                                               // PFX_Hit_Flash (7) placeholder.

    // Basic-slot icons: the bow FAMILY's own item image (Small IMAGE_ID of the item's image set) —
    // the archer mirror of the ninja melee slot showing the sword.
    private const int BowIcon = 14134;        // set 3104 Archer's Bow
    private const int HorseBowIcon = 14170;   // set 3110 Horse Bow
    private const int CompositeBowIcon = 14176; // set 3111 Composite Bow
    private const int RecurveBowIcon = 14140; // set 3105 Recurve Bow
    private const int RaptorBowIcon = 14146;  // set 3106 Raptor Bow
    private const int MoltenBowIcon = 14152;  // set 3107 Molten Bow (mantis-model epic)

    // Special icons: abil_archer_* Small IMAGE_IDs.
    private const int VolleyIcon = 22801;         // abil_archer_volley (4845)
    private const int FreezingIcon = 22908;       // abil_archer_freezing_arrow (4876)
    private const int ExplosiveIcon = 22798;      // abil_archer_explosive_arrows (4844)
    private const int SplittingIcon = 22914;      // abil_archer_splitting_arrow (4878)
    private const int StunningIcon = 11630;       // abil_archer_stunning_shots (2651)
    private const int FireArrowIcon = 22902;      // abil_archer_fire_arrow (4874)
    private const int LightningIcon = 22911;      // abil_archer_lightning_call (4877)
    private const int ConcussiveIcon = 22899;     // abil_archer_concussive_shot (4873) — Sonic Boom
    private const int RicochetIcon = 22572;       // abil_archer_ricochet (4789)
    private const int FireBombIcon = 22905;       // abil_archer_fire_bomb (4875)

    // Same proven-castable slot ability-def ids the ninja toolbar uses (4895/4899, from the live
    // capture). 4896/4897 are the ids BETWEEN them — the same captured bar's middle slots, used for
    // the two level-ability slots (castability expected but not yet live-proven; first thing to
    // check when the 4-slot bar lands in-game).
    private const int BasicSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;
    private const int SniperSlotDefId = 4896;
    private const int RainSlotDefId = 4897;

    private const int SniperIcon = 22575;     // abil_archer_sniper_shot (4790) Small
    // Rain of Arrows has no modern icon set in the client (only the legacy 2009 set 860 with a
    // tiny type-6 image) — the Volley art (arrows raining down) is the same concept, reuse it.
    private const int RainIcon = VolleyIcon;

    private const int SniperImpactFx = 15384; // PFX_hit-flash_red_head_sniper-shot-land-1
    private const int RainLaunchFx = 1110;    // PFX_arrows_rain_launch (on the caster, arrows go up)
    private const int RainLandFx = 1111;      // PFX_arrows_rain_land_dust (on each victim)
    private const int LevelAbilityEnergyCost = 50; // half the bar each (specials keep the full 100)

    // Sniper Shot: heavy single-target shot, ~1.75× the bow tier's special damage (re-sourced 2026-07-25 from
    // the Scoped Stalker Bow's own damage table — see the header SNIPER SHOT note for the derivation).
    private static WeaponAbility SniperShot(int specialDmg) =>
        new("Sniper Shot", SniperIcon, (int)(specialDmg * 1.75f), SpecialAnim, SniperImpactFx,
            EnergyCost: LevelAbilityEnergyCost);

    // Rain of Arrows: caster-centered arrow rain, ~0.75× the tier's special damage as AoE.
    private static WeaponAbility RainOfArrows(int specialDmg) =>
        new("Rain of Arrows", RainIcon, (int)(specialDmg * 0.75f), SpecialAnim, RainLandFx, RainLaunchFx,
            AoeRadius: 10f, EnergyCost: LevelAbilityEnergyCost);

    public static readonly WeaponAbility BareShot = new("Shoot", BowIcon, 150, BasicShotAnim, BasicHitFx);

    // Damage per tier (wiki anchors marked; the rest interpolate the same curve, basic ≈ 0.55×special).
    //   L1  basic 350*, special 640✓ | L5 basic 855*, special 1554✓ | L8 basic 1150*, special 2100*
    //   L12 basic 1492✓, special 2707✓ | L16 basic 2372✓, special 4750✓
    //
    // The 10 (basic, special) pairs — built per tier below. FX columns:
    //   CastEffectId = the arrow trail / launch on the caster; EffectId = the land/impact on the target.
    // Assemble the 4-slot weapon: the bow's own pair + the tier-scaled level abilities.
    private static ArcherWeapon Make(WeaponAbility basic, WeaponAbility special, int specialDmg) =>
        new(basic, special, SniperShot(specialDmg), RainOfArrows(specialDmg));

    private static ArcherWeapon Volleys(int icon, int basicDmg, int specialDmg) => Make(
        new("Barrage", icon, basicDmg, BasicShotAnim, BasicHitFx, 15489, CastEffectStopMs: 1200), // PRJ_magical_multi-arrow
        // Volley rains arrows AROUND the archer (wiki: "rains arrows down around you striking nearby
        // opponents") — a caster-centered AoE like the ninja's 1000 Storms. Launch 1110 fires the
        // arrows up; the rain-loop 16204 lands at the caster's feet at the end; 1111 dusts each victim.
        new("Volley", VolleyIcon, specialDmg, SpecialAnim01, 1111, 1110, CasterEndEffectId: 16204, AoeRadius: 10f),
        specialDmg);

    private static ArcherWeapon Blizzards(int icon, int basicDmg, int specialDmg) => Make(
        // 16110 is the arrow's PRJ flight trail — a loop that never self-terminates without a
        // projectile to die with (it's the "snow under the player" the user sighted). Tag-played
        // and stopped after the shot window.
        new("Icy Arrow", icon, basicDmg, BasicShotAnim, BasicHitFx, 16110, CastEffectStopMs: 1200),
        new("Blizzard Blast", FreezingIcon, specialDmg, SpecialAnim02, 16116, 16110, CastEffectStopMs: 1200),
        specialDmg);

    private static ArcherWeapon Explosions(int icon, int basicDmg, int specialDmg) => Make(
        new("Charged Shot", icon, basicDmg, BasicShotAnim, BasicHitFx, 15479, CastEffectStopMs: 1200), // PRJ_flaming_orange_arrow
        new("Explosive Shot", ExplosiveIcon, specialDmg, SpecialAnim03, 15373, 15479, CastEffectStopMs: 1200),    // explosive-arrow-land; flaming trail
        specialDmg);

    private static ArcherWeapon Splintering(int icon, int basicDmg, int specialDmg) => Make(
        // Both cast FX are PRJ trails (see the Blizzards note) — tag-played with a timed stop.
        new("Multi-Shot", icon, basicDmg, BasicShotAnim, 5307, 16056, CastEffectStopMs: 1200),
        new("Splitting Arrow", SplittingIcon, specialDmg, SpecialAnim04, 5246, 15488, CastEffectStopMs: 1200),
        specialDmg);

    private static ArcherWeapon Stunning(int icon, int basicDmg, int specialDmg) => Make(
        new("Power Shot", icon, basicDmg, BasicShotAnim, BasicHitFx, 16050, CastEffectStopMs: 1200), // stunning-shot trail
        // 16050 is a PRJ trail (see the Blizzards note) — tag-played with a timed stop.
        new("Stunning Shot", StunningIcon, specialDmg, SpecialAnim05, 16054, 16050, CastEffectStopMs: 1200),
        specialDmg);

    private static ArcherWeapon Flame(int icon, int basicDmg, int specialDmg) => Make(
        new("Smoldering Shot", icon, basicDmg, BasicShotAnim, BasicHitFx, 15479, CastEffectStopMs: 1200), // PRJ_flaming_orange_arrow
        new("Flaming Arrow", FireArrowIcon, specialDmg, SpecialAnim06, 16121, 15479, CastEffectStopMs: 1200),     // PFX_archer_fire-arrow on the victim; flaming trail
        specialDmg);

    private static ArcherWeapon Lightning(int icon, int basicDmg, int specialDmg) => Make(
        new("Electric Arrow", icon, basicDmg, BasicShotAnim, BasicHitFx, 5492, CastEffectStopMs: 1200), // PRJ_lightning_ball_light-blue
        new("Lightning Call", LightningIcon, specialDmg, SpecialAnim07, 16117, 5492, CastEffectStopMs: 1200),    // rooted lightning strike on the victim; lightning trail
        specialDmg);

    private static ArcherWeapon Booming(int icon, int basicDmg, int specialDmg) => Make(
        new("Sonic Arrow", icon, basicDmg, BasicShotAnim, BasicHitFx, 15501, CastEffectStopMs: 1200), // PRJ_beam_gray_trail_arrow
        // RE-CHECKED 2026-07-25: grepped ActorCompositeEffectDefinitions.xml for sonic/boom/shock — still no
        // dedicated archer sonic-boom FX. Near-misses considered and rejected: PFX_orb-explosion_orange_cog_
        // shockwave-yellow/16575 (an explosion, not sonic-styled) and PFX_lightning_blue_aoe_medic-shockpaddles/
        // 16154 (medic defibrillator zap). 5710 (chugawug ground-pound shockwave) remains the closest stand-in
        // (live-probe TODO, same iteration loop the ninja specials went through).
        new("Sonic Boom", ConcussiveIcon, specialDmg, SpecialAnim08, 5710, 15501, CastEffectStopMs: 1200), // beam trail
        specialDmg);

    private static ArcherWeapon Ricochet(int icon, int basicDmg, int specialDmg) => Make(
        new("Cover Fire", icon, basicDmg, BasicShotAnim, BasicHitFx, 16214, CastEffectStopMs: 1200), // ricochet trail
        // 16214 is a PRJ trail (see the Blizzards note) — tag-played with a timed stop.
        new("Ricochet", RicochetIcon, specialDmg, SpecialAnim09, 16215, 16214, CastEffectStopMs: 1200),
        specialDmg);

    private static ArcherWeapon Firebomb(int icon, int basicDmg, int specialDmg) => Make(
        new("Ember Arrow", icon, basicDmg, BasicShotAnim, BasicHitFx, 15479, CastEffectStopMs: 1200), // PRJ_flaming_orange_arrow
        new("Firebomb", FireBombIcon, specialDmg, SpecialAnim10, 16118, 15479, CastEffectStopMs: 1200),           // firebomb MIRV burst on the victim; flaming trail
        specialDmg);

    // weapon def id -> abilities, every retail bow (75000-75029), damage by the bow's level tier.
    public static readonly IReadOnlyDictionary<int, ArcherWeapon> ByWeaponDefId = new Dictionary<int, ArcherWeapon>
    {
        // ── L1 "Archer's Bow" (350 / 640✓; Volley's own curve is 506✓ at L1) ──
        [75000] = Volleys(BowIcon, 350, 506),
        [75001] = Blizzards(BowIcon, 350, 640),

        // ── L5 "Horse Bow" (855 / 1554✓; Volley interpolated 1000*) ──
        [75002] = Volleys(HorseBowIcon, 855, 1000),
        [75003] = Blizzards(HorseBowIcon, 855, 1554),
        [75004] = Explosions(HorseBowIcon, 855, 1554),
        [75005] = Splintering(HorseBowIcon, 855, 1554),

        // ── L8 "Composite Bow" (1150* / 2100*; Volley = 1548✓ at L8) ──
        [75006] = Volleys(CompositeBowIcon, 1150, 1548),
        [75007] = Blizzards(CompositeBowIcon, 1150, 2100),
        [75008] = Explosions(CompositeBowIcon, 1150, 2100),
        [75009] = Splintering(CompositeBowIcon, 1150, 2100),
        [75010] = Stunning(CompositeBowIcon, 1150, 2100),
        [75011] = Flame(CompositeBowIcon, 1150, 2100),

        // ── L12 "Recurve Bow" (1492✓ / 2707✓) ──
        [75012] = Volleys(RecurveBowIcon, 1492, 2707),
        [75013] = Blizzards(RecurveBowIcon, 1492, 2707),
        [75014] = Explosions(RecurveBowIcon, 1492, 2707),
        [75015] = Splintering(RecurveBowIcon, 1492, 2707),
        [75016] = Stunning(RecurveBowIcon, 1492, 2707),
        [75017] = Flame(RecurveBowIcon, 1492, 2707),
        [75018] = Lightning(RecurveBowIcon, 1492, 2707),
        [75019] = Booming(RecurveBowIcon, 1492, 2707),

        // ── L16 "Raptor Bow" (2372✓ / 4750✓) ──
        [75020] = Volleys(RaptorBowIcon, 2372, 4750),
        [75021] = Blizzards(RaptorBowIcon, 2372, 4750),
        [75022] = Explosions(RaptorBowIcon, 2372, 4750),
        [75023] = Splintering(RaptorBowIcon, 2372, 4750),
        [75024] = Stunning(RaptorBowIcon, 2372, 4750),
        [75025] = Flame(RaptorBowIcon, 2372, 4750),
        [75026] = Lightning(RaptorBowIcon, 2372, 4750),
        [75027] = Booming(RaptorBowIcon, 2372, 4750),
        [75028] = Ricochet(RaptorBowIcon, 2372, 4750),
        [75029] = Firebomb(RaptorBowIcon, 2372, 4750),

        // ── MOLTEN BOW (epic leveling bow; three item variants share the model/art) ──
        // RE-SOURCED 2026-07-25 (freerealms.fandom.com search-result snippets — direct WebFetch 402'd on this
        // domain every attempt, so this is via search text, not a verified page read): retail actually shipped
        // THREE distinct Molten Bow SKUs with THREE different ability pairs, not one shared guess:
        //   "Old School" (retired SC-shop item)     -> Barrage / Volley
        //   "New School" (current SC-shop item)     -> Magma Shot / Volcanic Rain (not one of our 10 built
        //                                               kits — no icon/FX sourced for this pair, so not added
        //                                               here; would need its own kit if pursued later)
        //   "Treasure Trader" (retired FB-app item) -> Smoldering Shot / Flaming Arrow
        // Our own ClientItemDefinitions.json: item 9033 and 55362 share the EXACT same NameId/DescriptionId
        // (386326/382762, differing only in MinProfileRank 16 vs 1), while 13655 has a distinct NameId/
        // DescriptionId (31647/6749) — i.e. OUR data treats 9033/55362 as the same named item and 13655 as a
        // different one. That internal grouping is the only evidence available for WHICH id is which SKU (the
        // localized name text itself needs the Global.Text hash reversal, not done here), so: the duplicate-
        // named pair (9033/55362) now gets Barrage/Volley (Old School — using the existing Volleys() kit, a
        // real wiki-confirmed pair instead of a guess), and 13655 keeps Smoldering Shot/Flaming Arrow (Treasure
        // Trader, also wiki-confirmed). Previously all three wrongly shared the Flame kit as an admitted guess.
        [9033] = Volleys(MoltenBowIcon, 2372, 4750),
        [13655] = Flame(MoltenBowIcon, 2372, 4750),
        [55362] = Volleys(MoltenBowIcon, 2372, 4750),
    };

    public static readonly int[] AllWeaponDefIds = ByWeaponDefId.Keys.ToArray();

    public static ArcherWeapon? GetEquippedWeapon(Player player)
    {
        var defId = player.GetEquippedWeaponDefinitionId();
        return defId != 0 && ByWeaponDefId.TryGetValue(defId, out var weapon) ? weapon : null;
    }

    // slot 0 = basic shot, 1 = the bow's special, 2 = Sniper Shot, 3 = Rain of Arrows.
    public static WeaponAbility ResolveAbility(Player player, int slot)
    {
        var weapon = GetEquippedWeapon(player);

        if (weapon is null)
            return BareShot;

        return slot switch
        {
            <= 0 => weapon.Basic,
            1 => weapon.Special,
            2 => weapon.Sniper,
            _ => weapon.Rain,
        };
    }

    // Build the 2-slot ability toolbar from the equipped bow — the exact ninja recipe with the
    // archer's profile id and images.
    public static AbilityPacketSetDefinition BuildToolbar(Player player, IResourceManager resources)
    {
        var weapon = GetEquippedWeapon(player);

        var equippedDefId = player.GetEquippedWeaponDefinitionId();
        var nameId = 0;
        if (resources.ClientItemDefinitions.TryGetValue(equippedDefId, out var weaponDef))
            nameId = weaponDef.NameId;

        if (weapon is null)
        {
            // UNMAPPED weapon on an archer (there are many more bows than the mapped kit — Molten,
            // Jagged, Amateur/Pro, event bows...): give the basic shot instead of a DEAD bar, so
            // every bow at least fires. Nothing equipped at all keeps the empty bar (ninja parity).
            if (equippedDefId == 0)
                return AbilityPacketSetDefinition.CreateEmpty(ArcherProfileId);

            var fallback = new AbilityPacketSetDefinition { ProfileId = ArcherProfileId, SlotCount = 8 };
            fallback.Slots.Add(MakeSlot(BasicSlotDefId, BareShot.IconImageId, nameId, manaCost: 0));
            return fallback;
        }

        var def = new AbilityPacketSetDefinition { ProfileId = ArcherProfileId, SlotCount = 8 };

        def.Slots.Add(MakeSlot(BasicSlotDefId, weapon.Basic.IconImageId, nameId, manaCost: 0));
        // ManaCost drives the client's grey-out below cost; the special keeps the live-decoded
        // full-bar gate. RETAIL GROUND TRUTH (04-01 capture): the bar carries EXACTLY 2 ability
        // slots — even a maxed ninja's set was 2 full + 6 empty. The UI's remaining slots are
        // BATTLE-ITEM slots (wire Type 2 + ItemDefinitionId — health potions etc.), NOT abilities;
        // the Sniper/Rain definitions below stay parked until battle-item slots are implemented.
        def.Slots.Add(MakeSlot(SpecialSlotDefId, weapon.Special.IconImageId, nameId, manaCost: weapon.Special.EnergyCost));

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
