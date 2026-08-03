using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Zones;

// INSTANCE (Frostfang Fury): the REAL arena zone for the "Frostfang Growler!" combat encounter, done the
// proper way — a genuine server-side zone the player is TeleportToZone'd into, so tiles/visibility/NPC
// delivery all run through the normal engine pipeline.
//
// The world + coords come from the CLIENT'S OWN DATA (2026-07-01, see docs/STATUS.md):
//   world  = sg_random_encounter_clearing (green grass clearing; matches the Sunrise reference video)
//   center = (136, y≈0.5, 165) radius 100, from sg_random_encounter_clearingAreas.xml ("Bed" AreaDefinition)
//
// ★ ENCOUNTER SPEC — GROUND TRUTH (2026-07-05): the ENTIRE live encounter decoded from the 2014-04-01
// capture (idx 27735-38200; scripts in the session scratchpad, analysis in docs/STATUS.md):
//   * ONE "roamer" wolf pre-spawned before the player even loads (wolf_evil, tint evil_purple,
//     AddNpc Speed=3.0) — it wanders at walk speed and never charges; the video shows it idling
//     around as the player loads in.
//   * 4 WAVES of pack wolves: 6, 9, 10, 10 — each wave 2× wolf_evil (tex 'evil'/'evil_black') and
//     the rest wolf (tex 'snow'/'base_metal'). A wave spawns ~1s after the previous is (nearly)
//     cleared (live triggered at ≤1 alive). Total pack = 1+6+9+10+10 = 36. Max ~12 alive at once.
//   * spawn points are FIXED locations ringing the whole arena (bake of every live spawn below) —
//     wolves appear far away, idle ~2.2s, then get ExpectedSpeed 3.0 + 6.0 + CharacterState 0x8001
//     and CHARGE the player at 6.0. (No proximity aggro — the "roaming pack" look in the video is
//     just far-away wolves running in.)
//   * pack wolves: NameId 104067/116023 ("Frostfang Snarler"), HideNamePlate=1, healthBar=0 —
//     NO overhead plates (video-confirmed); they show as red dots on the minimap via the combat
//     notification. HP 760 (one basic hit). Bites are TINY on the live wire (3-5 vs a 7828-HP ninja).
//   * the ALPHA spawns WITH wave 4: model 176 + tex 'snow'/'snow_blue', scale 1.7, NameId 423045,
//     plate SHOWN + healthBar=1 (the video's floating red name + bar — live sends NO op32/sub9 boss
//     display for him). HP 3800. He charges and bites like the pack.
//   * the Alpha has NO flee threshold: at 0 HP he is "defeated" and the flee IS his death
//     presentation — RemovePlayerGracefully(Animate, Delay=10000) + ExpectedSpeed 6.0: the client
//     runs him off for 10s (video 1:25-1:35). Pack wolves use the same packet with Delay=2000
//     (death clip + 5017 poof).
//   * win moment (all at once): health heart (736) + coin pile (841, Knockback pop + fx, removed
//     ~instantly) at the Alpha's spot; op45/sub3 ObjectiveComplete (green-check "Goal Complete!",
//     id 12642) + op47/sub3 row complete; reward banners; the EXIT DOOR (846 sg_exit_door_01,
//     NameId 4826, scale 1.2, cursor 17, minimap badge ImageId 186) spawns at (145, 0, 173) —
//     NO auto-kick; the player leaves by clicking the door.
//   * the goal 12642 "Scare away the wolves!" is Total=1 with NO per-kill ticks on live.
public sealed class FrostfangArenaZone : CombatEncounterZone
{
    private sealed class FrostfangArenaDefinition : BaseZoneDefinition
    {
    }

    // Real ground height: LIVE TEST 10 (2026-07-02 19:31) — the client settles the player to y≈0.0-0.6
    // across the clearing. (The old "y≈14" reading was wrong and left wolves hovering at canopy height.)
    private const float GroundY = 0.5f;

    // Encounter identity, shared by the details header ints + EncounterStatePacket + PlayerEnter
    // (the live server uses one [encounterId][instanceId] pair across all of them). 174 = the
    // Frostfang Growler activity id (ClientActivityDefinitions).
    public const int EncounterId = 174;
    public const int EncounterInstanceId = 1;

    // Client enum MINI_GAME_TYPE_COMBAT = 4 (IDA). The minigame status handler only shows the objective
    // (goals) pane for a COMBAT-type minigame — see the details packet below.
    private const int CombatMiniGameType = 4;

    // ── Wolf identities: GROUND TRUTH, every field verbatim from the live AddNpc packets ────────────
    private const int SnowWolfModelId = 176;   // wolf.adr        tex 'snow' / tint 'base_metal'
    private const int EvilWolfModelId = 177;   // wolf_evil.adr   tex 'evil' / tint 'evil_black'
    private const int SnarlerSnowNameId = 104067; // "Frostfang Snarler" family string
    private const int SnarlerEvilNameId = 116023;
    private const int RoamerNameId = 115837;   // the pre-spawned evil_purple roamer's live NameId
    private const int AlphaNameId = 423045;    // "Frostfang Alpha"
    private const int PackActiveProfile = 151; // live ActiveProfile on every pack wolf (non-zero also
                                               // keeps the red hostile name resolve — see Npc.Disposition)
    private const int AlphaActiveProfile = 152;

    private const int WolfHealth = 760;        // live: player -2739 basic one-shots; video archer ~3 arrows
    private const int AlphaHealth = 3800;      // live max in the killing HitPointModification
    private const float AlphaScale = 1.7f;     // live AddNpc scale (was 1.6 guessed from video)

    private const float RoamSpeed = 3f;        // live ExpectedSpeed while ambling
    // Charge speed (live 6.0 for all wolves) is the shared MobChaseSpeed override above.
    // The roamer's fight-kickoff HOWL (live idx 28467-28469, both on the roamer, one tick before wave 1's
    // AddNpc burst): a rear-up cast pose + a "commanding shout" composite over its head. THIS is what
    // summons the pack — no wolf spawns until the howl fires. Trigger is PROXIMITY (the live capture shows
    // the player walking straight up to the roamer, 52u -> ~4u, and it howls at close range — not a timer).
    private const int RoamerHowlAnimId = 1111;   // AnimationGroup com_cast_01 (SetAnimation op35/8)
    private const int RoamerHowlFxId = 15226;    // PFX_moire-circles_multi_head_commanding-shout-level-1_loop
    private const int RoamerHowlHoldMs = 1500;   // plant + hold the howl pose (anim + fx) before charging
    private const float RoamerAggroRange = 6f;   // player-approach distance that fires the howl
    private const int AggroDelayMs = 2200;     // live: ES 3.0 + ES 6.0 + state 0x8001 land ~2.2s after AddNpc
    private const int SpawnPoofFxId = 46;      // AddNpc.CompositeEffectId on every live WAVE wolf (not the roamer)
    private const int DeathPoofFxId = 5017;    // the graceful-remove composite effect on every dying wolf
    private const int AlphaFleeMs = 10000;     // graceful-remove Delay on the defeated Alpha (he runs off)
    private const int WolfDeathHoldMs = 2000;  // graceful-remove Delay on pack wolves (death clip plays)

    // CharacterState 0x8001 = live "charging/in-combat" state (bit0 baseline + bit15). Every live wolf
    // toggles 1 -> 0x8001 at its charge moment; the PLAYER toggles the same pair with IsFighting.
    // NOTE our 2026-07-03 test showed bit15 AT SPAWN suppresses an overhead plate, so the Alpha
    // (plated) does NOT get this — video-first. Pack wolves have no plates to lose.
    private const int CharState_Baseline = 0x1;
    private const int CharState_Charging = 0x8001;

    // Waves (live): 6, 9, 10, 10 — two evil wolves in each, Alpha alongside the last wave.
    private static readonly int[] WaveSizes = [6, 9, 10, 10];
    private const int EvilPerWave = 2;
    private const int NextWaveDelayMs = 1000;  // live gap: last kill -> next wave ≈ 0.6-1.3s

    // Every live wolf spawn point (x, y, z), baked verbatim from the 04-01 AddNpc positions — fixed
    // locations ringing the arena (center ~(136,165)); the live server drew from these repeatedly.
    private static readonly Vector3[] SpawnPoints =
    [
        new(166.08f, 0.35f, 197.34f), new(139.08f, 1.64f, 113.27f), new(122.40f, 0.56f, 114.63f),
        new(160.33f, 1.69f, 137.58f), new(102.58f, 0.73f, 203.86f), new(104.37f, 1.56f, 131.93f),
        new( 99.96f, 0.78f, 138.62f), new(157.86f, 0.72f, 127.60f), new(197.86f, 2.03f, 173.21f),
        new(102.50f, 1.44f, 131.17f), new(169.48f, 0.63f, 153.74f), new(120.73f, 1.59f, 111.62f),
        new(140.46f, 1.76f, 113.18f), new( 97.46f, 0.24f, 173.99f), new(101.83f, -0.09f, 190.66f),
        new(111.27f, 0.80f, 125.00f), new(170.80f, 0.48f, 191.63f), new(111.35f, -0.19f, 210.22f),
        new(158.58f, 1.58f, 137.10f), new(136.48f, -0.36f, 209.45f), new( 97.55f, 1.72f, 160.75f),
        new(138.88f, 2.02f, 115.44f), new( 97.03f, 0.26f, 174.44f), new(183.62f, 1.10f, 183.02f),
        new(108.96f, 0.02f, 209.94f), new(183.20f, 0.85f, 163.49f), new(118.60f, 0.94f, 118.90f),
    ];

    // Live one-off actor positions (roamer / Alpha / exit door), verbatim from the capture.
    private static readonly Vector4 RoamerSpawn = new(129.33f, GroundY, 171.81f, 1f);
    private static readonly Vector4 AlphaSpawn = new(154.32f, 1.96f, 209.35f, 1f);
    private static readonly Vector4 DoorSpawn = new(145.0f, 0.0f, 173.35f, 1f);

    // ── Exit door (846 = sg_exit_door_01.adr) — live fields from AddNpc idx 37181 + companions ──────
    private const int DoorModelId = 846;
    private const int DoorNameId = 4826;
    private const float DoorScale = 1.2f;
    private const int DoorInteractRange = 125;
    private const int DoorActiveProfile = 28;
    private const int DoorCursorId = 17;         // live NpcRelevance entry for the door
    private const int DoorMinimapImageId = 186;  // live AddNotifications badge (minimap exit icon)
    private const int DoorBadgeType = 7;
    private const int DoorBadgeUnknown3 = 102;

    // Coin-pile pop at the win (841 = loot_coins_01.adr): knocked outward and removed ~instantly.
    private const int CoinsModelId = 841;
    private const int CoinsNameId = 139649;
    private const int CoinsPopFxId = 5192;       // PlayCompositeEffect on the coins at the win moment
    private const float CoinsKnockMagnitude = 0.0712f; // live Knockback magnitude

    // Chase-and-bite AI now runs on the shared CombatEncounterZone tick (TickMobCombat / TickMobReturnHome).
    // Wolves charge a touch faster than the pre-spawned zones' default 5; everything else uses the base tuning.
    protected override float MobChaseSpeed => 6f;

    // Defeated-Alpha flee run (video 1:25-1:35: he turns and sprints off into the fog until he's gone).
    private const float FleeSpeed = 9f;        // a touch faster than the chase so he clearly gets away
    private const float FleeDespawnRadius = 90f; // ~arena edge from center (136,165), r100 playable

    // Optional spawn override pinned live via the "!arena set" chat command (fine-tuning).
    public static Vector4? SpawnOverride;

    // Client movement gate (OnPlayerUpdatePosition, RE'd): MovementType must be 1 (CONTROLLER) or
    // 2 (PHYSICS), and the actor's rider must be the invalid-guid sentinel, else op125 updates are
    // dropped. Live: every walking NPC is type 2 (PHYSICS) — that path auto-plays locomotion.
    private const int WolfMovementTypePhysics = 2;

    private readonly IZoneManager _zoneManager;
    private readonly IResourceManager _resourceManager;
    private readonly Random _rng = new();

    // Heart pickups (video: +125 green heal on walk-over; model 736 = powerup_health_buff.adr, the
    // real drop — one is GUARANTEED at the Alpha's defeat spot on live; mid-fight drops are random).
    // (Live also dropped one 746 powerup_damage_buff mid-fight; damage buffs are a later task.)
    private const int HeartModelId = 736;
    private const int HeartHeal = 125;            // the green "+125" the video shows
    private const float HeartPickupRange = 2.6f;  // walk-over radius
    private const int HeartDropPercent = 12;      // random mid-fight drop chance per kill
    // Heart pickup FX: the live heart is removed gracefully with composite effect 15032 (the pickup
    // sparkle) — params verbatim from the capture remove (Animate=0, Delay=0, EffectDelay=5000).
    private const int HeartPickupFxId = 15032;
    // ★ WHAT THE HEART ACTUALLY IS (SOLVED 2026-07-05, wiki + 04-01 capture + video math). The health
    // powerup (model 736 powerup_health_buff — the ONLY combat "heart"; the sg_icon_* pickups are
    // Demolition Derby, unrelated) does TWO things on pickup:
    //   (1) a FLAT +125 heal — the wiki's "Small Heart: heals a low amount of your own health" (the
    //       green number). Video proof: archer at 417/500 -> +125 -> 542, seen as 542/665.
    //   (2) a TEMPORARY +33% MAX-HP BUFF for ~15s: MaxHealth ×1.33 (archer 500->665, ninja 7828->10411,
    //       both exactly ×1.33), healed to the new full, reverted ~15s later.
    // It is NOT heal-over-time — both parts land instantly; only the buff (and its FX) linger 15s. The
    // ninja showed no +125 float only because he grabbed it at full HP (flat heal on a full bar is
    // invisible; his visible gain was the buff fill). We do the flat +125 + the 15s looping shower now;
    // the real ×1.33 max-HP buff + revert needs the player HP pool (STATUS.md task).
    //
    // THE HEALING STATUS EFFECT (GROUND TRUTH, 04-01 idx 37215-37223): composite 15921 =
    // PFX_magic-heal_red_head_shower_lg_loop_raised (the LOOPING over-head heal shower + trail) is
    // attached to the player via an effect TAG (op35/sub41), held ~15s, then stopped (op35/sub42) —
    // NOT the one-shot 16324 blip we used before. The status-effect ICON under the portrait is driven
    // by the effect-tag entries (op38/sub16, 3 per pickup, server-defined effect ids 61401-61403);
    // that 97-byte format is complex/server-authored (embeds a float + source guid + effect refs) and
    // is a TODO — the looping composite below is the visible above-head heart/trail.
    private const int HealShowerFxId = 15921;  // looping over-head heal shower + trail
    private const int HealShowerMs = 15000;    // live buff duration (14.88s measured) — the ~15s shower
    private int _healTagCounter = 300;         // unique effect-tag ids for concurrent heart pickups
    private readonly List<Npc> _hearts = [];

    // Charging / SlotAngle / NextAttackTicks / Home / Idling / Planted now live on the shared EncounterMobState.
    private sealed class WolfState : EncounterMobState
    {
        public long ChargeAtTicks;   // Environment.TickCount64 when the charge kicks in
        // Roamer wander state
        public bool IsRoamer;
        public bool Howled;          // roamer has howled; standing in the pose until ChargeAtTicks, then charges
        public Vector2? WanderTarget;
        public long WanderPauseUntil;
    }

    private readonly object _stateLock = new();
    private readonly List<Npc> _wolves = [];
    private readonly Dictionary<ulong, WolfState> _wolfStates = [];
    private Npc? _alpha;
    // The DEFEATED Alpha while he RUNS AWAY (video: he never dies on screen — at 0 HP he turns and flees
    // to the fog). Kept OUT of _wolves so the normal chase/straggler-cleanup ignore him; the AI loop
    // drives his flee run and despawns him at the timeout / arena edge.
    private Npc? _fleeingAlpha;
    private long _alphaFleeUntilTicks;
    private int _waveIndex;        // next wave to spawn (0-based into WaveSizes)
    private bool _waveScheduled;
    private bool _roamerEngaged;   // set once the roamer has howled + spawned wave 1 (gates the kickoff)
    private int _killedSnarlers;
    private bool _won;
    private int _encounterRun; // bumped every StartEncounter; stops stale AI loops

    // PARTY CO-OP: the players currently in this arena instance. The encounter runs ONCE (started by
    // the first entrant = the party leader who pressed GO!); co-entrants join the running fight rather
    // than resetting it. Every shared encounter packet is Broadcast to all of them, so a solo player
    // (party of one) behaves exactly as before. The AI still ANCHORS on the first entrant (_anchor)
    // for wolf targeting — v1 co-op: wolves chase the leader, the whole party fights them.
    private readonly List<Player> _activePlayers = [];
    private Player? _anchor;

    // Knockout counter/limit — top-left combat HUD (op39/sub23 MiniGameKnockOut, Max=5 ground-truthed
    // from the 2014-04-01 burst idx 28043/28060/28071). Solo = 5 on live.
    // (KnockoutLimit + the knockout/fail/revive lifecycle now live in CombatEncounterZone.)

    // THE Goals-window goal (video: the panel shows only this). id 12642 / NameId 104176 =
    // "Scare away the wolves!" (confirmed live 2026-07-03). GROUND TRUTH (launch decode): the live
    // goal is Total=1 (a one-shot "deal with the pack" flag) — NO per-kill count ticks; it completes
    // in one go at the win via op45/sub3 + op47/sub3.
    private const int GoalScareWolves = 12642;
    private const int GoalScareWolvesNameId = 104176;
    private const int GoalScareWolvesDescId = 104177;

    // PRIZES — the offer popup's reward list AND the victory loot-wheel slices (both render from the
    // details packet's PREVIEW reward bundle; see RewardEntry). GROUND TRUTH 2026-07-04: decoded verbatim
    // from the real 04-01 launch packet (idx 28053) — and that player was ALSO a ninja, so this IS the
    // correct job set for us (icons/names/ids cross-checked against ClientItemDefinitions.json).
    // Job dependence is server-side: live picks the set for the player's ACTIVE job and stamps
    // MiniGameInfo.ProfileType with the job CATEGORY (2 = combat jobs, Profiles.json Type).
    public const int CombatProfileType = 2;
    public static List<RewardEntry> NinjaPrizePreview() =>
    [
        new() { Hidden = true,  IconId = 2483, TintId = 234, NameId = 133217, ItemDefId = 76209, DisplayName = "Kusa Ninja Tabi Boots" },
        new() {                 IconId = 3717, TintId = 264, NameId = 131152, ItemDefId = 75408, DisplayName = "Ninja's Power Shard of Regeneration I" },
        new() {                 IconId = 3229, TintId = 247, NameId = 131975, ItemDefId = 75091, DisplayName = "Ninja's Training Sword of 1000 Storms" },
        new() {                 IconId = 1198, TintId = 0,   NameId = 131129, ItemDefId = 75385, DisplayName = "Ninja's Necklace of Vitality I" },
        new() { Hidden = true,  IconId = 973,  TintId = -1,  NameId = 6666,   ItemDefId = 10482, DisplayName = "Battle Item Mystery Pack" },
    ];
    // Real preview bundle values, IDA-verified 2026-07-04 (bundle U2 = Num Coins, U3 = Experience):
    // 10 coins, 0 XP. The encounter's XP was granted by the GOAL's own reward bundle on live — that's
    // EncounterXp below, granted for real in WinEncounter via the job XP/level system.
    public const int PrizeCoins = 10;
    public const int PrizeXp = 0;

    // Job XP granted at the encounter win (live: 10, delivered by the goal's own reward
    // bundle rather than the wheel preview — the popup preview correctly keeps showing 0 XP).
    public const int EncounterXp = 10;

    // Per-kill XP (added 2026-07-29, live feedback: "dungeon/encounter enemies should give a small amount
    // of exp when killing them") - see EncounterArenaZone.PerKillXp's header comment for the full reasoning;
    // same small-trickle convention, scaled to this encounter's own EncounterXp (10). The Alpha doesn't get
    // a per-kill bump here - his defeat already triggers WinEncounter's full EncounterXp immediately.
    private const int PerKillXp = 2;

    // ARCHER set — the REFERENCE VIDEO's ground truth (its player was an archer; popup frame at 0:09
    // shows exactly these three): Power Shard of Vitality I / Ring of Regeneration I / Bow of Volleys —
    // the mirror of the ninja structure (shard + training weapon + jewelry). The two HIDDEN slots
    // aren't visible in the video, so: the boots are INFERRED by tier index (ninja hidden boot = its
    // costume family's TIER 2, archer tier 2 = Hen Feather; 11 tiers in both families) and the
    // Mystery Pack slot is the shared consumable prize.
    public const int ArcherProfileId = 35; // Profiles.json "Archer" (Type 2 = combat category)
    public static List<RewardEntry> ArcherPrizePreview() =>
    [
        new() { Hidden = true,  IconId = 4939, TintId = 247, NameId = 132741, ItemDefId = 75733, DisplayName = "Hen Feather Archer Boots" },
        new() {                 IconId = 3721, TintId = 230, NameId = 130968, ItemDefId = 75224, DisplayName = "Archer's Power Shard of Vitality I" },
        new() {                 IconId = 547,  TintId = 0,   NameId = 130924, ItemDefId = 75180, DisplayName = "Archer's Ring of Regeneration I" },
        new() {                 IconId = 3104, TintId = 228, NameId = 131884, ItemDefId = 75000, DisplayName = "Archer's Bow of Volleys" },
        new() { Hidden = true,  IconId = 973,  TintId = -1,  NameId = 6666,   ItemDefId = 10482, DisplayName = "Battle Item Mystery Pack" },
    ];

    // WARRIOR/WIZARD/BRAWLER sets (2026-07-26) — filled in following the exact pattern documented above
    // (tier-2 costume boots hidden slot + Power Shard of X I + tier-1 starter weapon + jewelry of Y I +
    // shared Mystery Pack), sourced from ClientItemDefinitions.json's real per-job 75xxx item block, same
    // as Ninja/Archer. No live capture exists for these 3 jobs (unlike Ninja/Archer), so two things are
    // inferred rather than ground-truthed: (1) the tier-2 boots are picked by matching each job's own
    // 11-tier costume family's TextureAlias "-L2" suffix, the same tier index Ninja's Kusa/Archer's Hen
    // Feather use; (2) the shard/jewelry STAT pairing mirrors Ninja's (Regeneration shard + Vitality
    // necklace) rather than Archer's opposite pairing, since Ninja is the majority pattern among the two
    // known-real examples - genuinely a guess, flagged rather than silently presented as confirmed.
    public const int WarriorProfileId = 32; // Profiles.json "Warrior" (Type 2 = combat category)
    public static List<RewardEntry> WarriorPrizePreview() =>
    [
        new() { Hidden = true,  IconId = 5432, TintId = 228, NameId = 133327, ItemDefId = 76319, DisplayName = "Standard Action Warrior Hightops" },
        new() {                 IconId = 3717, TintId = 232, NameId = 131217, ItemDefId = 75473, DisplayName = "Warrior's Power Shard of Regeneration I" },
        new() {                 IconId = 3120, TintId = 228, NameId = 132004, ItemDefId = 75120, DisplayName = "Warrior's Cudgel of Spinning" },
        new() {                 IconId = 1198, TintId = 0,   NameId = 131194, ItemDefId = 75450, DisplayName = "Warrior's Necklace of Vitality I" },
        new() { Hidden = true,  IconId = 973,  TintId = -1,  NameId = 6666,   ItemDefId = 10482, DisplayName = "Battle Item Mystery Pack" },
    ];

    public const int WizardProfileId = 12; // Profiles.json "Wizard" (Type 2 = combat category)
    public static List<RewardEntry> WizardPrizePreview() =>
    [
        // Tier-2 boots are gender-split for this job (unlike every other job here, a single unisex record) -
        // this is the male variant (76387/icon 5080); female = 76388/icon 5014, same NameId 133395.
        new() { Hidden = true,  IconId = 5080, TintId = 228, NameId = 133395, ItemDefId = 76387, DisplayName = "Novice Wizard Shoes" },
        new() {                 IconId = 3717, TintId = 264, NameId = 131282, ItemDefId = 75538, DisplayName = "Wizard's Power Shard of Regeneration I" },
        new() {                 IconId = 3158, TintId = 242, NameId = 132034, ItemDefId = 75150, DisplayName = "Wizard's Sparkle Twig of Shock" },
        new() {                 IconId = 1198, TintId = 0,   NameId = 131259, ItemDefId = 75515, DisplayName = "Wizard's Necklace of Vitality I" },
        new() { Hidden = true,  IconId = 973,  TintId = -1,  NameId = 6666,   ItemDefId = 10482, DisplayName = "Battle Item Mystery Pack" },
    ];

    public const int BrawlerProfileId = 43; // Profiles.json "Brawler" (Type 2 = combat category)
    public static List<RewardEntry> BrawlerPrizePreview() =>
    [
        // CORRECTED 2026-07-26: the wiki's real "Bonus Rewards" table (freerealms.fandom.com, Cracked Claw
        // Caverns) names Brawler's set "Saved by the Bell", not "Bum Rush" - the old entry here was an
        // inferred guess (tier-2 by TextureAlias "-L2" match) that turned out wrong on both the set AND the
        // tier (ClientItemDefinitions confirms "Saved by the Bell Brawler Boots" is TextureAlias "...-L1",
        // Id 75854, NameId 132862, Icon 4974/228 - a real, verified entry, not a guess like before).
        new() { Hidden = true,  IconId = 4974, TintId = 228, NameId = 132862, ItemDefId = 75854, DisplayName = "Saved by the Bell Brawler Boots" },
        new() {                 IconId = 3717, TintId = 264, NameId = 131022, ItemDefId = 75278, DisplayName = "Brawler's Power Shard of Regeneration I" },
        new() {                 IconId = 3131, TintId = 242, NameId = 131914, ItemDefId = 75030, DisplayName = "Brawler's Mallet of Sweeps" },
        new() {                 IconId = 1198, TintId = 0,   NameId = 130999, ItemDefId = 75255, DisplayName = "Brawler's Necklace of Vitality I" },
        new() { Hidden = true,  IconId = 973,  TintId = -1,  NameId = 6666,   ItemDefId = 10482, DisplayName = "Battle Item Mystery Pack" },
    ];

    // The reward set for the player's ACTIVE JOB — live behavior: the interact/launch packets
    // carry no profile, the SERVER picks the set for the player's active job and stamps only the job
    // CATEGORY (ProfileType=2, combat). Ninja = 04-01 capture ground truth; Archer = reference-video
    // ground truth (3 visible) + tier-2 boot inference; Warrior/Wizard/Brawler = the sets above.
    // CORRECTED 2026-07-26: this used to be a 2-way ternary (Archer vs. "everything else falls back to
    // Ninja") - meaning Warrior/Wizard/Brawler players were shown Ninja's reward set the whole time, not
    // their own job's gear, despite the comment above already documenting the intended per-job pattern.
    // The SAME set must be used at offer, launch, AND the win-time wheel packet — the client resolves
    // the wheel's landing slice by matching NameId against the launch packet's stored preview rows.
    public static List<RewardEntry> GetPrizePreviewFor(Player player) =>
        player.ActiveProfileId switch
        {
            ArcherProfileId => ArcherPrizePreview(),
            WarriorProfileId => WarriorPrizePreview(),
            WizardProfileId => WizardPrizePreview(),
            BrawlerProfileId => BrawlerPrizePreview(),
            _ => NinjaPrizePreview(),
        };

    // The goal, defined inline in the launch details packet. GROUND TRUTH: live 12642 ships
    // Status=1, Count=0, Total=1, Unknown8=0.
    private static IEnumerable<EncounterObjective> EncounterObjectives =>
    [
        new EncounterObjective
        {
            ObjectiveId = GoalScareWolves, NameId = GoalScareWolvesNameId, DescriptionId = GoalScareWolvesDescId,
            Status = 1, Count = 0, Total = 1, Unknown8 = 0,
        },
    ];

    private readonly Sanctuary.Game.Quests.IQuestManager _questManager;
    private readonly Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext> _dbContextFactory;

    public FrostfangArenaZone(IServiceProvider serviceProvider)
        : base(CreateDefinition(), serviceProvider)
    {
        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _questManager = serviceProvider.GetRequiredService<Sanctuary.Game.Quests.IQuestManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Sanctuary.Database.DatabaseContext>>();
    }

    private static BaseZoneDefinition CreateDefinition() => new FrostfangArenaDefinition
    {
        Id = 174, // the Frostfang Growler activity id (traceability; the runtime zone Id is assigned by the manager)
        Name = "sg_random_encounter_clearing",
        TileSize = 64,
        // The clearing is tiny (playable r100 around (136,165)); pad the tile grid generously around it.
        StartLongitude = -2,
        EndLongitude = 8,
        StartLatitude = -2,
        EndLatitude = 8,
        Sky = null, // the GO! teleport sends sky_shrouded_gloam.xml (encounter mood); world default otherwise
        // Live player spawn (04-01 capture, first c2s position idx 28214): (130.11, 1.03, 120.04) — the
        // SOUTH edge of the clearing, ~52u from the roaming wolf up at z~172. The player walks that whole
        // stretch north before closing on the roamer (matches the video's long approach). Spawning at the
        // arena centre (136,165) put the player ~10u from the roamer — right on top of it. GroundY+2 keeps
        // the small settle-drop onto real ground.
        SpawnPosition = new Vector4(130.11f, GroundY + 2f, 120.04f, 1f),
        SpawnRotation = Quaternion.Identity,
    };

    // Where GO! drops the player (the pinned override, if the user set one, else the real center).
    public Vector4 EffectiveSpawn => SpawnOverride ?? SpawnPosition;

    #region Zone lifecycle

    public override void OnClientIsReady(Player player)
    {
        // Finish the client's zone-in (same tail the starting zone sends): vitals + "zone data done".
        // Do NOT spawn NPCs here: the client sends ClientIsReady ~0.35s after BeginZoning, while the load
        // screen is still up, and discards every AddNpc sent then (LIVE TESTS 8+9, 2026-07-02).
        EnterAtFullVitals(player); // real max HP + mana so the bar matches the real-damage bites

        player.SendTunneled(new PacketZoneDoneSendingInitialData());
        player.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());

        // Keep the weapon-driven ability toolbar alive in the arena (any kit job — ninja or archer),
        // warming the FX cache so first casts render (see JobWeaponAbilities.PreloadAbilityEffects).
        JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager);
    }

    // The load screen has actually dropped (this is the handler that flips Player.Visible=true), so the
    // client accepts AddNpc from here on. This is the encounter's true start line.
    public override void OnClientFinishedLoading(Player player)
    {
        // Prune anyone who has already left (so a solo re-entry resets a stale instance cleanly).
        ActivePlayers();

        bool first;
        lock (_stateLock)
        {
            if (!_activePlayers.Any(p => p.Guid == player.Guid))
                _activePlayers.Add(player);
            first = _activePlayers.Count == 1;
        }

        if (first)
        {
            // First entrant (the party leader who pressed GO!) — spawn the encounter + start the AI.
            _anchor = player;
            StartEncounter(player);
        }
        else
        {
            // A party member joining the running fight: don't reset it — deliver the combat gate +
            // goals to THEM, and push the currently-alive encounter NPCs so they see the fight.
            _logger.LogInformation("Frostfang arena: {name} joined the party fight (member #{n}).",
                player.Name, _activePlayers.Count);
            DeliverEntrySequence(player, _encounterRun);
            PushLiveEncounterTo(player);
        }
    }

    // Broadcast a shared encounter packet to every player currently in this arena instance.
    // For a solo player this is exactly the old per-player send; for a party it drives everyone.
    protected override void Broadcast(ISerializablePacket packet)
    {
        foreach (var p in ActivePlayers())
            p.SendTunneled(packet);
    }

    // Push the currently-alive encounter NPCs (wolves/alpha/hearts/door) to a player who
    // just joined mid-fight, so the running encounter is visible to them.
    private void PushLiveEncounterTo(Player player)
    {
        List<Npc> live = [];
        lock (_stateLock)
        {
            live.AddRange(_wolves);
            if (_alpha is not null) live.Add(_alpha);
            live.AddRange(_hearts);
            if (ExitDoor is { } exitDoor) live.Add(exitDoor);
        }
        foreach (var npc in live)
        {
            player.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(player);
            SendNpcRelevance(player, npc);
        }
    }

    #endregion

    #region Encounter

    private void StartEncounter(Player player)
    {
        lock (_stateLock)
        {
            foreach (var old in _wolves)
                old.Dispose();
            _wolves.Clear();
            _wolfStates.Clear();
            foreach (var h in _hearts)
                h.Dispose();
            _hearts.Clear();
            _alpha?.Dispose();
            _alpha = null;
            _fleeingAlpha?.Dispose();
            _fleeingAlpha = null;
            ExitDoor?.Dispose();
            SetExitDoor(null);
            _waveIndex = 0;
            _waveScheduled = false;
            _roamerEngaged = false;
            _killedSnarlers = 0;
            _won = false;
            _encounterRun++;

            // The lone ROAMER — live pre-spawns it before the player's launch burst; the video shows
            // it ambling around as the player loads in. It wanders at walk speed until attacked.
            SpawnRoamer(player);
        }

        // THE COMBAT GATE + Goals — delivered per-player (each member needs their own MiniGameState).
        DeliverEntrySequence(player, _encounterRun);

        _logger.LogInformation("Frostfang arena: encounter start for {name} — roamer out, {waves} waves queued.",
            player.Name, WaveSizes.Length);

        StartWolfAi(player, _encounterRun);
    }

    // The per-player combat gate + goals burst (RE'd — see PacketEncounterDataCommon). Sent a
    // beat after the load settles (LIVE TEST 12: the client's zone-in tail resets encounter/UI state
    // right after FinishedLoading, so same-instant delivery is dropped). Called for the anchor at
    // StartEncounter AND for every party member who joins the running fight, so each gets their own
    // MiniGameState (without which op45 goal packets are silently dropped and the goals pane never shows).
    private void DeliverEntrySequence(Player player, int run)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);

                if (player.Zone != this || run != _encounterRun)
                    return;

                // MASTER GATE (RE'd 2026-07-02): the LAUNCH form of the details packet creates the
                // client's MiniGameState. While it's empty, every op45 objective packet is dropped and
                // IsInMiniGame() stays false. Type MUST be COMBAT (4) — the status handler only
                // populates the objective pane for a combat-type minigame.
                EncounterDetailsResponsePacket MakeLaunch() => new()
                {
                    Unknown = EncounterId,          // live header ints = [encounterId][instanceId]
                    Unknown2 = EncounterInstanceId,
                    NameId = 93276,                 // "Frostfang Growler!" (ClientActivityDefinitions Id 174)
                    DescriptionId = 104171,
                    Difficulty = 1,
                    IconId = 1345,
                    MiniGameType = CombatMiniGameType,   // 4 = COMBAT — the goals-pane gate
                    MembersOnly = true, // gates the win screen's "Members Only Bonus" Coins box
                    Launch = true,
                    Objectives = [.. EncounterObjectives],
                    PreviewRewards = GetPrizePreviewFor(player),
                    PreviewCoins = PrizeCoins,
                    PreviewXp = PrizeXp,
                    RewardXp = EncounterXp,
                    MemberCoins = PrizeCoins,
                    ProfileType = CombatProfileType,
                    ActivityId = EncounterId,
                };

                EncounterPacketPlayerEnter MakeEnter(ulong guid) => new()
                {
                    EncounterId = EncounterId,
                    InstanceId = EncounterInstanceId,
                    PlayerGuid = guid,
                };

                // ★ EXACT REAL-SERVER ENTRY SEQUENCE (2014-04-01 capture idx 28043-28224): LAUNCH twice
                // with a PlayerEnter between them (the first Populate fires before the status handler
                // exists; the PlayerEnter brings the HUD up; the second re-fires Populate). The op47 goal
                // row must be in the DS before the PlayerEnter (else ObjectiveListPopulate hides it).
                UiObjectiveAddPacket ScareWolvesRow() => new()
                {
                    ObjectiveId = GoalScareWolves,
                    NameId = GoalScareWolvesNameId,
                };

                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit)); // 28043
                player.SendTunneled(new ObjectiveActivatePacket    // op45 activate (announce)
                {
                    ObjectiveId = GoalScareWolves,
                    Total = 1, // live goal total — a one-shot flag, not a wolf counter
                });
                player.SendTunneled(ScareWolvesRow());   // 28049 — op47 "Scare away the wolves!"
                player.SendTunneled(MakeLaunch());       // 28053 — create state + goals
                player.SendTunneled(MakeEnter(0));       // 28058 — PlayerEnter: showMinigame
                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit)); // 28060
                player.SendTunneled(MakeLaunch());       // 28065 — LAUNCH again: re-fire Populate
                player.SendTunneled(ScareWolvesRow());   // 28069 — op47 row again (real server repeats)
                player.SendTunneled(new MiniGameKnockOutPacket(0, KnockoutLimit)); // 28071
                player.SendTunneled(PacketEncounterDataCommon.CreateCombatRules()); // 28122 — op62
                player.SendTunneled(MakeEnter(player.Guid)); // 28224 — PlayerEnter (player guid)

                player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = true });
                player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = true });
                player.SendTunneled(new EncounterStatePacket
                {
                    EncounterId = EncounterId,
                    InstanceId = EncounterInstanceId,
                    State = 6,
                });

                _logger.LogInformation("Frostfang arena: entry sequence delivered to {name} (run {run}).",
                    player.Name, run);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Frostfang arena: entry-sequence delivery failed.");
            }
        });
    }

    // ── Spawning ─────────────────────────────────────────────────────────────────────────────────────

    // The pre-spawned lone roamer (live: wolf_evil + tint 'evil_purple', AddNpc Speed=3.0,
    // NO spawn poof) — ambles around the mid-arena until attacked, then charges like the pack.
    private void SpawnRoamer(Player player)
    {
        var roamer = CreateWolf(player, EvilWolfModelId, RoamerNameId, "evil", "evil_purple",
            WolfHealth, 1f, RoamerSpawn, showPlate: false, PackActiveProfile, spawnFx: 0, speed: RoamSpeed);
        if (roamer is null)
            return;

        _wolves.Add(roamer);
        _wolfStates[roamer.Guid] = new WolfState
        {
            IsRoamer = true,
            SlotAngle = (float)(_rng.NextDouble() * Math.Tau),
            Home = roamer.Position,
        };

        // The roamer walks from the start (live ES 3.0 shortly after spawn).
        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = roamer.Guid, ExpectedSpeed = RoamSpeed });
        SendWolfMinimapMarkers(player, [roamer.Guid]);
    }

    // Spawn the next wave (caller holds _stateLock): live sizes 6/9/10/10, two wolf_evil in
    // each, the Alpha alongside the final wave, all at the baked live spawn points.
    private void SpawnWave(Player player)
    {
        if (_waveIndex >= WaveSizes.Length)
            return;

        var size = WaveSizes[_waveIndex];
        var isLastWave = _waveIndex == WaveSizes.Length - 1;
        _waveIndex++;
        _waveScheduled = false;

        var newGuids = new List<ulong>(size + 1);

        // Pick distinct spawn points for this wave.
        var points = new List<Vector3>(SpawnPoints);
        for (var i = 0; i < size; i++)
        {
            var pt = points.Count > 0
                ? points[_rng.Next(points.Count)]
                : SpawnPoints[_rng.Next(SpawnPoints.Length)];
            points.Remove(pt);

            var evil = i < EvilPerWave; // live: exactly two wolf_evil per wave
            var wolf = CreateWolf(player,
                evil ? EvilWolfModelId : SnowWolfModelId,
                evil ? SnarlerEvilNameId : SnarlerSnowNameId,
                evil ? "evil" : "snow",
                evil ? "evil_black" : "base_metal",
                WolfHealth, 1f, new Vector4(pt.X, pt.Y, pt.Z, 1f),
                showPlate: false, PackActiveProfile, SpawnPoofFxId, speed: 0f);
            if (wolf is null)
                continue;

            _wolves.Add(wolf);
            _wolfStates[wolf.Guid] = new WolfState
            {
                ChargeAtTicks = Environment.TickCount64 + AggroDelayMs,
                SlotAngle = (float)(_rng.NextDouble() * Math.Tau),
                Home = wolf.Position,
            };
            newGuids.Add(wolf.Guid);
        }

        if (isLastWave)
        {
            // ★ THE ALPHA — spawns WITH the last wave on live (idx 35077, third actor of the burst):
            // big (1.7) snow_blue wolf, plate + health bar SHOWN (the video's floating red name + red
            // bar — the red comes from hostile disposition + the name resolver; live sends NO op32/sub9
            // boss display and no NameColor).
            _alpha = CreateWolf(player, SnowWolfModelId, AlphaNameId, "snow", "snow_blue",
                AlphaHealth, AlphaScale, AlphaSpawn, showPlate: true, AlphaActiveProfile, spawnFx: 0, speed: 0f);
            if (_alpha is not null)
            {
                // The Alpha rides the SAME AI list as the pack (the loop ticks _wolves) so he charges +
                // bites like the others — he was missing from _wolves before, so he just stood there.
                // OnNpcKilled special-cases him (defeat -> flee -> win) via the _alpha reference.
                _wolves.Add(_alpha);
                _wolfStates[_alpha.Guid] = new WolfState
                {
                    ChargeAtTicks = Environment.TickCount64 + AggroDelayMs,
                    SlotAngle = (float)(_rng.NextDouble() * Math.Tau),
                    Home = _alpha.Position,
                };
                newGuids.Add(_alpha.Guid);
            }

            _logger.LogInformation("Frostfang arena: FINAL wave ({n} wolves) + the Frostfang Alpha.", size);
        }
        else
        {
            _logger.LogInformation("Frostfang arena: wave {w}/{total} — {n} wolves inbound.",
                _waveIndex, WaveSizes.Length, size);
        }

        SendWolfMinimapMarkers(player, newGuids);
    }

    // Live: one op35/sub10 AddNotifications per wave — a short "combat" entry per wolf,
    // which is what paints the red enemy dots on the minimap. Broadcast so every party member's
    // minimap shows the pack (the player arg is kept for call-site symmetry).
    private void SendWolfMinimapMarkers(Player player, IReadOnlyList<ulong> guids)
    {
        if (guids.Count == 0)
            return;

        var badge = new PlayerUpdatePacketAddNotifications();
        foreach (var guid in guids)
            badge.Notifications.Add(new NotificationInfo { Guid = guid, Combat = true, Type = 3, Unknown10 = true });
        Broadcast(badge);
    }

    private Npc? CreateWolf(Player player, int modelId, int nameId, string textureAlias, string tintAlias,
        int health, float scale, Vector4 pos, bool showPlate, int activeProfile, int spawnFx, float speed)
    {
        if (!TryCreateNpc(out var npc))
            return null;

        // ★ Every field below mirrors the live AddNpc packets verbatim (04-01 capture decode
        // 2026-07-05). Pack wolves: NameId set but plate HIDDEN + no bar (no overhead UI at all —
        // video-confirmed); the NameId still feeds the target frame when clicked. The Alpha flips
        // showPlate: plate + bar visible -> the floating red name + red bar over his head.
        npc.ModelId = modelId;
        // The Alpha (showPlate) keeps his named plate; the pack shows a NAMELESS plate so their HEALTH BAR
        // still renders (the bar is a nameplate element — a hidden plate meant no bar, only a flash-on-hit).
        npc.NameId = showPlate ? nameId : 0;
        npc.Name = null;
        npc.TextureAlias = textureAlias;
        npc.TintAlias = tintAlias;
        npc.HideNamePlate = false;
        npc.ShowHealthBar = true;
        npc.Scale = scale;
        npc.Disposition = 0;            // hostile
        // Non-zero ActiveProfile makes the client's AddNpc apply re-run the name color resolver AFTER
        // disposition lands -> hostile + NameColor unset = RED (see Npc.Disposition notes). Live uses
        // the real job-profile ids 151 (pack) / 152 (alpha).
        npc.ActiveProfile = activeProfile;
        npc.CompositeEffectId = spawnFx; // 46 = the live spawn poof on wave wolves (0 on roamer/alpha)
        npc.MaxHealth = health;
        npc.Health = health;
        // A combat target, NOT an NPC: no "Press X to talk" interact prompt. (The live capture had 1 here, but
        // the wolves have no InteractAction so the prompt is dead UI — and it made enemies look clickable.
        // IsInteractable=false + the crossed-swords cursor still leaves them attackable, same as the dummy.)
        npc.IsInteractable = false;
        npc.InteractRange = 100;        // live: range 100 on every wolf
        npc.Visible = true;
        npc.CursorId = 11;              // crossed-swords attack cursor (delivered via NpcRelevance)

        // Locomotion: model's own clips (-1), PHYSICS movement, live wire values.
        npc.WalkAnimId = -1;
        npc.RunAnimId = -1;
        npc.StandAnimId = -1;
        npc.MovementType = WolfMovementTypePhysics;
        npc.Speed = speed;              // live: 0 on wave wolves (ExpectedSpeed drives them), 3.0 roamer
        npc.RiderGuid = ulong.MaxValue; // "no rider" invalid-guid sentinel gate

        npc.UpdatePosition(pos, Quaternion.Identity);

        // Push directly so EVERY party member in the arena sees it immediately (co-op). For a solo
        // player this loop runs once = the old single-player push.
        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(npc);
            npc.OnAddVisiblePlayers(p);

            // Live post-spawn burst, in order: UpdateMana then CharacterState baseline.
            p.SendTunneled(new PlayerUpdatePacketUpdateMana { Guid = npc.Guid });
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = npc.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });

            // Clickable attack target (cursor via relevance — same recipe as the training dummy).
            SendNpcRelevance(p, npc);

            // Belt-and-suspenders hostile mark (op35/sub28).
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = npc.Guid, Disposition = 0 });
        }

        return npc;
    }

    // Snapshot of the players currently in this arena instance (co-op recipients). Filters
    // out any who have left (teleported to another zone) and prunes them so a departed member never
    // receives encounter packets and the instance can reset once it truly empties.
    private Player[] ActivePlayers()
    {
        lock (_stateLock)
        {
            _activePlayers.RemoveAll(p => p.Zone != this);
            if (_anchor is not null && _anchor.Zone != this)
                _anchor = _activePlayers.Count > 0 ? _activePlayers[0] : null;
            return [.. _activePlayers];
        }
    }

    // ── AI ───────────────────────────────────────────────────────────────────────────────────────────

    // Chase-the-player AI: position tick + client interpolation; bites use CombatPacketAttackProcessed
    // (live per-bite packet: wolf attacker, player target, fx 5409 / crit 5622).
    private void StartWolfAi(Player player, int run)
    {
        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Frostfang arena: AI loop started (run {run}).", run);

            try
            {
                for (var elapsed = 0; elapsed < 15 * 60 * 1000; elapsed += TickMs)
                {
                    await Task.Delay(TickMs);

                    if (run != _encounterRun)
                    {
                        _logger.LogInformation("Frostfang arena: AI loop exit — superseded by a new run (run {run}).", run);
                        return;
                    }

                    // Target the whole GROUP: each wolf picks its nearest live player every tick, so the pack
                    // spreads across the party and re-targets when a player falls. Loop lifetime is the run + any
                    // players remaining (not one anchor leaving).
                    var players = ActivePlayers();
                    if (players.Length == 0)
                    {
                        _logger.LogInformation("Frostfang arena: AI loop exit — all players left the zone (run {run}).", run);
                        return;
                    }

                    // Heart pickups: walk-over collection heals +125 (video) — any party member can grab them.
                    foreach (var p in players)
                        CollectHearts(p);

                    Npc[] pack;
                    Npc? fleeingAlpha;
                    lock (_stateLock)
                    {
                        pack = [.. _wolves];
                        fleeingAlpha = _fleeingAlpha;
                    }

                    if (pack.Length == 0 && fleeingAlpha is null)
                        continue; // between waves or encounter done

                    var now = Environment.TickCount64;
                    var dt = TickMs / 1000f;

                    // The defeated Alpha runs for the fog (kept out of _wolves so nothing else touches him). He
                    // flees away from the nearest standing player (or any player if the whole party is down).
                    if (fleeingAlpha is not null)
                    {
                        var alphaHere = new Vector3(fleeingAlpha.Position.X, fleeingAlpha.Position.Y, fleeingAlpha.Position.Z);
                        TickFleeingAlpha(NearestLivePlayer(alphaHere, players) ?? players[0], fleeingAlpha, now, dt);
                    }

                    foreach (var wolf in pack)
                    {
                        if (!wolf.IsAlive)
                            continue;

                        WolfState? state;
                        lock (_stateLock)
                            _wolfStates.TryGetValue(wolf.Guid, out state);
                        if (state is null)
                            continue;

                        var here = new Vector3(wolf.Position.X, wolf.Position.Y, wolf.Position.Z);

                        // Whole party down: DISENGAGE — amble back to the spawn post and idle there until someone
                        // revives (shared tick; resets Charging/Planted so the wolf re-engages cleanly). Otherwise
                        // this wolf's target is the nearest player still standing (sticky - see NearestLivePlayerSticky).
                        var tgt = NearestLivePlayerSticky(here, players, state);
                        if (tgt is null)
                        {
                            TickMobReturnHome(wolf, state, dt, now);
                            continue;
                        }

                        var target = new Vector3(tgt.Position.X, tgt.Position.Y, tgt.Position.Z);

                        // ROAMER: amble between random waypoints at walk speed until a player closes in
                        // (proximity — the live trigger) or hits it (OnNpcDamaged). Either fires the howl
                        // via EngageRoamer. Scenery until then, matching the video's load-in wolf. Once it
                        // has howled it stops roaming (falls through to the hold+charge gate below).
                        if (state.IsRoamer && !state.Charging && !state.Howled)
                        {
                            var dxr = target.X - here.X;
                            var dzr = target.Z - here.Z;
                            if (dxr * dxr + dzr * dzr <= RoamerAggroRange * RoamerAggroRange)
                            {
                                _logger.LogInformation("Frostfang arena: player closed in on the roamer -> howl + wave 1.");
                                EngageRoamer(tgt, wolf, state);
                            }
                            else
                            {
                                TickRoamer(tgt, wolf, state, here, now, dt);
                                continue;
                            }
                        }

                        // Standing still: the roamer holding its howl pose, or a wave wolf in its ~2.2s
                        // post-spawn idle — either way, wait out ChargeAtTicks, then charge.
                        if (!state.Charging)
                        {
                            if (now < state.ChargeAtTicks)
                                continue;
                            BeginCharge(tgt, wolf, state);
                        }

                        // CHARGING: converge on an owned slot around the player, plant + bite in range — the
                        // shared tick (identical to the pre-spawned zones; wolves just use MobChaseSpeed=6 and
                        // the pack-wide bite spacing lives on the base attack gate now).
                        TickMobCombat(wolf, state, tgt, target, now, dt);
                    }
                }

                _logger.LogInformation("Frostfang arena: AI loop exit — 15min safety timeout (run {run}).", run);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Frostfang arena wolf AI failed (run {run}).", run);
            }
        });
    }

    // Wander tick: walk to a random waypoint near mid-arena at live walk speed (3.0),
    // pause a beat, pick another. No biting — the roamer is scenery until provoked.
    private void TickRoamer(Player player, Npc wolf, WolfState state, Vector3 here, long now, float dt)
    {
        if (state.WanderTarget is null)
        {
            if (now < state.WanderPauseUntil)
                return;

            // New waypoint within ~14m of the roamer's home spot, kept inside the clearing.
            var angle = (float)(_rng.NextDouble() * Math.Tau);
            var dist = 5f + (float)_rng.NextDouble() * 9f;
            state.WanderTarget = new Vector2(
                RoamerSpawn.X + MathF.Sin(angle) * dist,
                RoamerSpawn.Z + MathF.Cos(angle) * dist);
        }

        var wt = state.WanderTarget.Value;
        var to = new Vector2(wt.X - here.X, wt.Y - here.Z);
        var d = to.Length();

        if (d < 0.5f)
        {
            // Arrived — stand for 1.5-3.5s (send one stopped update so the client halts locomotion).
            state.WanderTarget = null;
            state.WanderPauseUntil = now + 1500 + _rng.Next(2000);
            Broadcast(new PlayerUpdatePacketUpdatePosition
            {
                Guid = wolf.Guid, Position = wolf.Position, Rotation = new Quaternion(0f, 0f, 1f, 0f),
                State = 1, Unknown = 0,
            });
            return;
        }

        var dir = to / d;
        var step = MathF.Min(RoamSpeed * dt, d);
        var newPos = new Vector4(here.X + dir.X * step, MoveToward(here.Y, GroundY, MobYSpeed * dt),
            here.Z + dir.Y * step, wolf.Position.W);
        var rot = new Quaternion(dir.X, 0f, dir.Y, 0f);

        wolf.UpdatePosition(newPos, rot);
        Broadcast(new PlayerUpdatePacketUpdatePosition
        {
            Guid = wolf.Guid, Position = newPos, Rotation = rot, State = 0, Unknown = 0,
        });
    }

    // The live aggro burst, verbatim order: ExpectedSpeed 3.0 -> ExpectedSpeed 6.0 ->
    // CharacterState 0x8001. The Alpha skips the 0x8001 (bit15 at spawn suppressed overhead plates in
    // our 2026-07-03 live test, and his plate must stay visible — video-first).
    private void BeginCharge(Player player, Npc wolf, WolfState state)
    {
        state.Charging = true;
        state.NextAttackTicks = Environment.TickCount64 + 1000 + _rng.Next(1500);

        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = wolf.Guid, ExpectedSpeed = RoamSpeed });
        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = wolf.Guid, ExpectedSpeed = MobChaseSpeed });

        if (!ReferenceEquals(wolf, _alpha))
        {
            Broadcast(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = wolf.Guid,
                Status = (CharacterStatus)CharState_Charging,
            });
        }
    }

    // The roamer's fight-kickoff (live idx 28467-28471): it plants, rears into a commanding
    // howl — SetAnimation com_cast_01 (1111) + PlayCompositeEffect 15226 (moire "commanding-shout" rings
    // over its head), animation and FX FIRING TOGETHER (EffectDelay 0) — and the pack spawns. It holds
    // the pose for RoamerHowlHoldMs (the AI loop then charges it via ChargeAtTicks) so the howl reads
    // before the lunge. Fires exactly once — proximity or a hit on the roamer (both idempotent).
    private void EngageRoamer(Player player, Npc roamer, WolfState state)
    {
        lock (_stateLock)
        {
            if (_roamerEngaged)
                return;
            _roamerEngaged = true;

            // Plant it where it stands so the client stops its wander walk and plays the howl cleanly.
            var facePlayer = new Vector2(player.Position.X - roamer.Position.X, player.Position.Z - roamer.Position.Z);
            var faceLen = facePlayer.Length();
            var faceDir = faceLen > 0.01f ? facePlayer / faceLen : new Vector2(0f, 1f);
            var howlRot = new Quaternion(faceDir.X, 0f, faceDir.Y, 0f);
            Broadcast(new PlayerUpdatePacketUpdatePosition
            {
                Guid = roamer.Guid, Position = roamer.Position, Rotation = howlRot, State = 1, Unknown = 0,
            });

            // The howl — animation and composite together (EffectDelay 0 keeps the FX in sync with the
            // pose; the live 2000 fired the rings ~2s late, which read as "the FX only went as he charged").
            Broadcast(new PlayerUpdatePacketSetAnimation
            {
                Guid = roamer.Guid,
                AnimationId = RoamerHowlAnimId,
            });
            Broadcast(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = roamer.Guid,
                Unknown2 = player.Guid,
                CompositeEffectId = RoamerHowlFxId,
                EffectDelay = 0,
                Position = new Vector4(0f, 0f, 0f, 1f),
                Clear = true,
            });

            // Hold the pose, THEN charge — the loop's charge gate waits out ChargeAtTicks.
            state.Howled = true;
            state.ChargeAtTicks = Environment.TickCount64 + RoamerHowlHoldMs;

            // The pack answers the call — not a moment before.
            SpawnWave(player);
        }
    }

    // The defeated Alpha's flee run: sprint straight AWAY from the player (facing that way,
    // no biting) until the flee timeout or he reaches the arena edge, then a small poof + despawn.
    // Smooth server-driven movement — no death clip, no teleport.
    private void TickFleeingAlpha(Player player, Npc alpha, long now, float dt)
    {
        var here = new Vector3(alpha.Position.X, alpha.Position.Y, alpha.Position.Z);

        var fromCenterX = here.X - 136f;
        var fromCenterZ = here.Z - 165f;
        var distFromCenter = MathF.Sqrt(fromCenterX * fromCenterX + fromCenterZ * fromCenterZ);

        if (now >= _alphaFleeUntilTicks || distFromCenter > FleeDespawnRadius)
        {
            lock (_stateLock)
            {
                if (!ReferenceEquals(_fleeingAlpha, alpha))
                    return; // already handled
                _fleeingAlpha = null;
            }
            Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { alpha.Guid } });
            alpha.GracefulRemoval = (false, 0, 0, DeathPoofFxId, 1000); // quiet poof once he's in the fog
            alpha.Dispose();
            _logger.LogInformation("Frostfang arena: the fled Alpha reached the fog -> despawned.");
            return;
        }

        // Run directly away from the player and face that way.
        var awayX = here.X - player.Position.X;
        var awayZ = here.Z - player.Position.Z;
        var len = MathF.Sqrt(awayX * awayX + awayZ * awayZ);
        var dir = len > 0.01f ? new Vector2(awayX / len, awayZ / len) : new Vector2(0f, 1f);
        var step = FleeSpeed * dt;
        var newPos = new Vector4(
            here.X + dir.X * step,
            MoveToward(here.Y, GroundY, MobYSpeed * dt),
            here.Z + dir.Y * step,
            alpha.Position.W);
        var rot = new Quaternion(dir.X, 0f, dir.Y, 0f);

        alpha.UpdatePosition(newPos, rot);
        Broadcast(new PlayerUpdatePacketUpdatePosition
        {
            Guid = alpha.Guid, Position = newPos, Rotation = rot, State = 0, Unknown = 0,
        });
    }

    // Provoking the roamer (any damage) flips it into a normal charger.
    public override void OnNpcDamaged(Player player, Npc npc)
    {
        lock (_stateLock)
        {
            if (_wolfStates.TryGetValue(npc.Guid, out var state) && state.IsRoamer && !state.Charging)
            {
                _logger.LogInformation("Frostfang arena: the roamer was provoked -> howl + wave 1.");
                EngageRoamer(player, npc, state);
            }
        }
    }

    // ── Hearts ───────────────────────────────────────────────────────────────────────────────────────

    // Drop a heart pickup (736 = powerup_health_buff.adr) — live spawns one at the defeated
    // Alpha's spot; mid-fight drops are random (the video's +125 heal at 1:05).
    private void SpawnHeart(Player player, Vector4 pos)
    {
        if (!TryCreateNpc(out var heart))
            return;

        heart.ModelId = HeartModelId;
        heart.Name = null;
        heart.NameId = 5102381;       // live heart NameId
        heart.Disposition = 1;        // neutral (not a combat target)
        heart.Scale = 1f;
        heart.IsInteractable = false; // auto-collected by walking over it, no click prompt
        heart.InteractRange = 0;
        heart.Visible = true;
        heart.MaxHealth = 0;          // not damageable
        heart.ShowHealthBar = false;
        heart.HideNamePlate = true;
        heart.ActiveProfile = 8;      // live heart AddNpc value
        heart.WalkAnimId = -1;
        heart.RunAnimId = -1;
        heart.StandAnimId = -1;
        heart.MovementType = WolfMovementTypePhysics;
        heart.RiderGuid = ulong.MaxValue;
        heart.UpdatePosition(pos, Quaternion.Identity);

        player.OnAddVisibleNpcs(heart);
        heart.OnAddVisiblePlayers(player);

        lock (_stateLock)
            _hearts.Add(heart);
    }

    // Walk-over heart collection: within range → +125 heal number + green FX, remove the
    // heart with the live pickup effect (graceful remove, fx 15032 — verbatim capture params).
    private void CollectHearts(Player player)
    {
        List<Npc>? collected = null;
        lock (_stateLock)
        {
            for (var i = _hearts.Count - 1; i >= 0; i--)
            {
                var h = _hearts[i];
                var dx = player.Position.X - h.Position.X;
                var dz = player.Position.Z - h.Position.Z;
                if (dx * dx + dz * dz > HeartPickupRange * HeartPickupRange)
                    continue;
                _hearts.RemoveAt(i);
                (collected ??= []).Add(h);
            }
        }

        if (collected is null)
            return;

        foreach (var h in collected)
        {
            // Green "+125" heal number over the player, backed by a REAL heal (2026-07-27 fix - this used
            // to be purely cosmetic, "cosmetic until HP is tracked" per the old comment below; that's the
            // same bug class reported for potions/power-ups once passive regen was turned off in dungeons).
            var healedAmount = player.Heal(HeartHeal);
            var maxHpStat = player.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 0;
            player.SendTunneled(new PlayerUpdatePacketHitPointModification
            {
                Guid = player.Guid,   // heal is self-sourced
                Guid2 = player.Guid,  // ...on the player
                Unknown = true,
                Unknown2 = maxHpStat,
                Unknown3 = player.CurrentHitpoints,
                Unknown4 = healedAmount,
            });

            // ★ THE HEALING STATUS EFFECT (live-faithful): attach the LOOPING heal shower (15921) over
            // the player's head via an effect tag (op35/sub41) — the "heart above his head + healing
            // trail" from the video — then stop it after HealShowerMs (op35/sub42). Sourced from the
            // heart's guid, exactly like the capture.
            var tagId = ++_healTagCounter;
            player.SendTunneled(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = player.Guid,
                TagId = tagId,
                CompositeEffectId = HealShowerFxId,
                SourceGuid = h.Guid,
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(HealShowerMs);
                    player.SendTunneled(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                    {
                        Guid = player.Guid,
                        TagId = tagId,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Frostfang arena: heal-shower stop failed.");
                }
            });

            // Live heart removal: graceful, fx 15032 (the pickup sparkle), params verbatim.
            h.GracefulRemoval = (false, 0, 5000, HeartPickupFxId, 1000);
            h.Dispose();
        }
    }

    // ── Kills / waves / victory ─────────────────────────────────────────────────────────────────────

    public override void OnNpcKilled(Player killer, Npc npc)
    {
        var alphaDown = false;
        var scheduleWave = false;

        lock (_stateLock)
        {
            if (ReferenceEquals(npc, _alpha))
            {
                _alpha = null;
                _wolves.Remove(npc);
                _wolfStates.Remove(npc.Guid);
                _killedSnarlers++;
                alphaDown = true;
            }
            else if (_wolves.Remove(npc))
            {
                _wolfStates.Remove(npc.Guid);
                _killedSnarlers++;

                // Live wave trigger: the next wave runs in when the field is (nearly) clear.
                if (_wolves.Count <= 1 && _waveIndex < WaveSizes.Length && !_waveScheduled && !_won)
                {
                    _waveScheduled = true;
                    scheduleWave = true;
                }
            }
            else
            {
                return; // not an encounter NPC
            }
        }

        // Clear the minimap combat marker for everyone, then the ONE live death packet:
        // RemovePlayerGracefully (Animate=true -> the client plays the wolf's death clip). The graceful
        // removal itself reaches all members via npc.Dispose (the wolf's visible set = all members).
        Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { npc.Guid } });

        if (!alphaDown)
        {
            npc.GracefulRemoval = (true, WolfDeathHoldMs, 0, DeathPoofFxId, 1000);
            var deathPos = npc.Position;
            npc.Dispose();

            // Random mid-fight power-up drop (video: the +125 heart pickup mid-fight is one of the 5 real
            // kinds - see CombatEncounterZone.TryDropPowerup/PowerupSystem - folded into the same roll
            // instead of a heart-only one, so Frostfang gets Energy/Flame Wave/Earth Shard/Super Shield
            // drops too, not just hearts).
            TryDropPowerup(deathPos);
            killer.AwardXp(PerKillXp);

            if (scheduleWave)
                ScheduleNextWave(killer, _encounterRun);
            return;
        }

        // ★ THE ALPHA IS DEFEATED — he FLEES, he does NOT die (video 1:25-1:35: at 0 HP he turns and
        //   runs off into the fog). We keep him alive-but-INVULNERABLE and drive a real run AWAY from
        //   the player in the AI loop (TickFleeingAlpha) until the timeout / arena edge, then poof.
        //   (The earlier build reused the pack "animate + poof" graceful-remove here, which made the
        //   client play his DEATH clip in place — he "died instead of fleeing", the user's report. The
        //   live graceful-remove(animate,delay=10000) is ambiguous; driving the run explicitly
        //   guarantees the video's flee.) He's out of _wolves already (above) + invulnerable, so he
        //   can't be hit while fleeing and nothing else moves him — no teleport stutter.
        _logger.LogInformation("Frostfang arena: the Alpha is DEFEATED -> he flees to the fog; encounter won.");

        var alphaPos = npc.Position;
        npc.Invulnerable = true;
        lock (_stateLock)
        {
            _fleeingAlpha = npc;
            _alphaFleeUntilTicks = Environment.TickCount64 + AlphaFleeMs;
        }
        Broadcast(new PlayerUpdatePacketExpectedSpeed { Guid = npc.Guid, ExpectedSpeed = FleeSpeed });

        // Boss coin drop (ported from EncounterArenaZone.GrantKillCoins, 2026-07-26) - the Alpha is this
        // encounter's only boss, and defeating him doubles as the win trigger, so this fires right
        // alongside the main win rewards below rather than at a separate mid-fight moment.
        GrantKillCoins(killer);

        WinEncounter(killer, alphaPos);
    }

    private const int BossCoinsMin = 3;
    private const int BossCoinsMax = 12;

    private void GrantKillCoins(Player killer)
    {
        var coins = _rng.Next(BossCoinsMin, BossCoinsMax + 1);

        using var dbContext = _dbContextFactory.CreateDbContext();
        var dbCharacter = dbContext.Characters.SingleOrDefault(x => x.Id == Sanctuary.Core.Helpers.GuidHelper.GetPlayerId(killer.Guid));
        if (dbCharacter is null)
            return;

        dbCharacter.Coins += coins;
        dbContext.SaveChanges();
        killer.Coins = dbCharacter.Coins;

        killer.SendTunneled(new ClientUpdatePacketCoinCount { Coins = killer.Coins });
        killer.SendTunneled(new RewardBundlePacket { Coins = coins, Unknown15 = 957 });
        killer.SendTunneled(new ChatPacketDebugChat
        {
            Message = $"<font color='#0000FF'>You receive {coins} coins.</font>",
            PrintToChat = true,
        });
    }

    private void ScheduleNextWave(Player player, int run)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NextWaveDelayMs);

                if (player.Zone != this || run != _encounterRun)
                    return;

                lock (_stateLock)
                    SpawnWave(player);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Frostfang arena wave spawn failed.");
            }
        });
    }

    // The win moment — every beat verbatim from the live capture burst: the Alpha's parting
    // drops (heart + coin pop), the goal completing (green ✓ "Goal Complete!"), the loot wheel + score
    // rows, and the exit door. NO auto-return — the player leaves through the door.
    private void WinEncounter(Player player, Vector4 alphaPos)
    {
        // Clear any pack wolves still alive — on live the Alpha spawns WITH the final wave, so the
        // player can defeat him while stragglers remain, and the win burst removes them (04-01: wolves
        // 0x22/0x26 removed right after the Alpha at 37148). Without this they'd keep biting on the
        // victory screen. Scatter them off with the same graceful poof + clear their minimap dots.
        List<Npc> stragglers;
        lock (_stateLock)
        {
            _won = true;
            stragglers = [.. _wolves];
            _wolves.Clear();
            _wolfStates.Clear();
        }
        foreach (var straggler in stragglers)
        {
            Broadcast(new PlayerUpdatePacketRemoveNotifications { Guids = { straggler.Guid } });
            straggler.GracefulRemoval = (true, WolfDeathHoldMs, 0, DeathPoofFxId, 1000);
            straggler.Dispose();
        }

        // 1) The Alpha's parting drops at his EXACT defeat spot — heart + coin pop (pure theater; the
        //    real reward is the wheel/XP below). Spawned once at the death spot.
        SpawnHeart(player, alphaPos);
        SpawnCoinPop(player, alphaPos);

        // ★ CO-OP: award the win to EVERY party member in the arena (each gets their own goal
        // complete, XP, quest credit, and loot-wheel prize). For a solo player this loops once.
        var enemies = _killedSnarlers;
        var knockoutsLeft = System.Math.Max(0, KnockoutLimit - KnockoutsUsed(player.Guid)); // real remaining lives
        MiniGameGameEndScorePacket MakeScore()
        {
            var s = new MiniGameGameEndScorePacket();
            s.Rows.Add(new MiniGameScoreRow { Name = "scoreEnemiesDefeated", Order = 0, Value = enemies, Points = enemies * 300 });
            s.Rows.Add(new MiniGameScoreRow { Name = "scorePlayerKnockouts", Order = 3, Value = knockoutsLeft, Max = KnockoutLimit, Points = knockoutsLeft * 5000 });
            s.Rows.Add(new MiniGameScoreRow { Name = "scoreTotalScore", Order = 4, Points = enemies * 300 + knockoutsLeft * 5000 });
            return s;
        }

        foreach (var member in ActivePlayers())
        {
            // Goal complete — op45/sub3 (green-check announce) + op47/sub3 (Goals-window row done).
            member.SendTunneled(new ObjectiveCompletePacket { ObjectiveId = GoalScareWolves });
            member.SendTunneled(new UiObjectiveCompletePacket { ObjectiveId = GoalScareWolves });

            // Goal reward XP (drives the member's active-job level bar). The grant banner is held until
            // the wheel stops (see BaseMiniGamePacketHandler.HandleLootWheelStopped) so it lands in ONE
            // combined "here's everything you got" popup with the coins/item, not its own early toast.
            member.AwardXp(EncounterXp);
            member.PendingWheelXp = EncounterXp;

            // Credit any quest whose active goal is "win THIS encounter" (EncounterComplete id 174).
            _questManager.OnEncounterComplete(member, EncounterId);

            // Loot wheel — each member spins their OWN prize (server picks it; the spin is theater).
            // Must be the member's own active-job set (NameId matching — see GetPrizePreviewFor).
            var prizes = GetPrizePreviewFor(member);
            var slice = _rng.Next(prizes.Count + 1); // 0..N-1 = items, N = coins
            var wheel = new MiniGameLootWheelSetItemToLandOnPacket();
            if (slice < prizes.Count)
            {
                member.PendingWheelPrize = prizes[slice];
                member.PendingWheelCoins = 0;
                wheel.Entries.Add(prizes[slice]);
            }
            else
            {
                member.PendingWheelPrize = null;
                member.PendingWheelCoins = PrizeCoins;
                wheel.Coins = PrizeCoins;
            }

            member.SendTunneled(wheel);
            member.SendTunneled(MakeScore());
        }

        // THE EXIT DOOR — spawned once, visible + clickable to all members (each leaves on their own).
        SpawnExitDoor(player);

        _logger.LogInformation("Frostfang arena: encounter WON — wheel armed, exit door out ({kills} kills).", enemies);
    }

    // The live coin-pile pop: loot_coins_01 spawns at the Alpha's spot, gets a Knockback
    // along a random direction + a burst effect, and is removed almost immediately.
    private void SpawnCoinPop(Player player, Vector4 pos)
    {
        if (!TryCreateNpc(out var coins))
            return;

        coins.ModelId = CoinsModelId;
        coins.NameId = CoinsNameId;
        coins.Name = null;
        coins.Disposition = 1;
        coins.Scale = 1f;
        coins.IsInteractable = false;
        coins.InteractRange = 0;
        coins.Visible = true;
        coins.MaxHealth = 0;
        coins.HideNamePlate = true;
        coins.ActiveProfile = 28;     // live coins AddNpc value
        coins.WalkAnimId = -1;
        coins.RunAnimId = -1;
        coins.StandAnimId = -1;
        coins.MovementType = WolfMovementTypePhysics;
        coins.RiderGuid = ulong.MaxValue;
        coins.UpdatePosition(new Vector4(pos.X, pos.Y + 1.5f, pos.Z, 1f), Quaternion.Identity);

        player.OnAddVisibleNpcs(coins);
        coins.OnAddVisiblePlayers(player);

        var angle = (float)(_rng.NextDouble() * Math.Tau);
        player.SendTunneled(new PlayerUpdatePacketKnockback
        {
            Guid = coins.Guid,
            Position = coins.Position,
            Direction = new Vector4(MathF.Sin(angle), 0f, MathF.Cos(angle), 0f),
            Magnitude = CoinsKnockMagnitude,
        });
        player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
        {
            Guid = coins.Guid,
            CompositeEffectId = CoinsPopFxId,
            Position = coins.Position,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150); // live removes the pile ~0.1s after the pop
                coins.GracefulRemoval = (false, 0, 0, 0, 1000);
                coins.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Frostfang arena: coin-pop removal failed.");
            }
        });
    }

    // The live exit door (846 = sg_exit_door_01.adr at (145, 0, 173.35), scale 1.2): AddNpc
    // hostile-then-SetDisposition(neutral) exactly like the wire, cursor 17, minimap exit badge.
    private void SpawnExitDoor(Player player)
    {
        if (!TryCreateNpc(out var door))
            return;

        door.ModelId = DoorModelId;
        door.NameId = DoorNameId;
        door.Name = null;
        door.Disposition = 0;           // live AddNpc ships 0, then flips neutral via sub28 below
        door.Scale = DoorScale;
        door.IsInteractable = true;
        door.InteractRange = DoorInteractRange;
        door.Visible = true;
        door.MaxHealth = 0;
        door.ShowHealthBar = false;
        door.HideNamePlate = false;     // live: plate shown (door name on approach)
        door.ActiveProfile = DoorActiveProfile;
        door.CursorId = DoorCursorId;   // live NpcRelevance cursor 17
        door.WalkAnimId = -1;
        door.RunAnimId = -1;
        door.StandAnimId = -1;
        door.MovementType = WolfMovementTypePhysics;
        door.RiderGuid = ulong.MaxValue;
        door.UpdatePosition(new Vector4(DoorSpawn.X, GroundY, DoorSpawn.Z, 1f), Quaternion.Identity);

        var badge = new PlayerUpdatePacketAddNotifications();
        badge.Notifications.Add(new NotificationInfo
        {
            Guid = door.Guid,
            Combat = false,
            Type = DoorBadgeType,           // live: 7
            Unknown3 = DoorBadgeUnknown3,   // live: 102
            ImageId = DoorMinimapImageId,   // live: 186 (minimap exit icon)
            DescriptionId = 0,
            NameId = DoorNameId,
            SubTextId = -1,
            Unknown8 = true,                // live: minimap-only (no floating icon over the door)
            CompositeEffectId = 0,
            Unknown10 = true                // constant 1 across all live samples
        });

        // CO-OP: the door must be visible + clickable to EVERY party member so each can leave. For a
        // solo player this loops once.
        foreach (var p in ActivePlayers())
        {
            p.OnAddVisibleNpcs(door);
            door.OnAddVisiblePlayers(p);

            // Live companion burst: SetDisposition(neutral), baseline state, cursor relevance, badge.
            // NO vitals packet (the door renders an overhead bar for any value — user-confirmed).
            p.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = door.Guid, Disposition = 1 });
            p.SendTunneled(new PlayerUpdatePacketUpdateCharacterState
            {
                Guid = door.Guid,
                Status = (CharacterStatus)CharState_Baseline,
            });
            SendNpcRelevance(p, door);
            p.SendTunneled(badge);
        }

        SetExitDoor(door);
    }

    // Release the client from the encounter (RE'd exit protocol): remove the minigame
    // state (op39/sub19 — full client-side teardown incl. combat exit for combat-type games) and
    // restore the default combat ruleset (op62) + clear the transient fighting state. Without this
    // the client stays InCombat forever (can't change jobs after leaving — LIVE TEST 11 bug).

    protected override void ReturnHome(Player player, bool immediate)
    {
        if (player.Zone != this)
            return; // already left

        bool won;
        lock (_stateLock)
            won = _won;

        EndEncounterForPlayer(player, won);

        var home = _zoneManager.StartingZone;

        player.TeleportToZone(home, home.SpawnPosition, home.SpawnRotation, sky: null, geometryId: 0);
    }

    // Knockout / fail / revive lifecycle lives in CombatEncounterZone — supply the encounter id + log label.
    // (Bites deal real damage, so hitting 0 HP knocks the player out.)
    protected override int FailEncounterId => EncounterId;
    protected override int FailInstanceId => EncounterInstanceId;
    protected override string EncounterLogName => "Frostfang arena";

    // A bespoke single-arena fight, not a DungeonCatalog "dungeon" - real source (legacy.fanbyte.com/wiki/
    // Combat_(FR)): "Wandering battle instances are allowed 10 knockouts" (vs. 15 for dungeons - see
    // CombatEncounterZone.KnockoutLimit's own comment for the dungeon default this overrides).
    protected override int KnockoutLimit => 10;
    protected override IResourceManager ResourceManagerForPowerups => _resourceManager;

    // MoveToward is the shared CombatEncounterZone helper now.

    #endregion
}
