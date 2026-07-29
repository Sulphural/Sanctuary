using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Combat;

// COMBAT WIP: abilities are driven by the EQUIPPED WEAPON, the way Free Realms did it. Each ninja
// "Ninja's Shadow Blade of X" weapon (Resources/ClientItemDefinitions.json, ids 75110-75119) grants TWO
// abilities: Ability 1 = a melee sword technique (slot 0), Ability 2 = the named special (slot 1).
//
// PER-WEAPON DAMAGE FIX (2026-07-29): every one of the ~90 real weapon items below now gets ITS OWN distinct
// WeaponAbility pair with real per-tier numbers, sourced from the OSFR community combat spreadsheet's
// weapon-summary tab (gid=1221468972, "CONFIRMED"/"PENDING" rows) cross-referenced against its icons/anim tab
// (gid=983369777) for Icon IMAGE_ID/FX/Animation per named ability variant — NOT a single shared kit object
// reused across every weapon that happens to grant the same special (that was the bug: the Training Sword,
// Blade, Scythe, Jagged Scythe and Shadow Blade "of Dragonstrike" items, 5 completely different real weapons
// spanning level 1-16, all showed the exact same top-tier "Dragonstrike" damage number, same shape of bug the
// Warrior/Medic files had). Every special TYPE (Dragonstrike/1000 Storms/Shuriken Storm/Flame Wave/Shadow
// Army/Solar Flare/Dragon Breath/Mysticism/Soul Power/Deception) is now a factory function parameterized by
// melee icon + both real damage numbers, called once per real weapon item id. "CONFIRMED" rows use the
// sheet's exact tooltip numbers; "PENDING" rows (mostly the "(?)" ability-name-uncertain scythe/bokken/blade
// tier reskins) still carry real numbers, just flagged per-entry below since the exact basic-attack name
// variant (and therefore its Icon IMAGE_ID) is unconfirmed - those fall back to the bare "Flame Flash" icon.
// CORRECTED 2026-07-29: "Molten Dragon Blade" (48322) was previously aliased into DragonBreathKit (a themed
// guess); the sheet's own weapon-summary row lists it as a DRAGONSTRIKE weapon (Flame Flash 8 / Dragonstrike
// 4, both CONFIRMED) - remapped. "Lunar Blade" (78715) and "Precursor Energy Blade" (79022) were previously
// aliased into MysticismKit/SoulPowerKit respectively (also themed guesses); the sheet gives both their OWN
// real, distinct ability names (Moon Slice/Celestial Spin and Energy Slash/Energy Storm) not shared with any
// "of X" weapon - each now gets its own dedicated kit below instead of borrowing an unrelated special's FX/
// mechanic. "Dragon Blade" (13663/55337/70444/76470) and "Storm Breaker" (9031/13669/55360) have no dedicated
// spreadsheet row of their own (their real in-game abilities weren't in either tab) - these stay a themed
// reuse of the Shadow-Blade-tier Dragonstrike/1000-Storms numbers, same honest "no dedicated source" flag the
// previous pass already carried, just now going through the shared factory instead of a hand-duplicated kit.
//
// ANIM CORRECTIONS from the same spreadsheet pass: Flame Wave's animation was 1032 (a guess); the sheet's own
// notes column says "Updated Animation ID from 1032" -> 1036, taken here. Flame Breath (Dragon Breath's
// special) was hard-flagged in the previous pass as a KNOWN-WRONG id (1037 = com_1hs_special_07, a generic
// unnamed clip with zero fire/dragon/breath naming anywhere in the client tables, previously left in place
// only for lack of a better-sourced alternative) - the sheet now offers 1071 (PENDING confidence, but a real
// sourced value beats a confirmed-wrong placeholder), taken here. Fan of Blades (Deception's special) is the
// one exception: the sheet lists 1051 at "UNKNOWN" confidence (the lowest tier in its own Anim Status column),
// while the previous pass's 1033 (air_throw) was live user-verified by sight - kept 1033 rather than downgrade
// to a lower-confidence value. Every other special's anim/FX already matched the sheet exactly (Dragonstrike/
// 1000 Storms/Shuriken Storm/Shadow Army/Flaming Uppercut/Mystical Blade/Mystical Drain all CONFIRMED, no
// change) so those are untouched.
//
// ICONS (cracked 2026-06-20): the ability-slot IconId is a flat IMAGE_ID, NOT an image-set id. The real
// ninja ability icons are the abil_ninja_* image sets' IMAGE_IDs (Client/Resources/Images/ImageSetMappings.txt,
// Small=type5). e.g. abil_ninja_shuriken_storm set 4902 -> Small IMAGE_ID 22986. (Sending the set id 4902 hit
// the food/fruit image #4902 instead.) FX ids match ActorCompositeEffectDefinitions.xml (confirmed live:
// id 1 = fire). EffectId = impact FX played on the TARGET (AttackProcessed). CastEffectId = FX played on the
// CASTER during StartCasting (the projectile/aura/ground-AoE you see come off the ninja); 0 = none. For
// projectile specials (Shuriken throw, Dragonstrike) CastEffectId = the launch/trail and EffectId = the
// land/impact; for ground-AoE specials the same id works for both. (Usage per
// drafts/ninja-special-anim-fx-research.md §FINDINGS iter 4.)
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
// BuffMultiplierPct/BuffDurationMs (added 2026-07-27, field names/values informed by the community
// "combat-v2" fork): a self-buff ability (0 Damage, BuffMultiplierPct > 0) applies a temporary % damage
// multiplier to the caster instead of resolving damage against a target - see the short-circuit in
// AbilityPacketClientRequestStartAbilityHandler.HandleCombatAbility. Default 0 = ordinary damage-dealing
// ability, unchanged behavior for every existing entry.
// TickCount/TickIntervalMs/CasterEndEffectStopMs (added 2026-07-27, live feedback: "archer's bow of volley
// shouldn't last that long.. a ton of arrows coming from the sky, should damage the enemy a few times and
// then stop after a bit"): default 1/0/0 = exactly one pass, unchanged for every ninja ability (none of the
// 10 specials use multi-tick).
public sealed record WeaponAbility(string Name, int IconImageId, int Damage, int Animation, int EffectId, int CastEffectId = 0, int SummonCount = 0, int SwordEffectId = 0, int CasterEndEffectId = 0, int EnemyExtraEffectId = 0, float AoeRadius = 0f, int CastEffectStopMs = 0, int EnergyCost = 100, int BuffMultiplierPct = 0, int BuffDurationMs = 0, int TickCount = 1, int TickIntervalMs = 0, int CasterEndEffectStopMs = 0);

public sealed record NinjaWeapon(WeaponAbility Melee, WeaponAbility Special);

public static class NinjaWeaponAbilities
{
    public const int WeaponSlot = 7;
    public const int NinjaProfileId = 2;

    private const int MeleeAnimation = 1021; // com_1hs_attack_01 = real 1-hand SWORD swing (human_m_com_1hs_attack_01.gr2).
                                              // Was 1099 (com_swing) which is NOT in human_m.adr -> client fell back to a
                                              // bare-hand swing. 1021-1024 = com_1hs_attack_01..04; group 1020=com_1hs_attack
                                              // picks one at random (authentic variety) if preferred.
    private const int MeleeHitFx = 5414;     // PFX_Hit_Metal_vs_Flesh — CONFIRMED by name in the client's own
                                             // ActorCompositeEffectDefinitions.xml (id="5414"). Ninja weapons are
                                             // all swords/scythes (metal), so this is the correct material-based
                                             // basic-hit FX for every ninja melee swing (was the generic placeholder
                                             // PFX_Hit_Flash, id 7). The same table also defines
                                             // PFX_Hit-Critical_Metal_vs_Flesh (id 5627) and
                                             // PFX_Hit-Knockout_Metal_vs_Flesh (id 5637), but WeaponAbility only
                                             // carries a single EffectId per ability with no crit/KO branch in the
                                             // damage-resolution code (AbilityPacketClientRequestStartAbilityHandler),
                                             // so those two variants are NOT wired up here — that would need a
                                             // separate crit-aware EffectId path, which is out of scope for this pass.
    // Melee slot shows the weapon's SWORD icon: the shadow-blade item set (3152) Small IMAGE_ID = 14407.
    // (abil_ninja_shadow_blade's 22599 renders as a leaf in our client; the item sword image is correct.)
    // Kept as the BARE-weapon fallback (no equipped weapon def match); real per-weapon melee icons below vary
    // instead — see the FlameFlashIconX/etc. constants, cross-referenced against each weapon's own exact
    // basic-attack ability-instance name (e.g. "Flame Flash 9" vs bare "Flame Flash") from the spreadsheet's
    // icons/anim tab, same technique the Medic file's per-weapon Icon4NNN consts use.
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

    // ── PER-WEAPON MELEE ICONS ── every "Flame Flash"/"Lightning Strike"/"Twisted Edge"/"Cinder Slash"/
    // "Dark Assault"/"Ashen Strike" basic attack in the spreadsheet is really a family of numbered variants
    // ("Flame Flash 9", "Flame Flash 12", bare "Flame Flash", ...), each with its OWN Icon IMAGE_ID in the
    // icons/anim tab despite sharing the same base ability name (stripped to the base name in AbilityNameIds,
    // same convention the Medic file uses for its own numbered-variant melee names). The letter suffix below
    // groups weapons that happen to land on the same icon value across different ability-name families (e.g.
    // bare "Flame Flash"/"Lightning Strike"/"Twisted Edge"/"Cinder Slash" all use icon 4335) — this is the
    // sheet's own real data, not a mistake on this end.
    private const int FlameFlashIconA = 4335;   // bare: Flame Flash / Flame Flash 2 / Lightning Strike / Twisted Edge / Cinder Slash
    private const int FlameFlashIconB = 14746;  // "3": Flame Flash 10 / Flame Flash 3 / Twisted Edge 3 / Cinder Slash 3 / Dark Assault 2 / Ashen Strike 2
    private const int FlameFlashIconC = 4113;   // "2/9": Flame Flash 9 / Lightning Strike 2 / Twisted Edge 2 / Cinder Slash 2 / Dark Assault (bare) / Ashen Strike (bare) / Fiery Slice (bare) / Mystic Rush (bare)
    private const int FlameFlashIconD = 4323;   // "4/11": Flame Flash 11 / Lightning Strike 3 / Twisted Edge 4 / Cinder Slash 4 / Dark Assault 3 / Ashen Strike 3 / Fiery Slice 2 / Mystic Rush 2 / Shadowslash / Hidden Strike
    private const int FlameFlashIconE = 4329;   // "6/12": Flame Flash 12 / Flame Flash 6 / Lightning Strike 4
    private const int FlameFlashIconF = 14412;  // "7/8": Flame Flash 7 / Flame Flash 8
    private const int FlameFlashIconG = 14109;  // "4": Flame Flash 4
    private const int FlameFlashIconH = 14812;  // "5": Flame Flash 5

    // ── SPECIAL-TYPE FX/ANIM/ICON (real, doesn't vary by weapon tier — see file header for what changed vs.
    // the previous pass). Damage DOES vary by tier, hence the factory functions below instead of one shared
    // constant WeaponAbility per special.
    private const int DragonstrikeIcon = 22965, DragonstrikeAnim = 1035, DragonstrikeCasterEndFx = 16186;
    private const int ThousandStormsIcon = 22992, ThousandStormsAnim = 1033, ThousandStormsCastFx = 16088;
    private const int ShurikenStormIcon = 22986, ShurikenStormAnim = 1039, ShurikenStormFx = 4012;
    private const int FlameWaveIcon = 22974, FlameWaveAnim = 1036, FlameWaveCastFx = 16140; // anim 1036 = sheet correction from 1032, see file header
    private const int ShadowArmyIcon = 22989, ShadowArmyAnim = 1061143, ShadowArmyImpactFx = 21, ShadowArmyCastFx = 16483;

    // Shadow Army's clone-summon config (see CombatCloneConfig's header comment for the generalized engine
    // this feeds - BaseZone.SummonCombatClones). Model/FX ids are the same real ones this file already used
    // before the 2026-07-29 generalization (model 945 human_m_ninja_ghost.adr, black-smoke poof 21, shadow-
    // blade impact 15999); AttackDamage/AttackCooldownMs/MoveSpeed/AttackRange are unchanged gameplay
    // constants carried over from the old StartingZone-only prototype, still ours to tune (no wiki number
    // for a clone's own attack). LeashRange is NEW (the old version had no leash at all - it only ever chased
    // one fixed dummy) - picked generously so the clones behave close to how they always did.
    public static readonly CombatCloneConfig ShadowArmyCloneConfig = new(
        ModelId: 945, Name: "Shadow Ninja", RunAnim: 3, WalkAnim: 2, StandAnim: 1, AttackAnim: 1021,
        AttackDamage: 200, AttackCooldownMs: 1400, HitFx: 15999, SpawnPoofFx: 21, LeashRange: 25f);
    public const int ShadowArmyLifetimeSeconds = 12;
    private const int SolarFlareIcon = 22977, SolarFlareAnim = 1017, SolarFlareCastFx = 16119;
    private const int DragonBreathIcon = 22971, DragonBreathAnim = 1071, DragonBreathCastFx = 16129; // anim 1071 = sheet correction from the previously-known-wrong 1037, see file header
    private const int MysticismIcon = 22980, MysticismAnim = 1061141, MysticismSwordFx = 16169;
    private const int SoulPowerIcon = 22983, SoulPowerAnim = 1034, SoulPowerFx = 16180;
    private const int DeceptionIcon = 22968, DeceptionAnim = 1033, DeceptionCastFx = 16185; // anim kept at the previous pass's live-verified 1033 (air_throw) over the sheet's own "UNKNOWN"-confidence 1051, see file header

    // Shadow Army's special damage has NO confirmed number at any tier (spreadsheet lists "(?)" every time,
    // "Unknown super attack effect values" in its own Notes column) — estimated here to sit in the same range
    // as every OTHER same-tier special (which IS sheet-confirmed), same convention as the Medic file's Nurse!/
    // Vitamins placeholder-damage flag. Real melee damage (the "388 (x2)"-style entries) IS confirmed.
    private const int ShadowArmySpecialEstL8 = 3492, ShadowArmySpecialEstL12 = 6107, ShadowArmySpecialEstL16 = 10674;

    // ── SPECIAL-TYPE FACTORIES ── one per real special ABILITY TYPE, parameterized by the melee icon + both
    // real per-weapon damage numbers so every weapon item below gets its own distinct pair instead of sharing
    // one kit object (the bug this pass fixes). AoeRadius/SummonCount/etc. match the "Scope" column in the
    // spreadsheet's icons/anim tab (Front cone/Surround/AOE => damages the whole pack; single-target otherwise).
    private static NinjaWeapon Dragonstrike(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Flame Flash", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Dragonstrike", DragonstrikeIcon, specialDmg, DragonstrikeAnim, 0, 0, 0, 0, DragonstrikeCasterEndFx)); // Front cone; land FX plays on the CASTER's feet at the end of the anim, nothing on the enemy (user round-2)

    private static NinjaWeapon ThousandStorms(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Lightning Strike", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("1000 Storms", ThousandStormsIcon, specialDmg, ThousandStormsAnim, 0, ThousandStormsCastFx, AoeRadius: 12f)); // AOE (house rule, not retail-sourced radius); cast FX on caster, nothing on enemy (user: stop casting on target)

    private static NinjaWeapon ShurikenStorm(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Twisted Edge", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Shuriken Storm", ShurikenStormIcon, specialDmg, ShurikenStormAnim, ShurikenStormFx, 0)); // Surround (all directions); impact-only FX on the enemy, no cast FX

    private static NinjaWeapon FlameWave(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Cinder Slash", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Flame Wave", FlameWaveIcon, specialDmg, FlameWaveAnim, 0, FlameWaveCastFx)); // Front cone; ground AoE FX at the caster's feet only (user: FX only at my feet)

    private static NinjaWeapon ShadowArmies(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Dark Assault", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Shadow Army", ShadowArmyIcon, specialDmg, ShadowArmyAnim, ShadowArmyImpactFx, ShadowArmyCastFx, SummonCount: 3)); // Summon (clones); summons 3 shadow-clone NPCs; specialDmg is an ESTIMATE, see const comment above

    private static NinjaWeapon SolarFlare(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Ashen Strike", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Flaming Uppercut", SolarFlareIcon, specialDmg, SolarFlareAnim, 0, SolarFlareCastFx)); // Surround; caster-only FX (user: player is the only one with the FX, not the NPC)

    private static NinjaWeapon DragonBreath(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Fiery Slice", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Flame Breath", DragonBreathIcon, specialDmg, DragonBreathAnim, 0, DragonBreathCastFx)); // Front cone; caster-only FX (user: FX played by me only, not any enemy)

    // Mystical Blade is a self-BUFF (empowers the WEAPON, not a damage nuke) — Damage stays 0, short-circuited
    // in AbilityPacketClientRequestStartAbilityHandler before target resolution. Only the MELEE (Mystic Rush)
    // damage varies per weapon tier; the buff magnitude (+200%/15s, carried from the community "combat-v2"
    // fork as a plausible estimate) is not sheet-sourced and stays fixed across tiers.
    private static NinjaWeapon Mysticism(int meleeIcon, int meleeDmg) => new(
        new("Mystic Rush", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Mystical Blade", MysticismIcon, 0, MysticismAnim, 0, 0, 0, MysticismSwordFx, BuffMultiplierPct: 200, BuffDurationMs: 15000));

    private static NinjaWeapon SoulPower(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Shadowslash", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Mystical Drain", SoulPowerIcon, specialDmg, SoulPowerAnim, SoulPowerFx, SoulPowerFx)); // Surround (drain); same FX id for cast+impact (AOE-drain beam)

    private static NinjaWeapon Deception(int meleeIcon, int meleeDmg, int specialDmg) => new(
        new("Hidden Strike", meleeIcon, meleeDmg, MeleeAnimation, MeleeHitFx),
        new("Fan of Blades", DeceptionIcon, specialDmg, DeceptionAnim, 0, DeceptionCastFx)); // Surround; caster-only FX (user: FX only on the animation, not the targets)

    // ── UNIQUE NOVELTY KITS ── real names/damage from the spreadsheet's weapon-summary tab that are NOT
    // variants of the 10 "of X" specials above (each has its own distinct pair of ability names). Icons from
    // the icons/anim tab where listed; where the sheet leaves the icon column blank, a sensible existing icon
    // is reused as a stand-in (flagged per kit) rather than shipping 0/blank, same convention as the Medic
    // file's own MeleeIcon fallback.
    private const int EverlovingMeleeIcon = 30203; // Secret Admirer (BASIC ATTACKS tab)
    private const int EverlovingSpecialIcon = 30190; // Heart Breaker (SUPER ATTACKS tab)
    private static readonly NinjaWeapon EverlovingKit = new(
        // Everloving Edge (item 76810), L16, CONFIRMED. No FX/anim listed for either ability in the sheet —
        // falls back to the generic melee-swing anim/hit-fx for both, same as Medic's own no-FX novelty kits.
        new("Secret Admirer", EverlovingMeleeIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Heart Breaker", EverlovingSpecialIcon, 6575, MeleeAnimation, MeleeHitFx));

    private const int CandyMeleeIcon = 28474; // Stalking Stuffer (BASIC ATTACKS tab)
    private const int CandySpecialIcon = 27727; // Candy Hurricane (SUPER ATTACKS tab)
    private static readonly NinjaWeapon CandyStickKit = new(
        // Candy Stick Sword (item 76560), CONFIRMED but the sheet gives a per-PLAYER-LEVEL scaling table
        // (279/444/776/1357/2372 melee, 977/1544/2716/4750/8302 special for levels 1/4/8/12/16) rather than one
        // fixed number — this server has no per-item level-scaling mechanic, so (same convention as the Medic
        // file's Power Fist) the top-rank (L16) numbers are used as the single representative pair. No FX/anim
        // listed for either ability.
        new("Stalking Stuffer", CandyMeleeIcon, 2372, MeleeAnimation, MeleeHitFx),
        new("Candy Hurricane", CandySpecialIcon, 8302, MeleeAnimation, MeleeHitFx));

    private const int NatureClawMeleeIcon = 39211; // Feral Swipe (BASIC ATTACKS tab)
    private const int NatureClawSpecialIcon = 39237; // Feral Spirit (SUPER ATTACKS tab)
    private static readonly NinjaWeapon NatureClawKit = new(
        // Ninja's Nature Claw (item 78199), CONFIRMED level-scaling table like Candy Stick Sword above (top
        // rank 2609/9132 used here) — BUT the sheet's own Notes column flags "Basic and Super attack have
        // additional dmg on top of variable dmg. Needs investigation" (an extra flat +1100/+1600 noted with a
        // "(?)"), so the numbers below are the scaling-table figures only, NOT that extra flat bonus — flagged
        // as the more uncertain of the two "Variable" novelty kits in this file. No FX/anim listed.
        new("Feral Swipe", NatureClawMeleeIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Feral Spirit", NatureClawSpecialIcon, 9132, MeleeAnimation, MeleeHitFx));

    private const int EnergySlashIcon = 45902; // Energy Slash (BASIC ATTACKS tab)
    private const int EnergyStormIcon = 294;   // Energy Storm (SUPER ATTACKS tab)
    private static readonly NinjaWeapon EnergyStormKit = new(
        // Precursor Energy Blade (item 79022), CONFIRMED level-scaling table (top rank 2609/8302 used, same
        // convention as above). CORRECTED 2026-07-29: previously aliased into SoulPowerKit (a themed guess);
        // the sheet gives this weapon its own real, distinct ability names (Energy Slash/Energy Storm), not
        // Shadowslash/Mystical Drain. No FX/anim listed.
        new("Energy Slash", EnergySlashIcon, 2609, MeleeAnimation, MeleeHitFx),
        new("Energy Storm", EnergyStormIcon, 8302, MeleeAnimation, MeleeHitFx));

    private static readonly NinjaWeapon LunarBladeKit = new(
        // Lunar Blade (item 78715), PENDING ("Unknown attack variants; missing tooltip data") level-scaling
        // table (top rank 2372/9132 used). CORRECTED 2026-07-29: previously aliased into MysticismKit (a
        // themed guess borrowing the sword-glow buff mechanic); the sheet gives this weapon its own real,
        // distinct ability names (Moon Slice/Celestial Spin), an ordinary damage pair, not a self-buff. Both
        // icons are blank in the sheet — reuses the bare Flame-Flash melee icon and the Dragonstrike special
        // icon as sensible stand-ins (flagged, not sheet-sourced) rather than shipping 0/blank.
        new("Moon Slice", FlameFlashIconA, 2372, MeleeAnimation, MeleeHitFx),
        new("Celestial Spin", DragonstrikeIcon, 9132, MeleeAnimation, MeleeHitFx));

    private static readonly NinjaWeapon BalloonSwordKit = new(
        // Balloon Sword (Reward Version) numbers used for ALL 3 named sheet variants (Reward/Coin Shop/
        // Gifting Pinata — our item data doesn't distinguish them, ids 16355-16359/77447), CONFIRMED.
        new("Surprise!", 31008, 2609, MeleeAnimation, MeleeHitFx),
        new("Party Crasher", 291, 8463, MeleeAnimation, MeleeHitFx));

    // weapon def id -> kit. Real client Ninja weapons (Training Sword L1, Blade L4/5, Scythe L8, Jagged Scythe
    // L12, Shadow Blade L16 — the 75090-75119 "of <Special>" item series) + every real novelty/coin-shop/dye
    // item found by exact Comment match against ClientItemDefinitions.json. Numbers cited per-entry above map
    // to the spreadsheet's weapon-summary tab rows; ids verified directly against ClientItemDefinitions.json
    // 2026-07-29 (Comment field, verbatim, batch-grepped).
    private static readonly Dictionary<int, NinjaWeapon> _byWeaponDefId = new()
    {
        // Training Sword (75090-75091, rank 1) — "Flame Flash 12"/"Lightning Strike 4"
        [75090] = Dragonstrike(FlameFlashIconE, 279, 1143),
        [75091] = ThousandStorms(FlameFlashIconE, 254, 889),

        // Blade (75092-75095, rank 5) — "Flame Flash"(bare)/"Lightning Strike"(bare)/"Twisted Edge"(bare)/"Cinder Slash"(bare)
        [75092] = Dragonstrike(FlameFlashIconA, 444, 1998),
        [75093] = ThousandStorms(FlameFlashIconA, 488, 1554),
        [75094] = ShurikenStorm(FlameFlashIconA, 537, 1554),
        [75095] = FlameWave(FlameFlashIconA, 444, 1998),

        // Scythe (75096-75101, rank 8) — "Flame Flash 10"/"Lightning Strike 2"/"Twisted Edge 3"/"Cinder Slash 3"/"Dark Assault 2"/"Ashen Strike 2"
        [75096] = Dragonstrike(FlameFlashIconB, 853, 3492),
        [75097] = ThousandStorms(FlameFlashIconC, 853, 2716),
        [75098] = ShurikenStorm(FlameFlashIconB, 938, 2716),
        [75099] = FlameWave(FlameFlashIconB, 853, 3492),
        [75100] = ShadowArmies(FlameFlashIconB, 776, ShadowArmySpecialEstL8), // melee = confirmed "388 (x2)" summed
        [75101] = SolarFlare(FlameFlashIconB, 853, 2716),

        // Jagged Scythe (75102-75109, rank 12) — "Flame Flash 9"/"Lightning Strike 2"/"Twisted Edge 2"/"Cinder Slash 2"/"Dark Assault"(bare)/"Ashen Strike"(bare)/"Fiery Slice"(bare)/"Mystic Rush"(bare)
        [75102] = Dragonstrike(FlameFlashIconC, 1492, 6107),
        [75103] = ThousandStorms(FlameFlashIconC, 1357, 4750),
        [75104] = ShurikenStorm(FlameFlashIconC, 1641, 4750),
        [75105] = FlameWave(FlameFlashIconC, 1357, 6107),
        [75106] = ShadowArmies(FlameFlashIconC, 1492, ShadowArmySpecialEstL12), // melee = confirmed "746 (x2)" summed
        [75107] = SolarFlare(FlameFlashIconC, 1641, 4750),
        [75108] = DragonBreath(FlameFlashIconC, 1492, 6107),
        [75109] = Mysticism(FlameFlashIconC, 1492), // melee = confirmed "746 (x2)" summed

        // Shadow Blade (75110-75119, rank 16, top tier — all 10 specials) — "Flame Flash 11"/"Lightning Strike 3"/
        // "Twisted Edge 4"/"Cinder Slash 4"/"Dark Assault 3"/"Ashen Strike 3"/"Fiery Slice 2"/"Mystic Rush 2"/"Shadowslash"(bare)/"Hidden Strike"(bare)
        [75110] = Dragonstrike(FlameFlashIconD, 2609, 10674),
        [75111] = ThousandStorms(FlameFlashIconD, 2372, 8302),
        [75112] = ShurikenStorm(FlameFlashIconD, 2870, 8302),
        [75113] = FlameWave(FlameFlashIconD, 2609, 10674),
        [75114] = ShadowArmies(FlameFlashIconD, 2609, ShadowArmySpecialEstL16), // melee = confirmed "1304 (x2)" summed
        [75115] = SolarFlare(FlameFlashIconD, 2870, 8302),
        [75116] = DragonBreath(FlameFlashIconD, 2609, 10674),
        [75117] = Mysticism(FlameFlashIconD, 2609), // melee = confirmed "1304 (x2)" summed
        [75118] = SoulPower(FlameFlashIconD, 2609, 8302),
        [75119] = Deception(FlameFlashIconD, 2609, 5977),

        // ── "Dragon Blade"/"Storm Breaker" (coin store / give) ── real items, but NEITHER has its own
        // dedicated spreadsheet row (their real in-game abilities weren't found in either tab) - themed reuse
        // of the top-tier (Shadow Blade) Dragonstrike/1000-Storms numbers, same as the previous pass, just
        // through the shared factory now instead of a hand-duplicated kit object.
        [13663] = Dragonstrike(FlameFlashIconA, 2609, 10674), [55337] = Dragonstrike(FlameFlashIconA, 2609, 10674),
        [70444] = Dragonstrike(FlameFlashIconA, 2609, 10674), [76470] = Dragonstrike(FlameFlashIconA, 2609, 10674),
        [9031] = ThousandStorms(FlameFlashIconA, 2372, 8302), [13669] = ThousandStorms(FlameFlashIconA, 2372, 8302),
        [55360] = ThousandStorms(FlameFlashIconA, 2372, 8302),

        // ── Unique novelty kits (own distinct ability names, not "of X" variants) ──
        [78715] = LunarBladeKit,     // Lunar Blade — CORRECTED, was wrongly MysticismKit
        [79022] = EnergyStormKit,    // Precursor Energy Blade — CORRECTED, was wrongly SoulPowerKit
        [76810] = EverlovingKit,     // Everloving Edge
        [76560] = CandyStickKit,     // Candy Stick Sword
        [78199] = NatureClawKit,     // Ninja's Nature Claw

        // ── Real named coin-shop/reward weapons found by exact Comment match, single-id or small dye/tint
        // groups, CONFIRMED-sourced unless noted. All ride the Dragonstrike family (Flame-Flash melee variant
        // + Dragonstrike special) per their weapon-summary row.
        [4900] = Dragonstrike(FlameFlashIconA, 279, 1143),     // Amateur Ninja Blade ("Flame Flash 2")
        [48163] = Dragonstrike(FlameFlashIconH, 2372, 10674),  // Butterfly Blade ("Flame Flash 5")
        [68112] = Dragonstrike(FlameFlashIconF, 2609, 10674),  // Elitist's Sword ("Flame Flash 7")
        [48181] = Dragonstrike(FlameFlashIconF, 776, 3492),    // Gemstone Blade ("Flame Flash 8")
        [4818] = Dragonstrike(FlameFlashIconA, 2372, 10674),   // All-Star Ninja Blade ("Flame Flash", bare)
        [48322] = Dragonstrike(FlameFlashIconF, 2372, 10674),  // Molten Dragon Blade ("Flame Flash 8") — CORRECTED, was wrongly DragonBreathKit
        [2269] = Dragonstrike(FlameFlashIconA, 2372, 10674),   // Spider Bite ("Flame Flash", bare)
        [22201] = Dragonstrike(FlameFlashIconB, 1357, 6107),   // Tidal Scythe ("Flame Flash 3")
        [38178] = Dragonstrike(FlameFlashIconB, 2372, 10674),  // Aqua Scythe ("Flame Flash 3")
        [22203] = Dragonstrike(FlameFlashIconG, 2372, 10674),  // Blazing Scythe ("Flame Flash 4")
    };

    // PENDING-sourced (ability-name variant uncertain, "(?)" in the sheet) or exact-name-unresolved entries —
    // damage numbers are still real (from the sheet's own per-weapon-level column), just the basic-attack icon
    // falls back to the bare "Flame Flash" icon since we can't confirm which numbered variant applies. Grouped
    // separately from the CONFIRMED block above for clarity, same "flag the uncertainty, use the number anyway"
    // convention the Medic file's PENDING rows use.
    private static readonly (int Id, int MeleeDmg, int SpecialDmg)[] PendingDragonstrikeSingles =
    [
        (55815, 2372, 10674),  // Magical Essence Shadowblade (L20)
        (22101, 776, 3492),    // Bubbleburst Blade (L4)
        (38084, 444, 1998),    // Forest Root (L1)
        (22102, 444, 1998),    // Nature's Root (L1)
        (4844, 254, 1143),     // Student Ninja Bokken (L1)
        (22100, 1357, 6107), (48132, 1357, 6107), // Juiced Scythe (L8, 2 ids)
        (27936, 2372, 10674), (48133, 2372, 10674), // Frostflame Scythe (L12, 2 ids)
        (22202, 2372, 10674), (48134, 2372, 10674), // Illuminating Scythe (L12, 2 ids)
        (38160, 2372, 10674),  // Toxic Bite (L12)
        (48223, 2372, 10674),  // Batty Scythe (L13)
        (48229, 2372, 10674),  // Winged Scythe (L13)
        (38187, 2372, 10674),  // Fiery Scythe (L16)
    ];

    // weapon def id -> icon var -> DYE/TINT RANGES and multi-id groups: real items, one Comment/NameId per
    // group, several TintId variants of the same base weapon (stats don't vary by dye, only the model's
    // color) — same pattern as the Medic file's own dye-range loops. Field initializers run before this ctor
    // body, so AllWeaponDefIds (snapshotted at the end) picks these up too.
    static NinjaWeaponAbilities()
    {
        // Flying Dragon Sword (L16, "Flame Flash" bare) — CONFIRMED, 10 dye variants.
        foreach (var id in new[] { 38229, 38232, 38235, 38238, 38241, 38244, 38248, 38251, 38255, 38259 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 2372, 10674);

        // Sturdy Summersaber (L16, "Flame Flash" bare) — CONFIRMED, 2 ids.
        foreach (var id in new[] { 37003, 38111 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 2372, 10674);

        // Striking Serpent Sword (L16, "Flame Flash (?)") — PENDING, 10 dye variants.
        foreach (var id in new[] { 30530, 30531, 30532, 30533, 30534, 30535, 30536, 30537, 30538, 30539 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 2372, 10674);

        // Smokey Shadowblade (L16, "Flame Flash (?)") — PENDING, 2 ids.
        foreach (var id in new[] { 23020, 48138 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 2372, 10674);

        // Luminous Shadowblade (L16, "Flame Flash (?)") — PENDING, 2 ids.
        foreach (var id in new[] { 29931, 48135 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 2372, 10674);

        // Glacial Blade (L16, "Flame Flash (?)") — PENDING, 2 ids.
        foreach (var id in new[] { 27930, 48136 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 2372, 10674);

        // Twisting Cobra Blade (L4, "Flame Flash (?)") — PENDING, 9 dye variants.
        foreach (var id in new[] { 38115, 38118, 38121, 38124, 38127, 38130, 38134, 38137, 38142 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 776, 3492);

        // Prowling Rat Bokken (L1, "Flame Flash (?)") — PENDING, 10 dye variants.
        foreach (var id in new[] { 38076, 38079, 38082, 38086, 38089, 38092, 38096, 38099, 38103, 38107 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 444, 1998);

        // Dancing Monkey Bokken (L1, "Flame Flash 6") — CONFIRMED, 10 dye variants.
        foreach (var id in new[] { 30290, 30291, 30292, 30293, 30294, 30295, 30296, 30297, 30298, 30299 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconE, 254, 1143);

        // Diving Hawk Scythe (L8, "Flame Flash (?)") — PENDING, 10 dye variants.
        foreach (var id in new[] { 30410, 30411, 30412, 30413, 30414, 30415, 30416, 30417, 30418, 30419 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 1357, 6107);

        // Soaring Eagle Scythe (L12, "Flame Flash (?)") — PENDING, 10 dye variants.
        foreach (var id in new[] { 38152, 38155, 38158, 38162, 38165, 38168, 38172, 38175, 38180, 38184 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 1357, 6107);

        // Stalking Panther Scythe (L12, "Flame Flash (?)") — PENDING, 10 dye variants.
        foreach (var id in new[] { 30470, 30471, 30472, 30473, 30474, 30475, 30476, 30477, 30478, 30479 })
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, 2372, 10674);

        // Balloon Sword (Reward/Coin Shop/Gifting Pinata, not distinguished in our data) — CONFIRMED, 6 ids.
        foreach (var id in new[] { 16355, 16356, 16357, 16358, 16359, 77447 })
            _byWeaponDefId[id] = BalloonSwordKit;

        foreach (var (id, meleeDmg, specialDmg) in PendingDragonstrikeSingles)
            _byWeaponDefId[id] = Dragonstrike(FlameFlashIconA, meleeDmg, specialDmg);

        AllWeaponDefIds = _byWeaponDefId.Keys.ToArray();
    }

    public static IReadOnlyDictionary<int, NinjaWeapon> ByWeaponDefId => _byWeaponDefId;

    public static readonly int[] AllWeaponDefIds;

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
