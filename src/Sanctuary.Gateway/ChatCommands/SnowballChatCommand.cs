using System;
using System.Linq;

using Sanctuary.Game;
using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
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
    public override string Usage => "[anim <id>] [release <ms>] [model <id>] [scale <f>] [throwfx <id>] [play <id>] [scan [from] [to]]";
    public override string Description => "Snowball tool: grant it, retune the throw, or scan animations.";
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
        }

        SnowballTool.Give(invoker, _resourceManager);
        Reply(invoker, $"[snowball] Granted for {SnowballTool.ToolDurationMs / 1000}s - toolbar slot 3, animation " +
                       $"{SnowballTool.ThrowAnimationId} ({NameOf(SnowballTool.ThrowAnimationId)}), release {SnowballTool.ThrowReleaseMs}ms.");
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
