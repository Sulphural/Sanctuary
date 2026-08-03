using System;
using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// MEDIC (profile 11) — scalpel/bonesaw/megasaw/shockrod, a healing-support melee job. Weapon-driven like the
// warrior/brawler kits: the equipped "Medic's <Weapon> of <Special>" grants a MELEE (slot 0) + a named SPECIAL
// (slot 1). Real client data mined 2026-07-28 from src/Resources/ClientItemDefinitions.json (item ids, weapon
// tiers/ranks, real per-item NameId) + client AnimationGroups.xml/ActorCompositeEffectDefinitions.xml found in
// a sibling repo checkout + freerealms.fandom.com search snippets (direct WebFetch to the domain returns
// HTTP 402 in this environment - blocked at the proxy level, tried the page, its MediaWiki API, and an
// archive.org mirror). See the per-kit citations below for exactly what's confirmed vs. flagged.
//
// ANIM: client AnimationGroups.xml gives Medic a DEDICATED wieldType (14, shared by all 3 weapon classes
//   20/27/28 - unlike Warrior, which infers 1h/2h/fist purely from weapon model shape). The wieldType-14 basic
//   swing dispatcher (com_swing, group 1099) alternates BOTH com_1hs_attack(1020) and com_2hs_attack(1040) at
//   equal weight for every weapon tier - a real, citable mechanic difference from Warrior's per-model pick.
//   Simplified here to a fixed 1020 for the melee slot (can't express true per-swing alternation in one
//   WeaponAbility.Animation field) - a citable simplification, not an invented number. No wieldType-gated
//   special-anim dispatcher exists in the file (same gap Warrior's specials had); special anims below just
//   alternate 1020/1040 as a placeholder, UNCONFIRMED - flagged per kit.
// FX: 7 of 10 specials resolved to a real, exact-name-match composite in ActorCompositeEffectDefinitions.xml
//   (Shock/Vitamins/Immunization/Antibodies/Reflexes/Lasers/Triage - Triage was on a stale pre-spreadsheet
//   thematic guess, the "medic-bloodcell trio", until 2026-07-29 when it was corrected to its own real
//   sourced FX per the spreadsheet's icons/anim tab); the remaining 3 (Vitals/Alarm/Vitae) found no matching
//   composite at all and fall back to the generic hit-flash, same honesty convention as Warrior's unconfirmed
//   kits. LINGERING FX: Antibodies' cast FX (16225) was a "_loop_" asset played bare with no stop mechanism
//   and no bounded lifetime of its own - confirmed root cause of live feedback "medic has some effects still
//   on the character for a really long time" (2026-07-29) - fixed with an explicit CastEffectStopMs, same
//   fix class as the potion heal-shower/PowerupSystem.HealShowerMs bugs from earlier in the same session.
//   ANIM: all 10
//   specials now use real, distinct com_1hs/2hs_special_NN animation ids (re-checked 2026-07-28 against
//   AnimationGroups.xml), not a placeholder reuse of the 2 melee swing anims. DAMAGE: 4 of 10 specials have a
//   real number from a freerealms.fandom.com weapon-item page (via search snippet, not a raw page fetch - the
//   domain is blocked here, confirmed blocked on both the index and individual sub-pages); the rest are ours to
//   tune, scaled to sit in the same range as the confirmed ones - re-searched 2026-07-28 and still unreachable,
//   not simply under-searched.
// ICONS/NAMES: mostly real now (found 2026-07-28) — the earlier "no local ImageSets.txt/ImageSetMappings.txt"
//   claim was wrong, they live directly under the client install (`Client/Resources/Images/`), just nowhere in
//   this REPO's own Resources (unlike anim/FX xml, which do have a sibling-repo copy). Ability/trait NameId/
//   DescId are REAL, resolved via the client's own Global.Text T4 hash against en_us_data.dat (see
//   reference_t4_localization_hash) - the trait descriptions needed a second pass: the client's actual tooltip
//   text isn't the wiki's summary verbatim (it has a trailing "Next rank: ..." clause and says "opponent" where
//   the wiki paraphrased "enemy"), so the first exact-match attempt against the wiki's own wording came back
//   empty. Icons: exact-name abil_medic_* sets exist for 4 abilities (firstaid/target_vitals/shockpaddles/
//   vaccination); 3 more use a real set with no exact name match but a strong thematic fit (refresh->Vitamins,
//   musclerelaxant->Reflexes, well_being->Vitae), flagged as such, same honesty convention as Triage's FX
//   reassignment below. The rest (7 of 10 special icons, plus every trait/skill's underlying weapon-item icon)
//   are still 0/blank - no set name fits them at all. The WEAPON item's own real NameId (used for the toolbar
//   label, same as every other job) IS real, mined directly from ClientItemDefinitions.json.
// TRAITS: real, from freerealms.fandom.com's Medic page (user-provided screenshot 2026-07-28) — ONE list of 4
//   level-gated unlocks, not two. (An earlier pass misread the wiki page as documenting two separate systems -
//   a "Skills" table with mechanical numbers and a "Traits" box with flavor names/levels 5/10/15/20 - because
//   the names/levels genuinely differ between the two tables. They're the same 4 unlocks; the wiki just
//   presents them twice, once as a mechanics table and once as flavor-text boxes. The Skills table's names/
//   levels/numbers are used here as canonical since they carry real numbers.) Real full rank-1..5 progressions
//   were found alongside the descriptions (not modeled - no skill-rank-up system exists - but recorded in the
//   TraitData comment below for whenever one does). Shock Paddles' real in-game tutorial dialogue also reveals
//   a REVIVE-allies purpose alongside the damage, not modeled here (damage-only) - a genuine gap this pass
//   surfaced, not previously known.
public sealed record MedicWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class MedicWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int MedicProfileId = 11;

    // wieldType 14's swing dispatcher alternates 1hs/2hs for every Medic weapon regardless of model shape;
    // 1020 (1hs) picked as the single representative basic-attack anim. See file header for the real mechanic
    // this simplifies.
    private const int MeleeAnim = 1020;        // com_1hs_attack
    private const int MeleeAnim2h = 1040;       // com_2hs_attack (alternated into specials below, unconfirmed)
    private const int MeleeHitFx = 7;           // PFX_Hit_Flash — generic impact flash (fallback for unconfirmed kits).

    // No dedicated icon exists for any of the 10 real melee names (Incision/Injure/Assist/Bruise/Wound/Sedate/
    // Cauterize/Heartbreak/Clear!/Traumatize) - same gap Warrior's file has for its own basic-attack slot,
    // handled there by reusing one of its own real special icons as a stand-in rather than shipping blank.
    // Same fix here: reuse the Triage icon (Medic's most central/iconic special) instead of leaving 0/blank.
    private const int MeleeIcon = 22591;        // abil_medic_triage, type 6 (stand-in, see above)

    private const int MeleeSlotDefId = 4895;
    private const int SpecialSlotDefId = 4899;

    public static readonly WeaponAbility BareMelee = new("Assist", MeleeIcon, 200, MeleeAnim, MeleeHitFx);

    // ── TRAITS (the real 4 — see file header) ── real NameIds (T4-hash resolved against en_us_data.dat), real
    // levels (1/5/10/15), real level-1 numbers. None of the 4 mention "critical hit" as a TRIGGER (only
    // Vitamins mentions crit at all, and it GRANTS a crit boost rather than consuming an existing one) - so
    // unlike Warrior/Archer/Wizard/Brawler, Medic has no baseline "unlocks crit chance" trait; the 4 below are
    // wired as: periodic tick (First Aid), always-on flat bonus (Target Vitals), periodic self-buff
    // (Vitamins), and an always-on splash bonus (Shock Paddles) - none crit-gated. Trigger CADENCE (First
    // Aid/Vitamins are periodic, not proc-based) is our interpretation where the wiki gives no trigger wording
    // at all; the EFFECT each one has is real.
    public const int FirstAidLevel = 1;
    public const int TargetVitalsLevel = 5;
    public const int VitaminsLevel = 10;
    public const int ShockPaddlesLevel = 15;

    private const int FirstAidNameId = 3784;
    private const int TargetVitalsNameId = 3810;
    private const int VitaminsNameId = 24061;
    private const int ShockPaddlesNameId = 3800;

    // First Aid: "Heals yourself and any ally near you for 250 health." Periodic tick (Player.MedicFirstAidTick,
    // riding the existing per-second RegenTick cadence). Interval/radius are ours to tune, not wiki-specified.
    public const int FirstAidHealAmount = 250;
    public const int FirstAidHealRadius = 15;
    public const int FirstAidTickIntervalMs = 10000;

    // Target Vitals: "...strike at the vital points of your enemy inflicting 133 damage." Always-on flat
    // addition to every hit once unlocked.
    public const int TargetVitalsBonusDamage = 133;

    // Vitamins: "...increasing critical hit chance by 1% and increasing critical hit damage by 10% for 5
    // seconds." Wired as a periodic self-buff (same tick cadence as First Aid) via the existing CombatBuffs
    // temporary damage-multiplier registry. Only the damage-% half is modeled (CombatBuffs has no crit-CHANCE
    // buff registry) - the +1% crit chance is not applied, documented simplification not silently dropped.
    public const float VitaminsCritDamageBonusPercent = 0.10f;
    public const int VitaminsDurationMs = 5000;

    // Shock Paddles: "Causes 50 damage to an enemy and two other enemies near that enemy." Always-on splash
    // bonus applied alongside every hit (like Target Vitals, just with 2 extra nearby hostiles hit too).
    public const int ShockPaddlesBonusDamage = 50;
    public const int ShockPaddlesExtraTargets = 2;
    public const float ShockPaddlesSplashRadius = 10f;

    // Shock Paddles' REAL second purpose — the client's own tutorial dialogue (Docaloc's questline) says
    // "...shocking your opponents, and reviving your allies", not just damage. This was a documented, known
    // gap (flagged but not modeled) until now: wired 2026-07-29 as a TRAIT-gated periodic tick on
    // Player.MedicSkillsTick (same pattern as First Aid/Vitamins — Shock Paddles is a job trait unlocked at
    // L15, not a per-weapon special, so it must work regardless of which weapon/special the Medic has
    // equipped; an earlier attempt wrongly gated this on casting a weapon special literally named "Shock
    // Paddles", which most L15+ Medics would never trigger). Revives any nearby downed ally to full health,
    // or tops up a nearby wounded one, on the same ~10s cadence as First Aid. The MECHANIC is real/sourced;
    // the radius and heal amount have no wiki/dialogue number given, so they're ours to tune (radius matches
    // the splash radius above for consistency; heal amount picked to feel meaningful without trivializing
    // death). The on-hit COMBAT splash half (ShockPaddlesBonusDamage above) is unrelated and unchanged.
    public const float ShockPaddlesReviveRadius = 15f;
    public const int ShockPaddlesAllyHealAmount = 500;

    // DescIds are REAL now too (resolved 2026-07-28) — the first exact-match attempt failed because the
    // client's actual tooltip text isn't the wiki's summary: it has a trailing "Next rank: ..." clause the
    // wiki didn't show, and Shock Paddles says "opponent" where the wiki paraphrased "enemy". Full real
    // rank-1..5 progressions found alongside these (not modeled - no skill-rank-up system - but recorded here
    // for whenever one exists): First Aid heal 250/281/313/344/375; Target Vitals damage 133/150/167/183/200;
    // Vitamins crit% /crit-dmg% /duration 1%/10%/5s -> 2%/20%/6s -> 3%/30%/7s -> 4%/40%/8s -> 5%/50%/9s; Shock
    // Paddles damage 50/56/63/69/75. Shock Paddles' real in-game tutorial dialogue (Docaloc's questline) also
    // says it has a REVIVE-allies purpose alongside the damage ("shocking your opponents, and reviving your
    // allies") - not modeled here (damage-only), a real gap this data surfaced, not previously known.
    // Icons: found after all — the earlier "no local ImageSets.txt/ImageSetMappings.txt" claim was wrong, they
    // live directly under the client install (`Client/Resources/Images/`), just nowhere in this repo's own
    // Resources. Real exact-name-match sets exist for 3 of 4: abil_medic_firstaid (887), abil_medic_
    // target_vitals (2654), abil_medic_shockpaddles (890) — resolved via ImageSetMappings.txt, TYPE 6 (64px)
    // for trait-panel icons per the established technique (type 5/32px is for ability-bar icons instead, see
    // the weapon Specials below). No "vitamins"-named set exists — IconId stays 0 for that one only.
    private const int FirstAidDescId = 3813;
    private const int TargetVitalsDescId = 24022;
    private const int VitaminsDescId = 24062;
    private const int ShockPaddlesDescId = 90388;

    private const int FirstAidTraitIcon = 261;      // abil_medic_firstaid, type 6
    private const int TargetVitalsTraitIcon = 11640; // abil_medic_target_vitals, type 6
    private const int ShockPaddlesTraitIcon = 270;   // abil_medic_shockpaddles, type 6
    private const int VitaminsTraitIcon = 22960;     // abil_medic_vitamins, type 6 (exact match, found 2026-07-29)

    private static readonly JobTraits.Trait[] TraitData =
    [
        new(FirstAidNameId, FirstAidDescId, FirstAidTraitIcon, FirstAidLevel),
        new(TargetVitalsNameId, TargetVitalsDescId, TargetVitalsTraitIcon, TargetVitalsLevel),
        new(VitaminsNameId, VitaminsDescId, VitaminsTraitIcon, VitaminsLevel),
        new(ShockPaddlesNameId, ShockPaddlesDescId, ShockPaddlesTraitIcon, ShockPaddlesLevel),
    ];

    public static List<AbilityExperience> BuildTraitEntries(int rank) => JobTraits.Build(TraitData, rank, MedicProfileId);

    public static bool HasTrait(Player player, int traitLevel) =>
        player.ActiveProfileId == MedicProfileId && player.ActiveProfile.Rank >= traitLevel;

    // Target Vitals (L5) adds a flat bonus to every hit; Shock Paddles (L15) splashes onto up to 2 other
    // nearby hostiles alongside every hit. Both always-on once unlocked - neither wiki entry mentions a
    // crit/proc condition (Medic has no baseline crit-chance trait at all). First Aid/Vitamins (the other 2
    // real traits) are periodic and live on Player.MedicSkillsTick instead.
    public static int ApplyTraitDamage(Player player, int baseDamage, Npc? target)
    {
        var dmg = baseDamage;

        if (HasTrait(player, TargetVitalsLevel))
            dmg += TargetVitalsBonusDamage;

        if (target is not null && HasTrait(player, ShockPaddlesLevel))
            SplashShockPaddles(player, target);

        return Math.Max(1, dmg);
    }

    // Shock Paddles splash: up to 2 other live hostiles within range of the crit's own target, each taking
    // the skill's flat bonus damage. Reuses the same nearby-hostile pattern the multishot archer kit uses.
    private static void SplashShockPaddles(Player player, Npc primaryTarget)
    {
        if (player.Zone is not { } zone)
            return;

        var tp = primaryTarget.Position;
        var extras = zone.Npcs
            .Where(n => !ReferenceEquals(n, primaryTarget) && n.IsHostile && n.IsDamageable && n.IsAlive)
            .Select(n =>
            {
                var dx = n.Position.X - tp.X;
                var dz = n.Position.Z - tp.Z;
                return (npc: n, d2: dx * dx + dz * dz);
            })
            .Where(t => t.d2 <= ShockPaddlesSplashRadius * ShockPaddlesSplashRadius)
            .OrderBy(t => t.d2)
            .Take(ShockPaddlesExtraTargets)
            .Select(t => t.npc);

        foreach (var extra in extras)
        {
            var killedExtra = extra.ApplyDamage(ShockPaddlesBonusDamage);
            player.SendTunneledToVisible(new PlayerUpdatePacketHitPointModification
            {
                Guid = player.Guid,
                Guid2 = extra.Guid,
                Unknown = true,
                Unknown2 = extra.MaxHealth,
                Unknown3 = extra.Health,
                Unknown4 = -ShockPaddlesBonusDamage,
            }, sendToSelf: true);

            if (killedExtra)
                player.Zone.OnNpcKilled(player, extra);
            else
                player.Zone.OnNpcDamaged(player, extra);
        }
    }

    // ── SPECIALS (10) ── melee (slot 0) + the named special (slot 1). Real names/tiers from the 75060-75089
    // "Medic's <Weapon> of <Special>" item series in ClientItemDefinitions.json (Comment field, verbatim).
    // AoeRadius > 0 => hits every hostile in range of the caster.
    //
    // SPECIAL ANIMS (re-checked 2026-07-28 directly against AnimationGroups.xml, not just the melee swing pool
    // used before): com_1hs_special_01..09 = ids 1031-1039 and com_2hs_special_01..08 = ids 1051-1058 are both
    // real, distinct, sequential animation groups (confirmed by grep, not inferred) - each of the 10 specials
    // below gets its own real id from these pools (alternating 1hs/2hs, matching the melee slot's own real
    // dual-swing mechanic) instead of the earlier placeholder reuse of the two melee anim ids for everything.
    private const int SpecialAnim1 = 1031, SpecialAnim2 = 1051, SpecialAnim3 = 1032, SpecialAnim4 = 1052,
        SpecialAnim5 = 1033, SpecialAnim6 = 1053, SpecialAnim7 = 1034, SpecialAnim8 = 1054,
        SpecialAnim9 = 1035, SpecialAnim10 = 1055;

    // Real special-ability icons, TYPE 6 (64px). Live-tested 2026-07-29: type 5 (32px) — the assumption from
    // an old memory note that ability-bar icons want type 5, never actually verified until now — renders as a
    // broken-image X in the AbilitiesScreen's Attack/Special columns (a non-zero IconId the client can't
    // resolve), while type 6 is the ONLY type empirically confirmed to render in this exact client build (the
    // Traits panel, which already uses type 6 throughout, works correctly). Switched every id below to its
    // type-6 equivalent accordingly.
    private const int TargetVitalsIcon = 11640;   // abil_medic_target_vitals
    private const int ShockPaddlesIcon = 270;     // abil_medic_shockpaddles
    private const int ImmunizeIcon = 22948;       // abil_medic_immunize
    private const int TriageIcon = 22591;         // abil_medic_triage
    private const int AntibodiesIcon = 22942;     // abil_medic_antibodies
    private const int LaserSurgeryIcon = 22951;   // abil_medic_laser_surgery
    private const int ReflexTestIcon = 22957;     // abil_medic_reflex_test
    private const int VitaminsIcon = 22960;       // abil_medic_vitamins
    private const int VitaminsIconType6 = 22960;  // abil_medic_vitamins (trait-panel icon, same id as above now)
    private const int NurseIcon = 22954;          // abil_medic_nurse
    private const int BloodCellIcon = 22945;      // abil_medic_bloodcell

    // Real FX per special-TYPE (doesn't vary by weapon tier, same convention as the anim pools above).
    //
    // Triage FX CORRECTED 2026-07-29 (live feedback: "read the description of the abilities" surfaced
    // Triage's REAL sourced FX from the same spreadsheet tab this file already cites for names/anims/icons —
    // "PFX_heal_red_hands_cast_starburst (aoe resolve: 16161)" — which was never applied when the ability's
    // heal/AoE mechanics got fixed; the file was still on the OLD pre-spreadsheet "medic_bloodcell trio"
    // thematic guess (16218/16220) from before the real data was found. TriageCastFx (16162,
    // PFX_heal_red_hands_cast_starburst) plays on cast via StartCasting's CompositeEffectId - confirmed a
    // genuine one-shot in ActorCompositeEffectDefinitions.xml (minLifeTime/defaultLifeTime = 1.0s), so no
    // CastEffectStopMs needed. TriageFx (16161, PFX_heal_red_explosion_lg_AOE) plays on each enemy Triage's
    // AoE damage hits, matching the "aoe resolve" pairing the sheet's own FX-name column documents.
    private const int TriageFx = 16161, TriageCastFx = 16162;
    private const int ShockFx = 16154;
    private const int ImmunizeFx = 16190;
    private const int VitaminsFx = 16184;
    private const int ReflexTestFx = 16230;

    // Antibodies FX. AntibodiesCastFx (16225, PRJ_beam_trail_orange_blobs_loop_medic-antibodies) is a
    // "_loop_" asset with NO minLifeTime/defaultLifeTime bound declared in ActorCompositeEffectDefinitions.xml
    // (unlike e.g. Triage's 16162 above, or Ninja's Flame Wave/Flame Breath cast FX, which are also
    // "_loop_"-named but self-terminate via their own declared bound) - it was being played bare via
    // StartCasting's one-shot CompositeEffectId field with no stop mechanism at all, so it never turned off
    // once cast (live feedback 2026-07-29: "medic has some effects still on the character for a really long
    // time" - this is the confirmed root cause, found by cross-referencing every FX id this file and its 5
    // sibling job files use against the client's own "_loop_"-named effect definitions). Fixed by giving it
    // AntibodiesCastEffectStopMs, which routes it through the tag-attach/explicit-remove mechanism instead
    // (same fix class as the potion heal-shower and PowerupSystem's HealShowerMs earlier this session).
    private const int AntibodiesFx = 16264, AntibodiesCastFx = 16225;
    public const int AntibodiesCastEffectStopMs = 2500;
    private const int LaserFx = 16153;

    // Real special-ability animation ids from the OSFR community spreadsheet's dedicated Icons/Anim tab
    // (docs.google.com/spreadsheets/d/1_p8Wxy-ZCBCqveDlm8MyH4HEdMa-eSNHuyI8ImYx2bE, gid=208692944, provided
    // 2026-07-29) — supersedes the arbitrary com_1hs/2hs_special_NN pool assignment from the previous pass.
    // The sheet's own "Anim Status" column marks these PENDING (community-sourced, not dev-confirmed) rather
    // than CONFIRMED, so still not 100% certain, but real sourced data beats an arbitrary pool pick. 4 of 10
    // specials (Nurse!/Immunize/Blood Cell/Antibodies) have no anim listed at all - those keep the old
    // arbitrary pool assignment, now clearly the weaker fallback.
    private const int TriageAnim = 1113;
    private const int TargetVitalsAnim = 1011041;
    private const int ShockPaddlesAnim = 1136;
    private const int VitaminsAnim = 1112;
    private const int ReflexTestAnim = 1052;
    private const int LaserSurgeryAnim = 1061141;

    // Real per-WEAPON melee icons from the same spreadsheet tab — cross-referenced against the weapon-tier
    // sheet's exact ability-instance suffix (e.g. "Medic's Reflex Hammer of Triage" grants specifically
    // "Incision 13", not bare "Incision") to pull the matching Icon IMAGE_ID for THAT exact weapon. This is
    // why the melee icon varies per weapon tier below instead of being one shared MeleeIcon constant — the
    // client apparently gives each real weapon its own distinct basic-attack icon instance, not one shared
    // per ability name. Several icon values repeat across different specials at the same tier (e.g. 4296 for
    // every Bonesaw-tier basic) - that's the sheet's own real data, not a mistake on this end.
    private const int Icon4254 = 4254, Icon3961 = 3961, Icon4296 = 4296, Icon4311 = 4311, Icon4191 = 4191;

    // Every one of the 30 real weapon items gets ITS OWN distinct WeaponAbility pair with real per-tier
    // numbers — NOT a single shared definition reused across every weapon that carries a given special (that
    // was the bug: 5 completely different weapons all showing identical "Triage" numbers). Source: the OSFR
    // community combat spreadsheet's weapon-summary tab ("CONFIRMED" rows only — provided by the user
    // 2026-07-29), cross-checked against ability names already sourced from ClientItemDefinitions.json/wiki.
    // This ALSO corrected several ability names that were wrong before: Alarm's special is really "Nurse!"
    // (matches the "summons medical assistants" line found in client tutorial dialogue earlier — a support/
    // utility ability, not damage, hence no confirmed damage number below), Immunization's melee is "Bruise"
    // (special "Immunize", not "Immunization"), Vitae's pair is "Sedate"/"Blood Cell" (not "Last Rites"/
    // "Vitae" — those were invented, wrongly, last pass), Antibodies' melee is "Heartbreak" (not "Inoculate"),
    // and Lasers' pair is "Cauterize"/"Laser Surgery" (not "Precision Strike"/"Lasers"). Every name below is
    // spreadsheet-CONFIRMED unless commented otherwise. Numeric suffixes in the sheet (e.g. "Incision 13") are
    // per-weapon-instance artifacts, not part of the ability's real display name — stripped here to match the
    // base name used elsewhere (AbilityNameIds).
    // Triage's REAL wiki description (OSFR spreadsheet icons/anim tab, gid=208692944, re-checked 2026-07-29
    // per live feedback "read the description of the abilities and make sure any heal abilities like triage
    // [are wired to heal]"): "Sorts out a friend from foe with surgical precision, healing you and your group
    // and damaging all nearby opponents." Element=heal, Effect="heal / dmg", Scope="AoE (Heal), AoE (Dmg)" -
    // this was WRONG before (damage-only, single-target - no AoE, no heal at all, despite already having a
    // wiki-confirmed real per-tier damage number). Fixed: the special now ALSO heals the caster + nearby
    // allies (via Player.HealSelfAndNearbyAllies, same helper First Aid uses - wired in
    // AbilityPacketClientRequestStartAbilityHandler, gated on ability.Name == "Triage") on top of its existing
    // real damage number, and the damage itself is now AoE (AoeRadius, matching "all nearby opponents") instead
    // of single-target. The per-tier DAMAGE numbers remain the real sourced ones; the HEAL amount has no wiki
    // number given anywhere, so it's ours to tune (picked to roughly match First Aid's own 250, since both are
    // "you and your group" heals).
    public const int TriageHealAmount = 250;
    public const float TriageHealRadius = 15f;

    private static MedicWeapon Triage(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Incision", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Triage", TriageIcon, specialDmg, TriageAnim, TriageFx, CastEffectId: TriageCastFx, AoeRadius: 8f));

    private static MedicWeapon Vitals(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Injure", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Target Vitals", TargetVitalsIcon, specialDmg, TargetVitalsAnim, MeleeHitFx));

    // Nurse! has no confirmed damage number on ANY tier (spreadsheet lists "-" every time) — real client
    // tutorial dialogue AND the sheet's icons/anim tab (gid=208692944, Scope="Summon") agree this is a
    // support/summon ability ("Calls a group of trained medical assistants to help you fight"), not a damage
    // nuke. FIXED 2026-07-29 (live feedback: "wire it in... tell me which weapons it's for"): now actually
    // summons, via SummonCount + BaseZone.SummonCombatClones (see MedicNurseCloneConfig below and the
    // Ninja Shadow Army generalization in CombatCloneConfig's header comment) — same summon PRIMITIVE Shadow
    // Army uses, its own real "medical assistant" model (421, human_f_nurse_naia.agr — the actual in-game
    // Nurse Naia/Tara/Nurse Jane NPC model, per Npcs.json) instead of reusing the ninja's shadow-clone look.
    // The flat placeholder damage stays too (same pattern as Ninja's Shadow Army, which deals its own real
    // damage IN ADDITION to summoning) since there's no wiki basis to say Nurse! deals literally zero.
    // Summon count (2) has no wiki number either - "a group" - picked smaller than Shadow Army's 3 since this
    // is a support/utility summon, not a primary damage cooldown.
    public const int NurseSummonCount = 2;
    public const int NurseSummonLifetimeSeconds = 12;

    // Assistant clone-summon config (see CombatCloneConfig's header comment). AttackAnim/AttackDamage/
    // AttackCooldownMs/HitFx/SpawnPoofFx have no wiki source for a "medical assistant" specifically - reused
    // the same generic humanoid swing/hit/poof the Ninja config uses (ours to tune, not invented-and-hidden).
    public static readonly CombatCloneConfig NurseCloneConfig = new(
        ModelId: 421, Name: "Medical Assistant", RunAnim: 3, WalkAnim: 2, StandAnim: 1, AttackAnim: 1021,
        AttackDamage: 200, AttackCooldownMs: 1400, HitFx: MeleeHitFx, SpawnPoofFx: 21, LeashRange: 15f);

    private static MedicWeapon Alarm(int meleeIcon, int meleeDmg) => new(
        new("Assist", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Nurse!", NurseIcon, 500, SpecialAnim3, MeleeHitFx, SummonCount: NurseSummonCount));

    // Immunize's REAL wiki description (same sheet as Triage, gid=208692944): "Just what the doctor ordered!
    // Makes you and your group invincible and damages all nearby opponents." Effect="buff (dmg reduction) /
    // dmg", Scope="AoE (Buff), AoE (Dmg)" - was WRONG before (damage-only, no buff at all). Fixed 2026-07-29:
    // wired in AbilityPacketClientRequestStartAbilityHandler (gated on ability.Name == "Immunize") to also
    // apply an incoming-damage-reduction buff to the caster + nearby allies via
    // Player.ApplyDamageReductionToNearbyAllies / CombatBuffs.AddDamageReductionBuff, on top of the existing
    // real per-tier damage number. The wiki says "invincible" (i.e. 100% reduction) - taken literally that
    // would make a Medic (and their whole group) briefly unkillable off a single button press, which reads as
    // exploitable rather than "a doctor's shield", so this is toned down to a large-but-not-total reduction
    // instead; the PERCENTAGE and DURATION have no wiki number given either way, so both are ours to tune.
    public const int ImmunizeDamageReductionPercent = 75;
    public const int ImmunizeDurationMs = 5000;
    public const float ImmunizeBuffRadius = 15f;

    private static MedicWeapon Immunization(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Bruise", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Immunize", ImmunizeIcon, specialDmg, SpecialAnim4, ImmunizeFx, AoeRadius: 8f));

    private static MedicWeapon Shock(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Clear!", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Shock Paddles", ShockPaddlesIcon, specialDmg, ShockPaddlesAnim, ShockFx, AoeRadius: 8f));

    // Vitamins special ALSO has no confirmed damage number on any tier ("-" every time, same as Nurse! above)
    // — same placeholder-flat-damage caveat.
    private static MedicWeapon Vitamins(int meleeIcon, int meleeDmg) => new(
        new("Wound", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Vitamins", VitaminsIcon, 500, VitaminsAnim, VitaminsFx));

    private static MedicWeapon Reflexes(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Traumatize", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Reflex Test", ReflexTestIcon, specialDmg, ReflexTestAnim, ReflexTestFx, AoeRadius: 8f));

    private static MedicWeapon Vitae(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Sedate", meleeIcon, meleeDmg, MeleeAnim, MeleeHitFx),
        new("Blood Cell", BloodCellIcon, specialDmg, SpecialAnim8, MeleeHitFx));

    private static readonly MedicWeapon AntibodiesKit = new(
        // Only exists at the top tier (Shockrod, L16) per the spreadsheet — no lower-tier variant.
        new("Heartbreak", Icon4191, 2372, MeleeAnim, MeleeHitFx),
        new("Antibodies", AntibodiesIcon, 10674, SpecialAnim9, AntibodiesFx, CastEffectId: AntibodiesCastFx, CastEffectStopMs: AntibodiesCastEffectStopMs));

    private static readonly MedicWeapon LasersKit = new(
        // Only exists at the top tier (Shockrod, L16). Melee "Cauterize" hits twice per the sheet (1186 x2) —
        // the WeaponAbility record has no multi-hit field, so this uses the summed total (2372) instead of
        // modeling 2 separate hits, a simplification not an invented number.
        new("Cauterize", Icon4191, 2372, MeleeAnim, MeleeHitFx),
        new("Laser Surgery", LaserSurgeryIcon, 10674, LaserSurgeryAnim, LaserFx, AoeRadius: 8f));

    // ── NOVELTY/COIN-SHOP/REWARD WEAPONS ── real names/damage/icons from the spreadsheet's weapon-summary tab
    // (CONFIRMED rows) cross-referenced with its Icons/Anim tab, same sourcing as the 10 core specials above.
    // These 3 have their own distinct ability names (not variants of the 10 "of <Special>" kits) - no FX/anim
    // data exists for any of them in either sheet (blank columns), so they use the generic melee-hit fallback,
    // flagged per kit.
    private static readonly MedicWeapon HeartKit = new(
        // Sweetheart Saw (item 76809). No FX/anim listed for either ability.
        new("Heart Attack", 30200, 2372, MeleeAnim, MeleeHitFx),
        new("Heart Breaker", 30190, 5977, SpecialAnim3, MeleeHitFx));

    private static readonly MedicWeapon BalloonKit = new(
        // Balloon Saw (item(s) 16350-16354/77446 - our data doesn't distinguish the sheet's 3 named variants
        // Reward/Coin Shop/Gifting Pinata, so this uses the Reward Version's numbers, the first-listed of the
        // three). No FX/anim listed.
        new("Annual Checkup", 31002, 2372, MeleeAnim, MeleeHitFx),
        new("Party Crasher", 291, 8453, SpecialAnim4, MeleeHitFx));

    private static readonly MedicWeapon PowerFistKit = new(
        // Medic's Power Fist (item 78198). The sheet flags this weapon's numbers as unresolved/needing
        // investigation ("Basic and Super attack have additional dmg on top of variable dmg") and lists a
        // per-rank scaling table rather than one fixed number - this uses the rank-16 (max, our top-rank
        // bracket) values (2372/8302) for consistency with how every other kit here picks one representative
        // number, NOT the sheet's own separate "1000/1500(?)" flat guess. No FX/anim listed.
        new("Power Smash", 39199, 2372, MeleeAnim, MeleeHitFx),
        new("Power Rain", 39240, 8302, SpecialAnim5, MeleeHitFx));

    // weapon def id -> ability pair, one PER ITEM (not shared). Real client Medic weapons (Reflex Hammer L1,
    // Scalpel L4, Bonesaw L8, Megasaw L12, Shockrod L16) — the 75060-75089 "of <Special>" item series, ids
    // verified directly against ClientItemDefinitions.json, numbers/names/icons verified against the
    // spreadsheet's exact per-weapon ability-instance suffix (see the Icon4NNN consts' header comment above).
    private static readonly Dictionary<int, MedicWeapon> _byWeaponDefId = new()
    {
        // Reflex Hammer (L1) — "Incision 13"/"Injure 3"
        [75060] = Triage(Icon4254, 279, 889), [75061] = Vitals(Icon4254, 254, 640),
        // Scalpel (L4) — "Incision 14"/"Injure"(base)/"Assist 3"/"Bruise 3"
        [75062] = Triage(Icon3961, 488, 1554), [75063] = Vitals(Icon4296, 444, 1118),
        [75064] = Alarm(Icon3961, 444), [75065] = Immunization(Icon3961, 444, 1118),
        // Bonesaw (L8) — "Incision 11"/"Injure"(base)/"Assist"(base)/"Bruise"(base)/"Wound"(base)/"Clear! 2"
        [75066] = Triage(Icon4296, 853, 2716), [75067] = Vitals(Icon4296, 776, 1955),
        [75068] = Alarm(Icon4296, 776), [75069] = Immunization(Icon4296, 776, 1955),
        [75070] = Vitamins(Icon4296, 776), [75071] = Shock(Icon4296, 776, 2716),
        // Megasaw (L12) — "Incision 12"/"Injure 2"/"Assist 2"/"Bruise 2"/"Wound 2"/"Clear! 3"/"Traumatize"(no
        // icon listed - Reflexes stays iconless at this tier)/"Sedate"(base)
        [75072] = Triage(Icon4311, 1492, 4750), [75073] = Vitals(Icon4311, 1357, 3419),
        [75074] = Alarm(Icon4311, 1357), [75075] = Immunization(Icon4311, 1357, 3420),
        [75076] = Vitamins(Icon4311, 1357), [75077] = Shock(Icon4311, 1357, 4750),
        [75078] = Reflexes(MeleeIcon, 1357, 6107), [75079] = Vitae(Icon4311, 1357, 3420),
        // Shockrod (L16) — "Incision 15"/"Injure 4"/"Assist 4"/"Bruise 4"/"Wound 3"/"Clear! 4"/"Traumatize 2"/
        // "Sedate 2"/"Heartbreak"/"Cauterize" — all 10 specials
        [75080] = Triage(Icon4191, 2609, 8302), [75081] = Vitals(Icon4191, 2372, 5977),
        [75082] = Alarm(Icon4191, 2372), [75083] = Immunization(Icon4191, 2372, 5977),
        [75084] = Vitamins(Icon4191, 2372), [75085] = Shock(Icon4191, 2372, 8302),
        [75086] = Reflexes(Icon4191, 2372, 10674), [75087] = Vitae(Icon4191, 2372, 5977),
        [75088] = AntibodiesKit, [75089] = LasersKit,

        // ── Starter weapon ── "Student Medic Hammer" (item 4622, class 27, model
        // hammer_ar_ag_weapon_reflexhammer.adr — same model as the real Reflex Hammer tier) — wired to the L1
        // Triage numbers, matching how the other jobs treat their tutorial/starter weapon. No sheet row of its
        // own (it's the tutorial-only starter, not a real shop/drop item) - reuses the Reflex Hammer's icon.
        [4622] = Triage(Icon4254, 279, 889),

        // ── Novelty/coin-shop/prize-wheel weapons ── real item ids looked up by exact Comment match against
        // ClientItemDefinitions.json, real ability data from the same spreadsheet's CONFIRMED rows. Weapons
        // whose sheet row was PENDING (not CONFIRMED), or whose exact name has no matching item in our data at
        // all (content this server build doesn't have - "Medic's Tentacle Bonesaw of Riptide", "Medic's
        // Forged Saw of Promise", the 3 "New School" weapons, "Treasure Trader Elemental Procedure", the 2
        // non-plain "Balloon Saw" variants), are left unmapped rather than guessed.
        [1431] = Triage(14770, 488, 1554),      // Elitist's Scalpel ("Incision 8")
        [48198] = Triage(14770, 853, 2716),     // Carbon Scalpel ("Incision 5")
        [48216] = Triage(14770, 853, 2716),     // Gravel Scalpel ("Incision 5")
        [48192] = Triage(14770, 853, 2716),     // Party Scalpel ("Incision 5")
        [48204] = Triage(14770, 853, 2716),     // Sunlit Scalpel ("Incision 5")
        [22223] = Triage(14388, 2609, 8302),    // Blazing Mega Saw ("Incision 3")
        [37933] = Triage(14388, 2609, 8302),    // Fiery Mega Saw ("Incision 3")
        [23025] = Triage(4191, 2609, 8302),     // Smokey Shockrod ("Incision 15")
        [48131] = Triage(4191, 2609, 8302),     // Smokey Shockrod (2nd id)
        [48323] = Triage(14388, 2609, 8302),    // Callahan's Cutter ("Incision 4")
        [55816] = Triage(14241, 2609, 8302),    // Magical Essence Shockrod ("Incision 10")
        [76809] = HeartKit,                     // Sweetheart Saw
        [78198] = PowerFistKit,                 // Medic's Power Fist
    };

    // Large dye/tint-variant id ranges - real items, but the sheet only gives one set of numbers per base
    // weapon name (dye color doesn't change stats), so every variant maps to the same kit. Field initializers
    // run before this ctor body, so AllWeaponDefIds (snapshotted at the end) picks these up too.
    static MedicWeaponAbilities()
    {
        // First Aid Hammer ("Incision 9") - 10 dye variants.
        foreach (var id in new[] { 30270, 30271, 30272, 30273, 30274, 30275, 30276, 30277, 30278, 30279 })
            _byWeaponDefId[id] = Triage(3961, 279, 889);

        // Trauma Support Shockrod ("Incision 14") - 10 dye variants.
        foreach (var id in new[] { 30510, 30511, 30512, 30513, 30514, 30515, 30516, 30517, 30518, 30519 })
            _byWeaponDefId[id] = Triage(3961, 2609, 8302);

        // Balloon Saw (plain) - our data doesn't split the sheet's Reward/Coin Shop/Gifting Pinata variants,
        // uses the Reward Version's numbers for all of them.
        foreach (var id in new[] { 16350, 16351, 16352, 16353, 16354, 77446 })
            _byWeaponDefId[id] = BalloonKit;

        // Mega Saw ("Incision 3") - the largest dye/tint range (68 ids).
        foreach (var id in new[] {
            7105,7106,7107,7108,7109,7112,7113,7114,7115,7116,7117,7118,7119,7120,7121,7123,7124,7125,7126,7127,
            7128,7129,7130,7131,7132,7133,7134,7135,7136,7137,7138,7139,30150,30151,30152,30153,30154,30155,
            30156,30157,30158,30159,37932,37934,37935,37936,37938,37939,37941,37942,37944,37945,37947,37948,
            37950,37951,37953,37954,37955,37957,37958,37960,37961,37962,37964,37965,37966,37968 })
            _byWeaponDefId[id] = Triage(14388, 1492, 4750);

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, MedicWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

    // Ability name Global.Text ids — UNMINED (see file header). Empty for now; SlotNameIcon falls back to
    // NameId 0 (blank AbilitiesScreen label) for every entry until these are reversed from a live client.
    // REAL ability name Global.Text ids (resolved 2026-07-29, same T4-hash technique as the traits) — fills
    // the AbilitiesScreen Attack/Special columns, which were blank ("String#0 not found") until now since this
    // dict was empty. "Triage" had 2 real candidate ids in different client-text contexts (both `ucdt` type,
    // so type alone didn't disambiguate) - picked 420186 over 442456 because it sits in the SAME 420xxx-421xxx
    // neighborhood as every other confirmed Medic ability name below, while 442456 was an outlier only
    // superficially close to an unrelated Warrior id. Target Vitals/Vitamins/Shock Paddles reuse the exact
    // same id already confirmed working for the Traits panel (verified: each string has only ONE `ucdt`
    // client-text entry total, so there's no alternate to prefer). Alarm/Immunization/Vaccinate/Injection/
    // Last Rites/Inoculate/Precision Strike/Lasers/Vitae found NO matching client string at all (re-searched
    // 2026-07-29) - stay unresolved (NameId 0, blank column) same as their already-flagged damage numbers.
    // Every previously-unresolved entry from the last pass turned out to be searching for the WRONG name
    // (Alarm/Immunization/Vaccinate/Injection/Last Rites/Inoculate/Precision Strike/Lasers/Vitae were never
    // real ability names to begin with - see the per-special comments above). All 20 real melee+special names
    // now resolve.
    private static readonly IReadOnlyDictionary<string, int> AbilityNameIds = new Dictionary<string, int>
    {
        ["Incision"] = 420174,
        ["Triage"] = 420186,
        ["Injure"] = 420394,
        ["Target Vitals"] = 3810,
        ["Assist"] = 420612,
        ["Nurse!"] = 420627,
        ["Bruise"] = 421040,
        ["Immunize"] = 421043,
        ["Clear!"] = 421247,
        ["Shock Paddles"] = 3800,
        ["Wound"] = 421246,
        ["Vitamins"] = 24061,
        ["Traumatize"] = 421264,
        ["Sedate"] = 421265,
        ["Reflex Test"] = 421276,
        ["Blood Cell"] = 421277,
        ["Heartbreak"] = 421288,
        ["Cauterize"] = 421289,
        ["Antibodies"] = 421300,
        ["Laser Surgery"] = 421301,
        // Novelty-weapon ability names (Sweetheart Saw / Medic's Power Fist). Power Smash/Power Rain each had
        // several real-CID candidates (likely shared generic novelty-item text, not Medic-specific) - picked
        // the pair sitting adjacent to each other (437177/438137) as the most likely same-context match.
        ["Heart Attack"] = 426972,
        ["Heart Breaker"] = 427021,
        ["Annual Checkup"] = 428379,
        ["Party Crasher"] = 45406,
        ["Power Smash"] = 437177,
        ["Power Rain"] = 438137,
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

    public static MedicWeapon? GetEquippedWeapon(Player player)
    {
        var defId = player.GetEquippedWeaponDefinitionId();
        return defId != 0 && ByWeaponDefId.TryGetValue(defId, out var weapon) ? weapon : null;
    }

    // slot 0 = melee, slot 1 = special.
    public static WeaponAbility ResolveAbility(Player player, int slot)
    {
        var defId = player.GetEquippedWeaponDefinitionId();

        if (!ByWeaponDefId.TryGetValue(defId, out var weapon))
            return BareMelee;

        return slot <= 0 ? weapon.Melee : weapon.Special;
    }

    public const int SpecialEnergyCost = 100;

    public static AbilityPacketSetDefinition BuildToolbar(Player player, IResourceManager resources)
    {
        var weapon = GetEquippedWeapon(player);

        if (weapon is null)
            return AbilityPacketSetDefinition.CreateEmpty(MedicProfileId);

        var nameId = 0;
        if (resources.ClientItemDefinitions.TryGetValue(player.GetEquippedWeaponDefinitionId(), out var weaponDef))
            nameId = weaponDef.NameId;

        var def = new AbilityPacketSetDefinition { ProfileId = MedicProfileId, SlotCount = 8 };

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
