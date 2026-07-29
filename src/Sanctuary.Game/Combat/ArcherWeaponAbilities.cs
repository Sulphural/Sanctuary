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
//
// ── 2026-07-29 PASS: per-real-item data from the OSFR community combat spreadsheet ──────────────────────
// Same treatment MedicWeaponAbilities.cs got: the OLD ByWeaponDefId only covered the 30 canonical
// "Archer's <Family> Bow of <Special>" items (75000-75029) + the 3 Molten Bow SKUs — every OTHER real bow
// item (Shortbow, Recurve Bow, Jagged Bow, the ~60 named unique/reward/coin-shop bows, all their dye-tint
// variants...) fell through GetEquippedWeapon to the generic FallbackWeapon/BareShot, i.e. they all showed
// the SAME tier-1 Barrage/Volley numbers regardless of the real item's actual tier or special. Fixed by
// mining the spreadsheet's weapon-summary tab (one row per real item, "Data Status" CONFIRMED = a real
// tooltip number, PENDING = a real number whose exact ability-NAME VARIANT is uncertain, flagged "(?)") and
// cross-referencing every named row's real Comment string against ClientItemDefinitions.json to resolve its
// actual item id(s) — ~78 additional weapon rows / ~560 additional item ids now carry their own real,
// distinct basic+special damage instead of the shared fallback. The already-mapped 75000-75029 tier's own
// numbers were ALSO corrected against the sheet in this pass: the previous file interpolated a single flat
// basic-damage number per tier (e.g. 1150 for every L8 Composite Bow special); the sheet shows the real
// per-ABILITY basic damage differs even within one tier (e.g. Composite Bow's Icy Arrow 2 hits for 776 but
// its Smoldering Shot hits for 853) - every 75000-75029 entry below now carries its own sheet-sourced number,
// not the old interpolated curve. Rows whose Basic Attack name is a numbered "Barrage N"/"Icy Arrow N"/etc.
// variant get that EXACT variant's own Icon IMAGE_ID from the icons/anim tab's BASIC ATTACKS section
// (VariantIcon() below); rows marked "(?)" (variant genuinely unknown) fall back to the bow FAMILY's own
// item-image icon (the pre-existing, real, ImageSetMappings.txt-sourced convention) rather than guessing a
// numbered variant. "x2" rows (e.g. "222 (x2)") are a real double-hit the WeaponAbility record can't express
// as two ticks on the basic slot, so the two hits are summed into one number (222x2=444), same simplification
// MedicWeaponAbilities.cs uses for Cauterize. A few rows had an item COMMENT with no matching entry in
// ClientItemDefinitions.json at all (this server build doesn't ship that content) — left unmapped, not
// guessed: "Archer's Wild Bow of Forbidden Magic", "Archer's Tentacle Bow of Riptide", "Archer's Feathered
// Bow of Ragnarok", "Archer's Forged Bow of Advantage" (so the Archer's Advantage special has no real item to
// attach to — its factory function is still defined below for documentation, just unused), "Archer's
// Awakened Bow of Forbidden Magic", "Old School Molten Bow" (superseded by the already-known 9033/55362
// items), "Treasure Trader Molten Bow" (superseded by the already-known 13655 item), "New School Molten Bow".
// AoE SCOPE FIX: the sheet's own Scope column marks Explosive Shot ("AoE explosion"), Splitting Arrow
// ("Multi-target"), Firebomb ("AoE fire"), and Lightning Call ("...damage all enemies around you") as
// area-effect, but their WeaponAbility entries had AoeRadius=0 (single-target) before this pass - given a
// real radius now, same as Volley's existing AoE. Ricochet's "Bounces between targets" is a distinct
// mechanic (sequential bounce, not a flat radius) — deliberately NOT given AoeRadius, kept single-target with
// a comment, since the existing combat system has no bounce-chain primitive to express it.
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
        // "Archer's Advantage" (Perfect Shot/Archer's Advantage) has no real item id in this build (see the
        // file header's 2026-07-29 note) - no NameId hunt was done for an unused pair. The novelty-weapon
        // pairs below (Balloon/Beloved/Barber Pole/Archer's Power Bow/New School Scoped Stalker) are all-new
        // names this pass didn't have client access to T4-hash-resolve (unlike MedicWeaponAbilities.cs's
        // novelty pairs, which WERE resolved in an earlier session) - deliberately left OUT of this dict so
        // BuildActiveEntry's unmapped-name path renders them via the tier-1 Barrage/Volley fallback label
        // instead of a fabricated NameId, same honesty convention as every other "unresolved" flag in this
        // file. Their damage numbers and icons are still real (sheet-sourced), only the display NAME/DESC
        // text ids are unresolved.
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
    // Damage corrected 2026-07-29 to the real CONFIRMED L1 number (279) from the "Archer's Bow of Volleys"
    // weapon-summary row — was 350, an old unsourced approximation.
    private static readonly ArcherWeapon FallbackWeapon = Volleys(BowIcon, 279, 506);

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

    // ── PER-WEAPON BASIC-ATTACK ICONS (2026-07-29) ── the icons/anim tab's BASIC ATTACKS section gives many
    // numbered variants (Barrage 2..11, Icy Arrow 2/3, Charged Shot 2, ...) their OWN Icon IMAGE_ID, distinct
    // from the bare ability's - real per-instance art, the same phenomenon MedicWeaponAbilities.cs documents
    // for its Icon4254/Icon3961/etc. constants. Recorded here verbatim (every row from that section, not just
    // the ones this file currently uses, since it's real citable data even where unused). VariantIcon() looks
    // up a weapon-summary row's exact Basic Attack name; rows marked "(?)" (variant genuinely unknown) call it
    // with the bare name instead and fall back to the bow FAMILY's own item-image icon at the call site (the
    // pre-existing, real, ImageSetMappings.txt-sourced convention already used for every "*BowIcon" constant
    // above) rather than guessing a numbered variant.
    private static readonly IReadOnlyDictionary<string, int> BasicAttackVariantIcon = new Dictionary<string, int>
    {
        ["Barrage"] = 22570, ["Archer's Shot"] = 44869, ["Icy Arrow"] = 4131, ["Barrage 2"] = 4131,
        ["Icy Arrow 2"] = 4161, ["Charged Shot"] = 4161, ["Smoldering Shot"] = 4161, ["Multi-Shot"] = 4161,
        ["Power Shot"] = 4161, ["Barrage 3"] = 4161, ["Archer's Shot 2"] = 45325, ["Anniversary Arrow"] = 30987,
        ["Anniversary Arrow 2"] = 30987, ["Archer's Shot 3"] = 45099, ["Barrage 10"] = 14145, ["Barrage 11"] = 14133,
        ["Barrage 4"] = 4155, ["Barrage 5"] = 4137, ["Barrage 6"] = 14175, ["Barrage 7"] = 14169, ["Barrage 8"] = 14145,
        ["Barrage 9"] = 14139, ["Charged Shot 2"] = 4155, ["Electric Arrow"] = 4143, ["Ember Arrow"] = 4143,
        ["Icy Arrow 3"] = 4155, ["Magma Shot"] = 4149, ["Missile-Toe"] = 28450, ["Multi-Shot 2"] = 4155,
        ["Perfect Shot"] = 39645, ["Power Shot 3"] = 4143, ["Power Shot 4"] = 4137, ["Precise Shot"] = 14163,
        ["Shot to the Heart"] = 30194, ["Smoldering Shot 2"] = 4143, ["Smoldering Shot 3"] = 4137,
    };

    // Falls back to the bare "Barrage" icon (not 0/blank) when a variant name has no entry — same
    // don't-ship-blank convention as every other fallback icon in this file.
    private static int VariantIcon(string variantName) =>
        BasicAttackVariantIcon.TryGetValue(variantName, out var id) ? id : BasicAttackVariantIcon["Barrage"];

    // ── NOVELTY-WEAPON ability icons (Balloon/Beloved/Barber Pole/Archer's Power Bow/New School Scoped
    // Stalker) — real Icon IMAGE_IDs from the icons/anim tab's SUPER ATTACKS section, same sourcing as the
    // 10 core specials above.
    private const int PartyCrasherIcon = 291;      // Party Crasher / Party Crasher 2 (both rows use this id)
    private const int HeartBreakerIcon = 30190;    // Heart Breaker - no icon column value; the weapon-summary
                                                    // row's own note flags "icon not visible (probably 30190)"
    private const int CandyHurricaneIcon = 27727;  // Candy Hurricane

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
        // CORRECTED 2026-07-27 (live feedback: "bow of volleys should be like a green effect for the
        // basic") — was 15489 PRJ_magical_multi-arrow (no color); real, name-matched green arrow trail is
        // 15483 PRJ_magical_green_arrow (ActorCompositeEffectDefinitions.xml).
        new("Barrage", icon, basicDmg, BasicShotAnim, BasicHitFx, 15483, CastEffectStopMs: 1200), // PRJ_magical_green_arrow
        // Volley rains arrows AROUND the archer (wiki: "rains arrows down around you striking nearby
        // opponents") — a caster-centered AoE like the ninja's 1000 Storms. Launch 1110 fires the
        // arrows up; the rain-loop 16204 lands at the caster's feet at the end; 1111 dusts each victim.
        // CORRECTED 2026-07-27 (live feedback: "shouldn't last that long.. a ton of arrows coming from the
        // sky, should damage the enemy a few times and then stop after a bit") — 16204 is a "_loop_" asset;
        // it was firing as a one-shot with no stop, so it rained forever while only ever landing ONE hit (a
        // mismatch with the multi-arrow visual). Now 4 hits, 700ms apart (same total damage as before,
        // split across the ticks), with the rain FX tag-held 3s then removed - a few real hits, then it stops.
        new("Volley", VolleyIcon, System.Math.Max(1, specialDmg / 4), SpecialAnim01, 1111, 1110,
            CasterEndEffectId: 16204, AoeRadius: 10f, TickCount: 4, TickIntervalMs: 700, CasterEndEffectStopMs: 3000),
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
        // AoeRadius added 2026-07-29: the sheet's Scope column marks this "AoE explosion" - was single-target.
        new("Explosive Shot", ExplosiveIcon, specialDmg, SpecialAnim03, 15373, 15479, CastEffectStopMs: 1200, AoeRadius: 8f),    // explosive-arrow-land; flaming trail
        specialDmg);

    private static ArcherWeapon Splintering(int icon, int basicDmg, int specialDmg) => Make(
        // Both cast FX are PRJ trails (see the Blizzards note) — tag-played with a timed stop.
        new("Multi-Shot", icon, basicDmg, BasicShotAnim, 5307, 16056, CastEffectStopMs: 1200),
        // AoeRadius added 2026-07-29: the sheet's Scope column marks this "Multi-target" (the arrow splits and
        // hits several opponents) - was single-target.
        new("Splitting Arrow", SplittingIcon, specialDmg, SpecialAnim04, 5246, 15488, CastEffectStopMs: 1200, AoeRadius: 6f),
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
        // AoeRadius added 2026-07-29: the wiki desc says it "calls lightning to damage all enemies AROUND
        // YOU" (caster-centered), matching Volley's own AoE shape - was single-target.
        new("Lightning Call", LightningIcon, specialDmg, SpecialAnim07, 16117, 5492, CastEffectStopMs: 1200, AoeRadius: 8f),    // rooted lightning strike on the victim; lightning trail
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
        // 16214 is a PRJ trail (see the Blizzards note) — tag-played with a timed stop. NOT given an
        // AoeRadius: the sheet's Scope is "Bounces between targets", a sequential bounce-chain mechanic, not a
        // flat-radius splash - the combat system has no bounce-chain primitive to express that, so this stays
        // single-target rather than approximating it as an AoE (which would hit everyone at once, not chain).
        new("Ricochet", RicochetIcon, specialDmg, SpecialAnim09, 16215, 16214, CastEffectStopMs: 1200),
        specialDmg);

    private static ArcherWeapon Firebomb(int icon, int basicDmg, int specialDmg) => Make(
        new("Ember Arrow", icon, basicDmg, BasicShotAnim, BasicHitFx, 15479, CastEffectStopMs: 1200), // PRJ_flaming_orange_arrow
        // AoeRadius added 2026-07-29: the sheet's Scope column marks this "AoE fire" - was single-target.
        new("Firebomb", FireBombIcon, specialDmg, SpecialAnim10, 16118, 15479, CastEffectStopMs: 1200, AoeRadius: 8f),           // firebomb MIRV burst on the victim; flaming trail
        specialDmg);

    // Archer's Advantage (Perfect Shot/Archer's Advantage) - CONFIRMED in the sheet ("The archer unleashes
    // the power of the forge to temper nearby allies with increased movement speed. Nearby opponents are
    // damaged and have reduced movement speed.", Scope = AoE dmg + AoE buff + AoE debuff) but NO matching
    // item exists in ClientItemDefinitions.json ("Archer's Forged Bow of Advantage" isn't content this server
    // build has - see the file header's 2026-07-29 note). Kept as a factory for documentation/future use,
    // same as Medic keeps its own never-called kits; not called anywhere in ByWeaponDefId below. Icon 39861
    // is real but is ALSO the Lucky Shot TRAIT's icon above (TraitData) - the sheet reuses the same art for
    // both, not a mistake on this end. Buff/debuff (+/-30% move speed) aren't modeled — no such mechanic
    // exists in WeaponAbility; only the damage half is representable.
    private static ArcherWeapon Advantage(int icon, int basicDmg, int specialDmg) => Make(
        new("Perfect Shot", icon, basicDmg, BasicShotAnim, BasicHitFx),
        new("Archer's Advantage", 39861, specialDmg, 1051111, BasicHitFx, AoeRadius: 10f),
        specialDmg);

    // ── NOVELTY / VARIABLE-LEVEL WEAPONS (2026-07-29) ── real names/damage/icons from the spreadsheet's
    // weapon-summary tab, same sourcing as the 10 core specials, but each has its own unique ability pair
    // (not a variant of the 10 elemental families) so it's built directly via Make() rather than through a
    // shared per-type factory - the same pattern MedicWeaponAbilities.cs uses for its HeartKit/BalloonKit/
    // PowerFistKit. Display names for these pairs are NOT in AbilityText (no NameId sourced this pass — see
    // the AbilityText dict's own comment), so the AbilitiesScreen columns show the tier-1 Barrage/Volley
    // fallback label for them; the damage numbers and icons below are still real.
    private static readonly ArcherWeapon BalloonKit = new(
        // 3 real SKUs (Reward/Coin/Gifting Pinata) share ONE Comment in our item data ("Balloon Bow", 6 ids
        // covering all of them) - can't split by item id, so every one uses the Reward Version's numbers, the
        // same limitation MedicWeaponAbilities.cs's own BalloonKit flags.
        new("Anniversary Arrow", VariantIcon("Anniversary Arrow"), 2372, BasicShotAnim, BasicHitFx),
        new("Party Crasher", PartyCrasherIcon, 8453, SpecialAnim, BasicHitFx),
        SniperShot(8453), RainOfArrows(8453));

    private static readonly ArcherWeapon BelovedKit = new(
        // Beloved Bow (item 76707).
        new("Shot to the Heart", VariantIcon("Shot to the Heart"), 2372, BasicShotAnim, BasicHitFx),
        new("Heart Breaker", HeartBreakerIcon, 6575, SpecialAnim, BasicHitFx),
        SniperShot(6575), RainOfArrows(6575));

    private static readonly ArcherWeapon BarberPoleKit = new(
        // Barber Pole Bow (item 76557). "Variable" level in the sheet (a leveling reward bow); uses its
        // top-bracket (L16) numbers, same one-representative-number convention MedicWeaponAbilities.cs uses
        // for its own rank-scaled PowerFistKit.
        new("Missile-Toe", VariantIcon("Missile-Toe"), 2372, BasicShotAnim, BasicHitFx),
        new("Candy Hurricane", CandyHurricaneIcon, 8302, SpecialAnim, BasicHitFx),
        SniperShot(8302), RainOfArrows(8302));

    private static readonly ArcherWeapon PowerBowKit = new(
        // Archer's Power Bow (item 78196). "Variable" level, top-bracket (L16) numbers. Neither ability has
        // an Icon IMAGE_ID in either sheet tab (both columns blank) - bow-family icon used as the basic-slot
        // stand-in, RainIcon (shared with Rain of Arrows) reused for Power Rain as the closest thematic fit.
        new("Power Shot 2", BowIcon, 2372, BasicShotAnim, BasicHitFx),
        new("Power Rain", RainIcon, 8302, SpecialAnim, RainLandFx, RainLaunchFx),
        SniperShot(8302), RainOfArrows(8302));

    private static readonly ArcherWeapon ScopedStalkerKit = new(
        // New School Scoped Stalker Bow (item 13656) - distinguished from the "Old School" dye-range group
        // (below, in the static ctor) by its own distinct NameId (31648 vs. the group's shared 17333) in
        // ClientItemDefinitions.json, same evidence MedicWeaponAbilities.cs's Molten Bow note used for its
        // own Old School/New School split. "Variable" level, top-bracket (L16) numbers. Special reuses the
        // existing SniperIcon/SniperImpactFx (same real ability, just the "2" tooltip variant).
        new("Precise Shot", VariantIcon("Precise Shot"), 2609, BasicShotAnim, BasicHitFx),
        new("Sniper Shot", SniperIcon, 8302, SpecialAnim, SniperImpactFx),
        SniperShot(8302), RainOfArrows(8302));

    // weapon def id -> abilities. Real client Archer bows: the canonical 75000-75029 "of <Special>" series +
    // 3 Molten Bow SKUs (below) + ~78 additional named weapon rows / ~560 additional item ids mined from the
    // OSFR community spreadsheet 2026-07-29 (dye/reward-variant ranges are added in the static ctor below,
    // same pattern MedicWeaponAbilities.cs uses for ITS dye ranges - field initializers here run first, then
    // the ctor body appends the ranges, and AllWeaponDefIds is snapshotted last so it picks up everything).
    private static readonly Dictionary<int, ArcherWeapon> _byWeaponDefId = new()
    {
        // ── L1 "Archer's Bow" ──
        [75000] = Volleys(BowIcon, 279, 506), // Barrage 2/Volley 2 [CONFIRMED]
        [75001] = Blizzards(BowIcon, 254, 640), // Icy Arrow/Blizzard Blast [CONFIRMED]

        // ── L5 "Horse Bow" ──
        [75002] = Volleys(HorseBowIcon, 488, 885), // Barrage 4/Volley 2 [CONFIRMED]
        [75003] = Blizzards(HorseBowIcon, 444, 1118), // Icy Arrow 3/Blizzard Blast [CONFIRMED]
        [75004] = Explosions(HorseBowIcon, 444, 1554), // Charged Blast 2(sic)/Explosive Shot [CONFIRMED]
        [75005] = Splintering(HorseBowIcon, 444, 1554), // Multi-Shot 2 (222x2=444)/Splitting Arrow [CONFIRMED]

        // ── L8 "Composite Bow" ──
        [75006] = Volleys(CompositeBowIcon, 853, 1548), // Barrage 3/Volley 2 [CONFIRMED]
        [75007] = Blizzards(CompositeBowIcon, 776, 1955), // Icy Arrow 2/Blizzard Blast [CONFIRMED]
        [75008] = Explosions(CompositeBowIcon, 776, 2716), // Charged Blast(sic)/Explosive Shot [CONFIRMED]
        [75009] = Splintering(CompositeBowIcon, 776, 2716), // Multi-Shot (388x2=776)/Splitting Arrow [CONFIRMED]
        [75010] = Stunning(CompositeBowIcon, 776, 1955), // Power Shot/Stunning Shot [CONFIRMED]
        [75011] = Flame(CompositeBowIcon, 853, 3492), // Smoldering Shot/Flaming Arrow [CONFIRMED]

        // ── L12 "Recurve Bow" ──
        [75012] = Volleys(RecurveBowIcon, 1492, 2707), // Barrage 5/Volley [CONFIRMED]
        [75013] = Blizzards(RecurveBowIcon, 1357, 3420), // Icy Arrow(?)/Blizzard Blast(?) [PENDING]
        [75014] = Explosions(RecurveBowIcon, 1357, 4750), // Charged Shot(?)/Explosive Shot(?) [PENDING]
        [75015] = Splintering(RecurveBowIcon, 1358, 4750), // Multi-Shot 2(?) (679x2=1358)/Splitting Arrow(?) [PENDING]
        [75016] = Stunning(RecurveBowIcon, 1357, 3420), // Power Shot 4/Stunning Shot 2 [CONFIRMED]
        [75017] = Flame(RecurveBowIcon, 1492, 6107), // Smoldering Shot 3/Flaming Arrow [CONFIRMED]
        [75018] = Lightning(RecurveBowIcon, 1357, 4750), // Electric Arrow 2(?)/Lightning Call 2(?) [PENDING]
        [75019] = Booming(RecurveBowIcon, 1357, 4750), // Sonic Arrow(?)/Sonic Boom(?) [PENDING]

        // ── L16 "Raptor Bow" ──
        [75020] = Volleys(RaptorBowIcon, 2609, 4732), // Barrage(?)/Volley(?) [PENDING]
        [75021] = Blizzards(RaptorBowIcon, 2372, 5977), // Icy Arrow(?)/Blizzard Blast(?) [PENDING]
        [75022] = Explosions(RaptorBowIcon, 2372, 8302), // Charged Blast(?)/Explosive Shot(?) [PENDING]
        [75023] = Splintering(RaptorBowIcon, 2372, 8302), // Multi-Shot(2) (1186x2=2372)/Splitting Arrow(?) [PENDING]
        [75024] = Stunning(RaptorBowIcon, 2372, 5977), // Power Shot 3/Stunning Shot 2 [CONFIRMED]
        [75025] = Flame(RaptorBowIcon, 2609, 10674), // Smoldering Shot 2/Flaming Arrow [CONFIRMED]
        [75026] = Lightning(RaptorBowIcon, 2372, 8302), // Electric Arrow/Lightning Call [CONFIRMED]
        [75027] = Booming(RaptorBowIcon, 2372, 8302), // Sonic Arrow/Sonic Boom (named but row still marked PENDING) [PENDING]
        [75028] = Ricochet(RaptorBowIcon, 2372, 10674), // Cover Fire/Ricochet [PENDING]
        [75029] = Firebomb(RaptorBowIcon, 2372, 2324), // Ember Arrow/Firebomb (special LOWER than basic - real sheet number, kept as-is) [CONFIRMED]

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
        // named pair (9033/55362) gets Barrage/Volley (Old School), and 13655 keeps Smoldering Shot/Flaming
        // Arrow (Treasure Trader). Damage numbers corrected 2026-07-29 to the sheet's "Old School Scoped
        // Stalker Bow"-tier row (3391/6143, the same oddly-high-for-its-listed-level value the sheet gives
        // several other L1 "premium" bows — kept as-is, it's what the sheet says) and the "Treasure Trader
        // Molten Bow" row (2609/10674) respectively — were the flat Raptor-tier numbers (2372/4750) before.
        [9033] = Volleys(MoltenBowIcon, 3391, 6143), // Old School Molten Bow - Barrage(?)/Volley(?) [PENDING]
        [13655] = Flame(MoltenBowIcon, 2609, 10674), // Treasure Trader Molten Bow - Smoldering Shot(?)/Flaming Arrow(?) [PENDING]
        [55362] = Volleys(MoltenBowIcon, 3391, 6143), // Old School Molten Bow (2nd id) - Barrage(?)/Volley(?) [PENDING]

        // ── L1 oddball high-value bows (real sheet numbers; unusually high damage despite a "Level 1" tag) ──
        [4287] = Volleys(VariantIcon("Barrage 2"), 3391, 6143), // Briarsting Bow - Barrage 2/Volley [CONFIRMED]
        [49611] = Volleys(MoltenBowIcon, 3391, 6143), // Coin Flow Mantis Bow - Barrage(?)/Volley(?) [PENDING]
        [49381] = Volleys(MoltenBowIcon, 3391, 6143), [49601] = Volleys(MoltenBowIcon, 3391, 6143), // Star Flow Mantis Bow (2 ids) - Barrage(?)/Volley(?) [PENDING]

        // ── L1 normal-value bows ──
        [6958] = Volleys(HorseBowIcon, 279, 506), // Amateur Archer Bow - Barrage(?)/Volley(?) [PENDING]
        [4266] = Volleys(VariantIcon("Barrage 11"), 279, 506), [29952] = Volleys(VariantIcon("Barrage 11"), 279, 506), // Student Archer Bow (2 ids, also the tutorial starter bow) - Barrage 11/Volley [CONFIRMED]
        [48160] = Volleys(BowIcon, 2609, 4732), // Butterfly Bow - Barrage(?)/Volley(?) [PENDING]
        [37140] = Volleys(BowIcon, 488, 885), // Forest Branch - Barrage(?)/Volley(?) [PENDING]
        [48166] = Volleys(BowIcon, 2609, 4732), // Monarch Bow - Barrage(?)/Volley(?) [PENDING]
        [22114] = Volleys(BowIcon, 488, 885), // Nature's Branch - Barrage(?)/Volley(?) [PENDING]

        // ── L4/L5 tier ──
        [22113] = Volleys(VariantIcon("Barrage 7"), 853, 1548), // Bubbleburst Bow - Barrage 7/Volley [CONFIRMED]
        [37194] = Volleys(HorseBowIcon, 853, 1548), // Soapy Bow - Barrage(?)/Volley(?) [PENDING]
        [48184] = Volleys(HorseBowIcon, 853, 1548), // Confetti Bow - Barrage(?)/Volley(?) [PENDING]
        [48178] = Volleys(VariantIcon("Barrage 7"), 853, 1548), // Gemstone Bow - Barrage 7/Volley [CONFIRMED]
        [48190] = Volleys(VariantIcon("Barrage 7"), 853, 1548), // Party Bow - Barrage 7/Volley [CONFIRMED]
        [48202] = Volleys(HorseBowIcon, 853, 1548), // Sunlit Bow - Barrage(?)/Volley(?) [PENDING]

        // ── L8 tier ──
        [6864] = Volleys(CompositeBowIcon, 853, 1548), // Pro Archer Bow - Barrage/Volley [CONFIRMED]
        [22217] = Volleys(CompositeBowIcon, 1492, 2707), // Tidal Bow - Barrage(?)/Volley(?) [PENDING]
        [22216] = Volleys(CompositeBowIcon, 1492, 2707), // Venom's Sting - Barrage(?)/Volley(?) [PENDING]

        // ── L12/L13 tier ──
        [4300] = Volleys(RecurveBowIcon, 2609, 4732), [30191] = Volleys(RecurveBowIcon, 2609, 4732), // All-Star Archer Bow (2 ids) - Barrage/Volley [CONFIRMED]
        [37229] = Volleys(CompositeBowIcon, 2609, 4732), // Aqua Bow - Barrage(?)/Volley(?) [PENDING]
        [22219] = Volleys(RecurveBowIcon, 2609, 4732), // Blazing Bow - Barrage(?)/Volley(?) [PENDING]
        [27940] = Volleys(VariantIcon("Barrage 9"), 2609, 4732), [48111] = Volleys(VariantIcon("Barrage 9"), 2609, 4732), // Frostflame Bow (2 ids) - Barrage 9/Volley [CONFIRMED]
        [37211] = Volleys(CompositeBowIcon, 2609, 4732), // Toxic Sting - Barrage(?)/Volley(?) [PENDING]
        [48220] = Volleys(VariantIcon("Barrage 6"), 2609, 4732), // Batty Bow - Barrage 6/Volley [CONFIRMED]
        [48232] = Volleys(CompositeBowIcon, 2609, 4732), // Rainbow Bow - Barrage(?)/Volley(?) [PENDING]
        [48226] = Volleys(CompositeBowIcon, 2609, 4732), // Winged Bow - Barrage(?)/Volley(?) [PENDING]

        // ── L16 tier ──
        [37238] = Volleys(RecurveBowIcon, 2609, 4732), // Fiery Bow - Barrage(?)/Volley(?) [PENDING]
        [27934] = Volleys(RaptorBowIcon, 2609, 4732), [48114] = Volleys(RaptorBowIcon, 2609, 4732), // Glacial Bow (2 ids) - Barrage(?)/Volley(?) [PENDING]
        [29935] = Volleys(RaptorBowIcon, 2609, 4732), [48113] = Volleys(RaptorBowIcon, 2609, 4732), // Luminous Bow (2 ids) - Barrage(?)/Volley(?) [PENDING]
        [23024] = Volleys(RaptorBowIcon, 2609, 4732), [48116] = Volleys(RaptorBowIcon, 2609, 4732), // Smokey Bow (2 ids) - Barrage(?)/Volley(?) [PENDING]

        // ── L20 tier ──
        [48321] = Volleys(VariantIcon("Barrage 8"), 2609, 4732), // Bullseye's Blasting Bow - Barrage 8/Volley 2 [CONFIRMED]
        [55819] = Volleys(RaptorBowIcon, 2609, 4732), // Magical Essence Bow - Barrage(?)/Volley(?) [PENDING]

        // ── Novelty / variable-level weapons (own unique ability pairs — see the kits above) ──
        [76707] = BelovedKit,      // Beloved Bow
        [78196] = PowerBowKit,     // Archer's Power Bow
        [76557] = BarberPoleKit,   // Barber Pole Bow
        [13656] = ScopedStalkerKit, // New School Scoped Stalker Bow
    };

    // Large dye/reward-variant id ranges — real items, one sheet row covers every color, so every variant in
    // a group maps to the same kit (dye doesn't change stats). Field initializers above run before this ctor
    // body, so AllWeaponDefIds (snapshotted at the end) picks these up too — same structure
    // MedicWeaponAbilities.cs uses for its own dye ranges.
    static ArcherWeaponAbilities()
    {
        // Quick Shot Bow (10 dye/reward variants) - Barrage/Volley [CONFIRMED]
        foreach (var id in new[] { 30250,30251,30252,30253,30254,30255,30256,30257,30258,30259 })
            _byWeaponDefId[id] = Volleys(BowIcon, 279, 506);

        // Shortbow (66 dye/reward variants) - Barrage 11/Volley [CONFIRMED]
        foreach (var id in new[] { 4254,4255,4256,4257,4258,4261,4262,4263,4264,4265,4267,4268,4269,4270,4271,4272,4273,4274,4275,4276,4277,4278,4279,4280,4281,4282,4283,4284,4285,4286,4288,29950,29951,29953,29954,29955,29956,29957,29958,29959,37125,37126,37127,37128,37130,37131,37133,37134,37136,37137,37139,37141,37143,37144,37146,37147,37148,37150,37151,37153,37154,37155,37157,37158,37159,37161 })
            _byWeaponDefId[id] = Volleys(VariantIcon("Barrage 11"), 279, 506);

        // Swift Sting Bow (10 dye/reward variants) - Barrage/Volley [CONFIRMED]
        foreach (var id in new[] { 37129,37132,37135,37138,37142,37145,37149,37152,37156,37160 })
            _byWeaponDefId[id] = Volleys(BowIcon, 488, 885);

        // Old School Scoped Stalker Bow (51 dye/reward variants) - Barrage(?)/Volley(?); item image set 3109
        // has no known flat icon in our data, bare-Barrage stand-in used instead [PENDING]
        foreach (var id in new[] { 7627,7628,7629,7630,7631,7632,7633,7634,7635,7636,7637,7638,7639,7640,7641,7642,7643,7644,7645,7646,7647,7648,7649,7650,7651,7652,7653,7654,7655,7656,7657,7658,7659,7660,7661,37313,37315,37317,37319,37320,37322,37324,37326,37328,37331,37332,37334,37337,37339,37341,37342 })
            _byWeaponDefId[id] = Volleys(VariantIcon("Barrage"), 3391, 6143);

        // Fast Blast Bow (10 dye/reward variants) - Barrage(?)/Volley(?) [PENDING]
        foreach (var id in new[] { 30310,30311,30312,30313,30314,30315,30316,30317,30318,30319 })
            _byWeaponDefId[id] = Volleys(HorseBowIcon, 488, 885);

        // Rapid Shot Bow (10 dye/reward variants) - Barrage(?)/Volley(?) [PENDING]
        foreach (var id in new[] { 37166,37169,37172,37175,37178,37181,37185,37188,37192,37197 })
            _byWeaponDefId[id] = Volleys(HorseBowIcon, 853, 1548);

        // Recurve Bow (68 dye/reward variants) - Barrage(?)/Volley(?) - plain "Recurve Bow" (L4), distinct
        // from the "Archer's Recurve Bow of <Special>" L12 series above [PENDING]
        foreach (var id in new[] { 6930,6931,6932,6933,6934,6937,6938,6939,6940,6941,6942,6943,6944,6945,6946,6947,6948,6949,6950,6951,6952,6953,6954,6955,6956,6957,6959,6960,6961,6962,6963,6964,30010,30011,30012,30013,30014,30015,30016,30017,30018,30019,37162,37163,37164,37165,37167,37168,37170,37171,37173,37174,37176,37177,37179,37180,37182,37183,37184,37186,37187,37189,37190,37191,37193,37195,37196,37198 })
            _byWeaponDefId[id] = Volleys(HorseBowIcon, 488, 885);

        // Focus Fire Bow (10 dye/reward variants) - Barrage/Volley [CONFIRMED]
        foreach (var id in new[] { 30370,30371,30372,30373,30374,30375,30376,30377,30378,30379 })
            _byWeaponDefId[id] = Volleys(CompositeBowIcon, 1492, 2707);

        // Laminated Bow (69 dye/reward variants) - Barrage(?)/Volley(?) [PENDING]
        foreach (var id in new[] { 6965,6966,6967,6968,6969,6972,6973,6974,6975,6976,6977,6978,6979,6980,6981,6982,6983,6984,6985,6986,6987,6988,6989,6990,6991,6992,6993,6994,6995,6996,6997,6998,6999,30070,30071,30072,30073,30074,30075,30076,30077,30078,30079,37199,37200,37201,37202,37204,37205,37207,37208,37210,37212,37214,37215,37217,37218,37220,37221,37222,37224,37225,37227,37228,37230,37232,37233,37234,37236 })
            _byWeaponDefId[id] = Volleys(CompositeBowIcon, 488, 885);

        // Curved Bow (68 dye/reward variants) - Barrage(?)/Volley(?) - plain "Curved Bow" (L12), distinct
        // from "Archer's Recurve Bow of <Special>" above [PENDING]
        foreach (var id in new[] { 6860,6861,6862,6863,6867,6868,6869,6870,6871,6872,6873,6874,6875,6876,6877,6878,6879,6880,6881,6882,6883,6884,6885,6886,6887,6888,6889,6890,6891,6892,6893,6894,30130,30131,30132,30133,30134,30135,30136,30137,30138,30139,37237,37239,37240,37241,37243,37244,37246,37247,37249,37250,37252,37253,37255,37256,37258,37259,37260,37262,37263,37266,37267,37268,37270,37271,37272,37274 })
            _byWeaponDefId[id] = Volleys(RecurveBowIcon, 1492, 2707);

        // Savage Sting Bow (10 dye/reward variants) - Barrage(?)/Volley(?) [PENDING]
        foreach (var id in new[] { 30430,30431,30432,30433,30434,30435,30436,30437,30438,30439 })
            _byWeaponDefId[id] = Volleys(RecurveBowIcon, 2609, 4732);

        // Storm Shot Bow (10 dye/reward variants) - Barrage(?)/Volley(?) [PENDING]
        foreach (var id in new[] { 37203,37206,37209,37213,37216,37219,37223,37226,37231,37235 })
            _byWeaponDefId[id] = Volleys(CompositeBowIcon, 1492, 2707);

        // Brutal Blast Bow (10 dye/reward variants) - Barrage/Volley [CONFIRMED]
        foreach (var id in new[] { 30490,30491,30492,30493,30494,30495,30496,30497,30498,30499 })
            _byWeaponDefId[id] = Volleys(RaptorBowIcon, 2609, 4732);

        // Feral Fire Bow (10 dye/reward variants) - Barrage(?)/Volley(?) [PENDING]
        foreach (var id in new[] { 37242,37245,37248,37251,37254,37257,37261,37265,37269,37273 })
            _byWeaponDefId[id] = Volleys(RecurveBowIcon, 2609, 4732);

        // Jagged Bow (67 dye/reward variants) - Barrage 10/Volley [CONFIRMED]
        foreach (var id in new[] { 4289,4290,4291,4292,4293,4294,4295,4296,4297,4298,4299,4301,4303,4304,4305,4306,4307,4308,4309,4310,4311,4312,4313,4314,4315,4316,4317,4318,4319,4320,4321,4322,4323,30190,30192,30193,30194,30195,30196,30197,30198,30199,37275,37276,37277,37278,37280,37281,37283,37284,37286,37288,37290,37293,37294,37296,37297,37298,37300,37301,37303,37304,37305,37307,37308,37309,37311 })
            _byWeaponDefId[id] = Volleys(VariantIcon("Barrage 10"), 2609, 4732);

        // Trick Shot Bow (10 dye/reward variants) - Barrage(?)/Volley(?) [PENDING]
        foreach (var id in new[] { 37279,37282,37285,37289,37292,37295,37299,37302,37306,37310 })
            _byWeaponDefId[id] = Volleys(RaptorBowIcon, 2609, 4732);

        // Balloon Bow (6 ids: Reward/Coin/Gifting Pinata variants share one Comment in our data) - see the
        // BalloonKit comment above for why every id uses the Reward Version's numbers.
        foreach (var id in new[] { 16340, 16341, 16342, 16343, 16344, 77444 })
            _byWeaponDefId[id] = BalloonKit;

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, ArcherWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

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
