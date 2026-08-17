using System;
using System.Linq;
using System.Numerics;

using Sanctuary.Game;
using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
using Sanctuary.Game.Zones;
using Sanctuary.Gateway.Handlers;
using Sanctuary.Packet;

namespace Sanctuary.Gateway.ChatCommands;

// Dev tooling for the Snowhill snowball fight: grants the tool, and - the reason this got as big as it is -
// finds the throw animation.
//
// ANSWER (live-confirmed via `scan`): it is 6219, scr_goal_throw_1h_right - the one and only animation in the
// whole game with "throw" in its name. It was passed over for several rounds on the theory that its type
// (14 = Soccer) confined it to the soccer minigame; that was wrong, its loadType 2 = OnDemand means asking
// for it loads it. The lesson worth keeping: an animation's TYPE says what it is for, its LOAD TYPE says
// whether you can have it.
//
// `scan` stays because that reasoning could not have been settled from the data - there is no thrown-weapon
// animation family to find (WieldTypeAnimationMappings.txt does define a wield type 6 "Thrown", but its
// animation group is 0, i.e. nothing), so it came down to watching all of them.
public class SnowballChatCommand : GatewayChatCommand
{
    private readonly IResourceManager _resourceManager;

    public SnowballChatCommand(GatewayServer server, IResourceManager resourceManager)
        : base(server)
    {
        _resourceManager = resourceManager;
    }

    public override string KeyWord => "snowball";
    public override string Usage => "[arena [leave|reset|win|target <n>|score]] [queue [id]] [probe <field> <secs>] [stun <ms>] [guard <ms> <cd>] [special <ms>] [anim <id>] [release <ms>] [model <id>] [scale <f>] [throwfx <id>] [play <id>] [scan [from] [to]]";
    public override string Description => "Snowball tool: grant it, enter Snowball Battles, retune the throw, or scan animations.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    // How long each candidate is held during a scan before the next one plays.
    private const int ScanStepMs = 2000;

    // Every one-shot gesture in the Emote family (AnimationTypes.xml type="4"), plus the two non-emote entries
    // at the end. 6219 is the winner and is listed last only because that is the order it was added in - a
    // reminder that the non-Emote types are worth scanning too, not skipping.
    private static readonly (int Id, string Name)[] Candidates =
    [
        (3301, "emo_angry"), (3302, "emo_applaud"), (3303, "emo_beg"), (3304, "emo_call_person"),
        (3305, "emo_call_pet"), (3306, "emo_cheer"), (3307, "emo_confused"), (3308, "emo_congrats"),
        (3309, "emo_cry"), (3310, "emo_curious"), (3311, "emo_flex"), (3312, "emo_flirt"),
        (3313, "emo_giggle"), (3314, "emo_give"), (3315, "emo_insult"), (3316, "emo_laugh"),
        (3317, "emo_no"), (3318, "emo_omg"), (3319, "emo_point"), (3320, "emo_ponder"),
        (3321, "emo_posing"), (3322, "emo_pray"), (3323, "emo_receive"), (3324, "emo_rofl"),
        (3325, "emo_sad"), (3326, "emo_shrug"), (3327, "emo_shy"), (3328, "emo_sleep_sitting"),
        (3329, "emo_sleep_standing"), (3330, "emo_taunt"), (3331, "emo_thanks"), (3332, "emo_twirl"),
        (3333, "emo_wave"), (3334, "emo_yawn"), (3335, "emo_yes"), (3336, "emo_frustrated"),
        (3337, "emo_trick_bad"), (3338, "emo_trick_good"), (3339, "emo_afraid"), (3340, "emo_fart"),
        (3341, "emo_burp"), (3342, "emo_bow"), (3343, "emo_charge"), (3344, "emo_salute"),
        (3345, "emo_shoo"), (3346, "emo_tap"), (3347, "emo_wait"), (3348, "emo_wink"),
        (3349, "emo_backflip"), (3350, "emo_air_guitar"), (3351, "emo_spraycan"), (3352, "emo_lets_be_friends"),
        (3353, "emo_going_on_quest"), (3354, "emo_hey"), (3355, "emo_please"), (3356, "emo_I_like_that"),
        (3357, "emo_sorry"), (3358, "emo_dont_worry"), (3359, "emo_that_stinks"), (3360, "emo_bye_bye"),
        (3361, "emo_Im_outta_here"), (3362, "emo_peace_out"), (3363, "emo_yo"), (3364, "emo_whats_up"),
        (3365, "emo_hi"), (3366, "emo_click_heels"), (3367, "emo_flip"), (3368, "emo_startled"),
        (3369, "emo_scare"), (3370, "emo_achievement_unlocked"), (3371, "emo_drink"),
        (6219, "scr_goal_throw_1h_right - THE THROW"),
        (1098, "com_2hp_special_08 / Hammer Toss (combat set only)"),
    ];

    public override bool Handle(Player invoker, string[] args)
    {
        var verb = args.Length >= 1 ? args[0].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "anim" when args.Length >= 2 && int.TryParse(args[1], out var animationId):
                SnowballTool.ThrowAnimationId = animationId;
                Reply(invoker, $"[snowball] Throw animation set to {animationId} ({NameOf(animationId)}).");
                return true;

            // How long the snowball is held back so it leaves the hand ON the throw rather than at the start
            // of the wind-up. Like the animation, the release frame isn't in any data - it's tuned by eye.
            case "release" when args.Length >= 2 && int.TryParse(args[1], out var releaseMs):
                SnowballTool.ThrowReleaseMs = Math.Clamp(releaseMs, 0, 2000);
                Reply(invoker, $"[snowball] Throw releases {SnowballTool.ThrowReleaseMs}ms into the animation.");
                return true;

            // The flying snowball's model and size. 1980 sg_snowball_bbe is a real snowball prop; 1056 puts
            // back ProjectileNpc's invisible carrier, where the snowflake trail is the only visual.
            case "model" when args.Length >= 2 && int.TryParse(args[1], out var modelId):
                SnowballTool.ProjectileModelId = modelId;
                Reply(invoker, $"[snowball] Projectile model set to {modelId} (1980 = sg_snowball_bbe, 1056 = invisible carrier).");
                return true;

            case "scale" when args.Length >= 2 && float.TryParse(args[1], out var scale):
                SnowballTool.ProjectileScale = Math.Clamp(scale, 0.05f, 10f);
                Reply(invoker, $"[snowball] Projectile scale set to {SnowballTool.ProjectileScale}.");
                return true;

            // The release sound. Must be a SOUND-ONLY composite - anything carrying particles draws a second
            // burst at the hand that reads as the snowball hitting twice. 0 silences the throw.
            case "throwfx" when args.Length >= 2 && int.TryParse(args[1], out var throwFx):
                SnowballTool.ThrowFxId = throwFx;
                Reply(invoker, $"[snowball] Throw FX = {throwFx} (5158 SFX_OS_IceCrack_Sm, 0 = silent). Sound-only ids only.");
                return true;

            case "play" when args.Length >= 2 && int.TryParse(args[1], out var previewId):
                Play(invoker, previewId, 0);
                Reply(invoker, $"[snowball] Playing {previewId} ({NameOf(previewId)}).");
                return true;

            case "scan":
                return Scan(invoker, args);

            // The arena's guard (slot 1 / the "2" key). Its bubble FX and timings are picks, not measured
            // retail values, so both are tunable without a rebuild.
            case "guardfx" when args.Length >= 2 && int.TryParse(args[1], out var guardFx):
                SnowballGuard.BubbleFxId = guardFx;
                SnowballGuard.TryGuard(invoker); // show it immediately
                Reply(invoker, $"[snowball] Guard bubble FX = {guardFx}. Candidates: 16437 shield_gold_SPHERE (default, forcefield), " +
                               "53 snowflakes_white_sphere (on-theme), 16124 shield_purple_lg, 5049 shield_swirl_blue, " +
                               "5055 ice_elemental_barrier. Must be a _loop.");
                return true;

            // How long a pile special stays on cooldown after it's thrown. It is never consumed, so this
            // is the only thing pacing it.
            case "special" when args.Length >= 2 && int.TryParse(args[1], out var specialCooldown):
                SnowballSpecials.CooldownMs = Math.Clamp(specialCooldown, 0, 300_000);
                Reply(invoker, $"[snowball] Pile special cooldown = {SnowballSpecials.CooldownMs}ms.");
                return true;

            // How long an arena knockdown lasts. This doubles as how long the victim is untargetable, so
            // it is the single knob for "my next throw goes straight through him".
            case "stun" when args.Length >= 2 && int.TryParse(args[1], out var stunMs):
                SnowballTool.ArenaStunMs = Math.Clamp(stunMs, 0, 10_000);
                Reply(invoker, $"[snowball] Arena knockdown = {SnowballTool.ArenaStunMs}ms (also the window " +
                               "where they can't be hit again).");
                return true;

            // The victory firework show. Fired as repeated one-shots, so both the effect and the beat are
            // tunable - `/snowball arena win` to watch it.
            case "winfx" when args.Length >= 2 && int.TryParse(args[1], out var winFx):
                SnowballArenaZone.FireworkFxId = winFx;
                Reply(invoker, $"[snowball] Firework FX = {winFx}. Candidates: 5354 celebration-medium (default), " +
                               "5353 small, 5349 large, 15588 fireworks-beams_celebration-lg, 15664 fountain_multi, " +
                               "5350 big_blue_celebration.");
                return true;

            case "winfxrate" when args.Length >= 2 && int.TryParse(args[1], out var winRate):
                SnowballArenaZone.FireworkIntervalMs = Math.Clamp(winRate, 200, 10_000);
                Reply(invoker, $"[snowball] Firework beat = {SnowballArenaZone.FireworkIntervalMs}ms.");
                return true;

            // Fire a bare cooldown pair at an arbitrary duration so it's visible WHICH button the client
            // decides to draw it on. MeleeRefresh carries no slot - it sets one global cooldown-end - and
            // LaunchAndLand only renders when its target is a real enemy, so this aims at the nearest one.
            case "cd" when args.Length >= 2 && int.TryParse(args[1], out var cdMs):
                var sweepTarget = invoker.Zone is SnowballArenaZone cdArena
                    ? cdArena.NearestOpponentGuid(invoker)
                    : 0;

                invoker.SendTunneled(new AbilityPacketMeleeRefresh { CooldownMs = cdMs });
                invoker.SendTunneled(new AbilityPacketLaunchAndLand
                {
                    Guid = invoker.Guid,
                    Guid2 = sweepTarget != 0 ? sweepTarget : invoker.Guid,
                    Guid3 = sweepTarget != 0 ? sweepTarget : invoker.Guid,
                    Position = invoker.Position,
                });

                Reply(invoker, $"[snowball] Sent MeleeRefresh({cdMs}ms) + LaunchAndLand (sweep target " +
                               $"{(sweepTarget != 0 ? "a real opponent" : "SELF - no enemy nearby, sweep will not render")}). " +
                               "Which slot went grey / swept?");
                return true;

            // ★ Isolate WHICH op36/13 float drives the ability button's radial LENGTH. The sweep is ~1s
            // regardless of the cooldown, and the real per-ability duration is believed to live in one of
            // eight still-unidentified float offsets in the ability definition.
            //
            //   snowball probe none        - all eight zero (baseline: confirms the ~1s sweep)
            //   snowball probe 6c 15       - ONLY +0x6c = 15s, everything else zero
            //   snowball probe all 15      - all eight at 15s (the old blunt test, kept for comparison)
            //
            // Test one at a time: setting all eight can't tell "nothing drives it" apart from "one of them
            // suppresses it". After each, press the "2" key (guard) and watch the sweep LENGTH.
            case "probe":
                var field = args.Length >= 2 ? args[1].ToLowerInvariant() : null;
                var probeSeconds = args.Length >= 3 && float.TryParse(args[2], out var ps) ? ps : 15f;

                if (field is null)
                {
                    Reply(invoker, "[snowball] probe <none|all|" + string.Join("|", JobWeaponAbilities.ProbeFields) +
                                   "> [seconds]. Currently: " +
                                   (JobWeaponAbilities.ProbeField ?? "all") +
                                   $" @ {JobWeaponAbilities.ProbeSeconds?.ToString() ?? "the real cooldown"}s.");
                    return true;
                }

                if (field != "none" && field != "all" && !JobWeaponAbilities.ProbeFields.Contains(field))
                {
                    Reply(invoker, $"[snowball] '{field}' isn't a probe offset. Try one of: none, all, " +
                                   string.Join(", ", JobWeaponAbilities.ProbeFields) + ".");
                    return true;
                }

                JobWeaponAbilities.ProbeField = field == "all" ? null : field;
                JobWeaponAbilities.ProbeSeconds = field == "none" ? null : probeSeconds;

                // Re-send definitions + toolbar so the change takes effect without re-entering the arena.
                // The client reads a slot's definition when it first sees the slot and won't re-check, so the
                // toolbar has to be rebuilt, not just the definition re-sent.
                JobWeaponAbilities.SendToolbarWithPowerup(invoker, _resourceManager);

                Reply(invoker, field == "none"
                    ? "[snowball] All probe floats cleared. Press 2 - this is the baseline ~1s sweep."
                    : $"[snowball] op36/13 {(field == "all" ? "ALL fields" : "+0x" + field)} = {probeSeconds}s. " +
                      "Press 2 (guard) and watch how long the radial takes to complete.");
                return true;

            // ★ Isolate WHICH LaunchAndLand (op36/4) field carries the cooldown DURATION.
            //
            // A 2026-07-18 note recorded "field 3 = Unknown3 at +0x30 is the cooldown DURATION", live-tested
            // via an `!abil 4 3 5000` command that no longer exists. Wiring +0x30 up did nothing, so that
            // note's FIELD-INDEX -> struct-offset mapping is suspect (off-by-one between a 0-based probe
            // index and a 1-based field name is the obvious candidate). This sweeps them properly.
            //
            //   snowball ll                - list the fields and what is already known about each
            //   snowball ll u3 15000       - send a guard-shaped cast with ONLY Unknown3 = 15000
            //
            // Each send is a full StartCasting -> MeleeRefresh -> LaunchAndLand sequence naming slot 1, so
            // the guard button is the one to watch. Only the named field is set; everything else stays 0.
            case "ll":
                var llField = args.Length >= 2 ? args[1].ToLowerInvariant() : null;
                var llValue = args.Length >= 3 && int.TryParse(args[2], out var lv) ? lv : 15000;

                if (llField is null)
                {
                    Reply(invoker, "[snowball] ll <field> [value]. Untried ints: u2(+0x2c) u3(+0x30) " +
                                   "u5(+0x34) u6(+0x40) u7(+0x44) u8(+0x48) u10(+0x68) u11(+0x74). " +
                                   "NOTE: these were ALL swept 2026-07-19 with no result - see reference_ability_packet_formats. " +
                                   "Use `snowball ll fx` (Flag1+u4=16110) as the field-ORDER control; it should " +
                                   "put a freezing-shot effect on your target.");
                    return true;
                }

                // LaunchAndLand silently no-ops unless the target is a real OTHER entity, so this needs
                // something to aim at. An arena opponent if there is one, otherwise the nearest visible NPC -
                // which means the sweep can be probed solo out in the overworld instead of needing two
                // players in the arena.
                var target = invoker.Zone is SnowballArenaZone llArena ? llArena.NearestOpponentGuid(invoker) : 0;

                if (target == 0)
                {
                    var nearest = invoker.VisibleNpcs.Values
                        .OrderBy(n => Vector3.DistanceSquared(
                            new Vector3(n.Position.X, n.Position.Y, n.Position.Z),
                            new Vector3(invoker.Position.X, invoker.Position.Y, invoker.Position.Z)))
                        .FirstOrDefault();

                    target = nearest?.Guid ?? 0;
                }

                if (target == 0)
                {
                    Reply(invoker, "[snowball] Nothing to target - LaunchAndLand no-ops without a real entity, so " +
                                   "this test would prove nothing either way. Stand near an NPC or an opponent.");
                    return true;
                }

                var launch = new AbilityPacketLaunchAndLand
                {
                    Guid = invoker.Guid,
                    Guid2 = target,
                    Guid3 = target,
                    Position = invoker.Position,
                };

                switch (llField)
                {
                    case "u1": launch.Unknown1 = llValue; break;
                    case "u2": launch.Unknown2 = llValue; break;
                    case "u3": launch.Unknown3 = llValue; break;
                    case "u4": launch.Unknown4 = llValue; break;
                    case "u5": launch.Unknown5 = llValue; break;
                    case "u6": launch.Unknown6 = llValue; break;
                    case "u7": launch.Unknown7 = llValue; break;
                    case "u8": launch.Unknown8 = llValue; break;
                    case "u10": launch.Unknown10 = llValue; break;
                    case "u11": launch.Unknown11 = llValue; break;
                    // The CONTROL, not a cooldown test: the 2026-07-19 map says the a33760 block that plays
                    // an effect on the target only runs with Flag1 (+0x3c) TRUE, and was verified with
                    // 16110 PRJ_archer_freezing-shot_trail. Setting Unknown4 alone does nothing, which is
                    // why the first attempt at this control showed nothing and proved nothing.
                    case "fx":
                        launch.Flag1 = true;
                        launch.Unknown4 = args.Length >= 3 ? llValue : 16110;
                        break;
                    case "none": break;
                    default:
                        Reply(invoker, $"[snowball] '{llField}' isn't a field. Run `snowball ll` for the list.");
                        return true;
                }

                invoker.SendTunneled(new AbilityPacketStartCasting
                {
                    Unknown = invoker.Guid,
                    Unknown2 = target,
                    Animation = 1, // not -1 - the client emotes off that, see SnowballGuard
                    AbilityId = SnowballGuard.ToolbarSlotIndex + 1,
                });
                invoker.SendTunneled(new AbilityPacketMeleeRefresh { CooldownMs = llValue });
                invoker.SendTunneled(launch);

                Reply(invoker, $"[snowball] LaunchAndLand {llField} = {llValue} (slot 1 / the guard button). " +
                               "Watch it for a long sweep or a long grey.");
                return true;

            // Kill switch for the cooldown sweep (LaunchAndLand). Kept only as an escape hatch if it ever
            // misbehaves again - there is no "branch" knob any more, that experiment broke the working
            // packet and was reverted.
            case "sweep" when args.Length >= 2:
                SnowballTool.SendCooldownSweep = !args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
                Reply(invoker, $"[snowball] Cooldown sweep {(SnowballTool.SendCooldownSweep ? "ON" : "OFF")}.");
                return true;

            // Fire the snowball tutorial FTE on demand, or run an arbitrary line of client Lua to find the
            // right receiver for TriggerFirstTimeEvent. `snowball fte` sends every candidate form;
            // `snowball fte <lua...>` sends exactly what is typed.
            // ★ DOES THE CLIENT ACTUALLY EXECUTE WHAT ExecuteScriptPacket DELIVERS? The string is already
            // proven to ARRIVE (a marker turned up in client memory), but arriving and running are different
            // things, and every "is the API name right" test so far has been unable to tell them apart.
            //
            // This runs a script that CONSTRUCTS its result with string.rep, so the resulting text exists in
            // memory only if Lua actually ran - it cannot have come from the script source, unlike any
            // literal. A memory scan for the repeated block then answers it with no API guessing at all.
            case "luaprobe":
                invoker.SendTunneled(new ExecuteScriptPacket
                {
                    Script = "ZZPROBE = string.rep(\"QZXQ\", 12)",
                });
                Reply(invoker, "[snowball] Sent: ZZPROBE = string.rep(\"QZXQ\", 12). " +
                               "If 'QZXQ' x12 shows up in client memory, Lua executed.");
                return true;

            // `snowball fte <id> [name]` fires an arbitrary first-time event - use a known-good one such
            // as 2 (GamedockJobs) as a control before concluding anything about 75 (FtesSnowball).
            // The minigame TYPE drives which HUD the client builds. 4 = COMBAT gives the Goals pane but
            // also the knockout counter, which retail's snowball fight does not show - so the right value
            // is something else. Try one, watch the HUD, repeat.
            // Entry used to send LAUNCH twice, creating two MiniGameStates - the second is what kept
            // coming back. Off by default; turn it on if the HUD or the goal rows don't appear without it.
            case "doublelaunch" when args.Length >= 2:
                SnowballArenaZone.SendSecondLaunch = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                Reply(invoker, $"[snowball] Second LAUNCH {(SnowballArenaZone.SendSecondLaunch ? "ON (2 states)" : "OFF (1 state)")}" +
                               " - re-enter the arena to apply.");
                return true;

            case "type" when args.Length >= 2 && int.TryParse(args[1], out var mgType):
                SnowballArenaZone.MiniGameType = mgType;

                if (invoker.Zone is SnowballArenaZone typeArena)
                {
                    typeArena.ResendMatchState(invoker);
                    Reply(invoker, $"[snowball] MiniGameType = {mgType}, match state re-sent. " +
                                   "Check: Goals pane still there? Knockout counter gone?");
                }
                else
                {
                    Reply(invoker, $"[snowball] MiniGameType = {mgType} (applies next time you enter the arena).");
                }

                return true;

            case "fte" when args.Length >= 2 && int.TryParse(args[1], out var fteId):
                var fteName = args.Length >= 3 ? args[2] : string.Empty;
                SnowballArenaZone.TriggerFte(invoker, fteId, fteName);
                Reply(invoker, $"[snowball] Triggered first-time event {fteId}" +
                               (fteName.Length > 0 ? $" (\"{fteName}\")" : "") +
                               ". Watch the screen AND chat - the client prints its own FTE debug lines.");
                return true;

            case "fte":
                if (args.Length >= 2)
                {
                    var lua = string.Join(' ', args.Skip(1));
                    invoker.SendTunneled(new ExecuteScriptPacket { Script = lua });
                    Reply(invoker, $"[snowball] Ran client Lua: {lua}");
                    return true;
                }

                SnowballArenaZone.SendSnowballFte(invoker, control: true);
                Reply(invoker, "[snowball] Sent a VISIBLE control line first, then every candidate FTE trigger. " +
                               "Did a blue 'LUA TRANSPORT OK / Congrats!' notification appear? " +
                               "YES + no tutorial = Lua runs, the FTE call is wrong. " +
                               "NO = ExecuteScriptPacket doesn't run client Lua at all, and op107 has to be reversed.");
                return true;

            // Targeting feel: how far a throw reaches, and how tight the "I'm pointing at it" cone is.
            // Selection scores ANGLE FROM FACING first and distance second, so during the Snow Days
            // invasion a throw goes at the snowman you're looking at rather than whichever is nearest.
            case "aim" when args.Length >= 2 && float.TryParse(args[1], out var coneDeg):
                var clamped = Math.Clamp(coneDeg, 5f, 90f);
                SnowballTool.AimConeCos = MathF.Cos(clamped * MathF.PI / 180f);

                if (args.Length >= 3 && float.TryParse(args[2], out var range))
                    SnowballTool.ThrowRange = Math.Clamp(range, 4f, 60f);

                Reply(invoker, $"[snowball] Aim cone {clamped:0}deg, range {SnowballTool.ThrowRange:0}. " +
                               "Wider cone = snaps more readily, narrower = you must point at it.");
                return true;

            case "guard":
                if (args.Length >= 2 && int.TryParse(args[1], out var guardMs))
                    SnowballGuard.DurationMs = Math.Clamp(guardMs, 500, 60_000);
                if (args.Length >= 3 && int.TryParse(args[2], out var guardCooldown))
                    SnowballGuard.CooldownMs = Math.Clamp(guardCooldown, 0, 120_000);

                Reply(invoker, $"[snowball] Guard holds {SnowballGuard.DurationMs}ms, cooldown " +
                               $"{SnowballGuard.CooldownMs}ms, bubble FX {SnowballGuard.BubbleFxId}.");
                return true;

            // Snowball Battles - the team-PvP arena in sh_snowball_battle. Dev entry that skips the
            // matchmaking panel entirely.
            case "arena":
                return Arena(invoker, args);

            // Probe SelectQueueForUserPacket (141/12) live: which id opens the Matchmaking panel on its
            // QUEUE LIST rather than jumping to one game's pane. `/snowball queue` re-sends the current
            // value, `/snowball queue <id>` sets it and sends that. This is what Calvin then uses.
            case "queue":
                if (args.Length >= 2 && int.TryParse(args[1], out var queueId))
                    SnowballArenaZone.MatchmakingOpenQueueId = queueId;

                invoker.SendTunneled(new SelectQueueForUserPacket
                {
                    QueueId = SnowballArenaZone.MatchmakingOpenQueueId,
                });

                Reply(invoker, $"[snowball] Sent SelectQueueForUser(141/12) with queue id " +
                               $"{SnowballArenaZone.MatchmakingOpenQueueId} (0 = no pre-selection, 51 = Snowball Fighting).");
                return true;

            // Identify which column of the queue record the panel reads as "N Waiting" and which as
            // "Avg Wait". lobby.lua's QueuesPopulate takes them from columns 3 / 15 / 16 of the queues
            // data source, but the reconstructed field order here doesn't line up cleanly (Param5/Param6
            // duplicate EncounterDescriptionId/EncounterIcon on every row), so this writes a column and
            // re-sends the list: open Matchmaking, run it, watch which number moves.
            case "queuecol":
                return QueueColumn(invoker, args);

            // The decisive version of the above: mark every int field with 1000+its own index, so the
            // panel reports its own column mapping back. `/snowball queuescan reset` restores the row.
            case "queuescan":
                return QueueScan(invoker, args);

            // Send the 141/14 stats feed on demand. With a number it forces that waiting count for the
            // Snowball row, so the two int lists can be told apart without four testers.
            case "stats":
                if (args.Length >= 2 && int.TryParse(args[1], out var waiting))
                    MatchmakingQueueTable.WaitingOverride = waiting;

                MatchmakingQueueTable.SendStats(invoker, invoker.Guid);

                Reply(invoker, $"[snowball] Sent QueueStatsResponse(141/14). Snowball waiting = " +
                               $"{(MatchmakingQueueTable.WaitingOverride >= 0 ? MatchmakingQueueTable.WaitingOverride : MatchmakingQueueTable.WaitingIn(MatchmakingQueueTable.SnowballQueueId))}, " +
                               "avg waits 20/87/76/63/61s down the list.");
                return true;
        }

        SnowballTool.Give(invoker, _resourceManager);
        Reply(invoker, $"[snowball] Granted for {SnowballTool.ToolDurationMs / 1000}s - toolbar slot 3, animation " +
                       $"{SnowballTool.ThrowAnimationId} ({NameOf(SnowballTool.ThrowAnimationId)}), release {SnowballTool.ThrowReleaseMs}ms.");
        return true;
    }

    // Snowball Battles entry + match control.
    //
    //   /snowball arena              join the match (teams are auto-balanced, so the second player in
    //                                lands on the other side and you have an opponent)
    //   /snowball arena leave        bail out back to where you came from
    //   /snowball arena reset        wipe the score and roster so the arena can be played again
    //   /snowball arena target <n>   hits to win (retail's own number isn't recorded anywhere in the
    //                                client data - the goal strings only say "enough")
    //   /snowball arena score        read the current score
    //   /snowball arena card         re-raise the end-of-match result card (after a win)
    //   /snowball arena cardat <ms>  how long the fireworks run before the card covers them
    private bool Arena(Player invoker, string[] args)
    {
        var arena = CommandSupport.ZoneManager.GetOrCreateSnowballArena();
        var verb = args.Length >= 2 ? args[1].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            // Deliberately does NOT bail out when the server thinks you're elsewhere: this is the manual
            // escape hatch, and the case worth rescuing is exactly the one where server and client disagree
            // about where you are. SendHome clears the minigame state either way, and drops the teleport
            // when you really are somewhere else.
            case "leave":
                var wasInArena = invoker.Zone == arena; // SendHome moves them, so read this first
                arena.SendHome(invoker);
                Reply(invoker, wasInArena
                    ? "[snowball] Left the arena."
                    : "[snowball] You weren't in the arena - cleared the minigame state anyway.");
                return true;

            case "reset":
                arena.ResetMatch();
                Reply(invoker, "[snowball] Match reset - score 0-0, teams cleared.");
                return true;

            // Hand the win to whichever side the caller is on, so the end-of-match sequence can be tested
            // without playing out 80 hits.
            case "win":
                if (invoker.Zone != arena)
                {
                    Reply(invoker, "[snowball] You have to be in the arena to win it.");
                    return true;
                }

                var winner = arena.ForceWin(invoker);

                Reply(invoker, winner is { } winningTeam
                    ? $"[snowball] {winningTeam} Team wins - referee call, fireworks and exit door incoming. " +
                      "/snowball arena reset to play again."
                    : "[snowball] Couldn't force a win (no team assigned, or the match is already over).");
                return true;

            // Re-raise the end-of-match card on demand, so the rows can be looked at again without playing
            // (or force-winning) another match. Only does anything once a match has been decided - the card
            // reads its winner from the finished match.
            case "card":
                if (invoker.Zone != arena)
                {
                    Reply(invoker, "[snowball] You have to be in the arena to see its result card.");
                    return true;
                }

                arena.ShowResultCard(invoker);
                Reply(invoker, "[snowball] Re-raised the result card. Nothing appeared? The match hasn't been " +
                               "decided yet - /snowball arena win first.");
                return true;

            // How long the referee's call and the fireworks get before the card covers the arena.
            case "cardat" when args.Length >= 3 && int.TryParse(args[2], out var cardDelay):
                SnowballArenaZone.ResultCardDelayMs = Math.Clamp(cardDelay, 0, 60_000);
                Reply(invoker, $"[snowball] Result card goes up {SnowballArenaZone.ResultCardDelayMs}ms after the match is called.");
                return true;

            case "target" when args.Length >= 3 && int.TryParse(args[2], out var target):
                SnowballArenaZone.HitsToWin = Math.Clamp(target, 1, 500);
                Reply(invoker, $"[snowball] Hits to win = {SnowballArenaZone.HitsToWin} (takes effect next match - /snowball arena reset).");
                return true;

            case "score":
                Reply(invoker, $"[snowball] Blue {arena.BlueScore} - {arena.RedScore} Red (first to {SnowballArenaZone.HitsToWin}).");
                return true;
        }

        if (invoker.Zone == arena)
        {
            Reply(invoker, "[snowball] You're already in the arena. Grab a pile and start throwing.");
            return true;
        }

        // Same job rule as the real entry - this command skips the start screen, so it has to do it itself.
        AddMatchRequestPacketHandler.ForceAdventurerJob(invoker);

        // Team is picked BEFORE the teleport because the spawn point and facing both depend on it.
        var (spawn, facing) = arena.PrepareEntry(invoker);
        var team = arena.TryGetTeam(invoker.Guid, out var assigned) ? assigned.ToString() : "?";

        invoker.EncounterReturnPosition = invoker.Position; // come back here when the match ends
        invoker.TeleportToZone(arena, spawn, facing, sky: null, geometryId: 0);

        Reply(invoker, $"[snowball] Entering Snowball Battles on the {team} team - first to {SnowballArenaZone.HitsToWin} hits wins.");
        return true;
    }

    // Stamps 1000+index into every int field of the Snowball row and re-sends the list, so whatever the
    // panel prints as "N Waiting" and "Avg Wait" identifies those two fields outright.
    private bool QueueScan(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        var snowball = MatchmakingQueueTable.Snowball;

        if (conn is null || snowball is null)
            return true;

        if (args.Length >= 2 && args[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            MatchmakingQueueTable.Restore(snowball);
            MatchmakingQueueTable.Send(invoker, invoker.Guid);
            Reply(invoker, "[snowball] Queue row restored.");
            return true;
        }

        MatchmakingQueueTable.StampProbeMarkers(snowball);
        MatchmakingQueueTable.Send(invoker, invoker.Guid);

        Reply(invoker, "[snowball] Every column of the Snowball row now reads 1000+its index. On the panel: " +
                       "\"1013 Waiting\" would mean field 13; an Avg Wait of 16:54 (1014s) would mean field 14. " +
                       "Report both numbers, then /snowball queuescan reset.");
        return true;
    }

    //   /snowball queuecol              list the columns with their current Snowball values
    //   /snowball queuecol <col> <val>  write one and re-send the list to you
    private bool QueueColumn(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        var snowball = MatchmakingQueueTable.Snowball;
        if (snowball is null)
        {
            Reply(invoker, "[snowball] No Snowball Fighting row in the queue table.");
            return true;
        }

        if (args.Length < 3 || !int.TryParse(args[1], out var column) || !int.TryParse(args[2], out var value))
        {
            Reply(invoker, $"[snowball] {MatchmakingQueueTable.ColumnCount} columns: {MatchmakingQueueTable.DescribeColumns()}");
            Reply(invoker, "[snowball] Usage: /snowball queuecol <col> <value> - then watch the Matchmaking row. " +
                           "lobby.lua reads 3, 15 and 16 for the \"N Waiting / Avg Wait\" text.");
            return true;
        }

        var written = MatchmakingQueueTable.TrySetColumn(snowball, column, value);

        if (written is null)
        {
            Reply(invoker, $"[snowball] Column {column} is out of range or not an int/bool.");
            return true;
        }

        MatchmakingQueueTable.Send(invoker, invoker.Guid);

        Reply(invoker, $"[snowball] Snowball Fighting column {column} ({written}) = {value}, queue list re-sent.");
        return true;
    }

    // Plays every candidate in turn, announcing each one as it starts, so the whole set can be watched in one
    // pass and the winner read straight off the chat log. Optional from/to narrow the range for a second look.
    private bool Scan(Player invoker, string[] args)
    {
        var from = args.Length >= 2 && int.TryParse(args[1], out var f) ? f : int.MinValue;
        var to = args.Length >= 3 && int.TryParse(args[2], out var t) ? t : int.MaxValue;

        var selected = Candidates.Where(c => c.Id >= from && c.Id <= to).ToList();

        if (selected.Count == 0)
        {
            Reply(invoker, "[snowball] No candidates in that range.");
            return true;
        }

        Reply(invoker, $"[snowball] Scanning {selected.Count} animations, {ScanStepMs}ms each " +
                       $"(~{selected.Count * ScanStepMs / 1000}s). Note the one that looks like a throw, then: /snowball anim <id>");

        for (var i = 0; i < selected.Count; i++)
        {
            var (id, name) = selected[i];
            var atMs = i * ScanStepMs;

            Play(invoker, id, atMs);

            // The label rides the same delayed queue as the animation, so the chat line and the pose the
            // player is looking at are always the same candidate.
            invoker.SendTunneledToVisibleDelayed(new ChatPacketDebugChat
            {
                Message = $"<font color='#00A0FF'>[snowball] {id}  {name}</font>",
                PrintToChat = true
            }, atMs, sendToSelf: true);
        }

        return true;
    }

    // Same packet path the real throw uses (SetSynchronizedAnimations - the one the boombox dances prove
    // animates a player's own character), so what you see in a scan is exactly what a throw will look like.
    private static void Play(Player player, int animationId, int delayMs)
    {
        var animation = new PlayerUpdatePacketSetSynchronizedAnimations();
        animation.Animations.Add(new PlayerUpdatePacketSetSynchronizedAnimations.Animation
        {
            Guid = player.Guid,
            AnimationId = animationId
        });

        if (delayMs <= 0)
            player.SendTunneledToVisible(animation, sendToSelf: true);
        else
            player.SendTunneledToVisibleDelayed(animation, delayMs, sendToSelf: true);
    }

    private static string NameOf(int animationId) =>
        Candidates.FirstOrDefault(c => c.Id == animationId).Name ?? "unlisted";
}
