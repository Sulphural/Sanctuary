using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;

namespace Sanctuary.Gateway.ChatCommands;

// Runs the Snowmen Invaders world event on demand. Without this the only way to see it is to stand near the
// Gifting Tree and wait out its 15-minute interval.
public class SnowmenChatCommand : GatewayChatCommand
{
    public SnowmenChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "snowmen";
    public override string Usage => "[boss|chestfx <id>]";
    public override string Description => "Starts the Snowmen Invaders battle at the Gifting Tree now.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        if (invoker.Zone is not StartingZone zone)
        {
            Reply(invoker, "[snowmen] Only runs in the overworld.");
            return true;
        }

        // The treasure chest's attached glow. It has to stay a SILENT composite - see the ★ note on
        // StartingZone.TreasureShineFxId for why a sound-bearing loop here is what left the endless
        // "raindrops" running after the treasure was claimed. 0 turns the glow off entirely.
        // How many invaders spawn at each snowball pile.
        if (args.Length >= 2 && args[0].Equals("perpile", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var perPile) && perPile >= 0)
        {
            StartingZone.InvadersPerPile = System.Math.Min(perPile, 8);
            zone.RespawnSnowmenInvaders();
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] {StartingZone.InvadersPerPile} invader(s) per snowball pile; wave respawned.");
            return true;
        }

        // The effect played when an invader grabs a present - the rig-independent half of the theft.
        if (args.Length >= 2 && args[0].Equals("stealfx", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var stealFx) && stealFx >= 0)
        {
            StartingZone.InvaderStealFxId = stealFx;
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Invader grab effect = {stealFx} (0 = none).");
            return true;
        }

        // Walk a whole animation range on the invaders, one every few seconds, logging each id as it plays.
        // Watch the snowmen, note when one actually moves, then set it with `snowmen stealanim <id>`.
        if (args.Length >= 3 && args[0].Equals("animsweep", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var sweepFrom) && int.TryParse(args[2], out var sweepTo)
            && sweepTo >= sweepFrom && sweepTo - sweepFrom <= 60)
        {
            var stepMs = args.Length >= 4 && int.TryParse(args[3], out var st) ? System.Math.Clamp(st, 500, 10000) : 2500;
            zone.SweepInvaderAnimations(sweepFrom, sweepTo, stepMs);
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Sweeping animations {sweepFrom}..{sweepTo}, {stepMs}ms apart. " +
                "Each id is logged as it plays - watch for one that actually moves them.");
            return true;
        }

        // Play an animation on every live invader RIGHT NOW. The grab clip has to be found by trying: the
        // snowman rig's animation set isn't listed anywhere readable (AnimationGroups.xml doesn't name it),
        // so this cycles candidates in seconds instead of waiting for each one to walk to the tree.
        if (args.Length >= 2 && args[0].Equals("testanim", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var testAnim))
        {
            var played = zone.PlayInvaderAnimationNow(testAnim);
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Played animation {testAnim} on {played} invader(s). " +
                "If nothing moved, the rig doesn't have that clip - try another.");
            return true;
        }

        // How long the grab clip runs before the invader is put back to idle. A base animation LOOPS until
        // replaced, so this must match the CLIP length - too long and the grab visibly plays twice.
        if (args.Length >= 2 && args[0].Equals("grabclip", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var grabClip))
        {
            StartingZone.InvaderGrabClipMs = System.Math.Clamp(grabClip, 200, 6000);
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Grab clip length = {StartingZone.InvaderGrabClipMs}ms (idle reset deadline).");
            return true;
        }

        // Movement state broadcast while an invader RUNS HOME carrying a present. The client picks its
        // locomotion clip from this, and loc_run on this model is run WITHOUT the present - so 2 (run)
        // strips the gift. 0 stops it forcing a locomotion clip; try 0 / 1 / 2 to see which keeps the gift.
        if (args.Length >= 2 && args[0].Equals("movestate", System.StringComparison.OrdinalIgnoreCase)
            && byte.TryParse(args[1], out var moveState))
        {
            StartingZone.InvaderFleeMovingState = moveState;
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Getaway movement state = {moveState}. Watch whether the present stays in hand.");
            return true;
        }

        // ── Health-bar experiment ───────────────────────────────────────────────────────────────────
        // Swap the invaders' MODEL to confirm the nameplate health bar really is decided by the model's
        // RACE_ID and by nothing the server sends. Dungeon enemy models draw no bar; snowman models do.
        //   snowmen invadermodel 4     -> robgoblin_m_basic   (race 102) - expect NO bar
        //   snowmen invadermodel 10    -> ghostdwarf_m_miner  (race 9)   - expect NO bar
        //   snowmen invadermodel 1907  -> snowman_present     (race 0)   - back to normal, bar returns
        // Respawn the wave (or wait for the next refill) for it to take effect.
        if (args.Length >= 2 && args[0].Equals("invadermodel", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var invaderModel) && invaderModel > 0)
        {
            StartingZone.SnowmanInvaderModelId = invaderModel;
            zone.RespawnSnowmenInvaders();
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Invader model = {invaderModel}; wave respawned. " +
                "Does the nameplate still carry a health bar?");
            return true;
        }

        // ── Invader raid tuning ─────────────────────────────────────────────────────────────────────
        // The invaders walk to the tree, grab a present and run off; they never chase. The grab CLIP is a
        // guess - 6204 scr_def_run_steal_right is the only STEAL animation in the whole table, but it comes
        // from the soccer set and the snowman rig may simply not have it - so it is tunable in-game.
        if (args.Length >= 2 && args[0].Equals("stealanim", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var stealAnim))
        {
            StartingZone.InvaderStealAnimationId = stealAnim;
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Invader grab animation = {stealAnim} (0 = none). " +
                "Candidates: 6204 scr_def_run_steal_right, 3314 emo_give, 3323 emo_receive.");
            return true;
        }

        if (args.Length >= 2 && args[0].Equals("stealtime", System.StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var stealMs))
        {
            StartingZone.InvaderStealMs = System.Math.Clamp(stealMs, 0, 15000);
            CommandSupport.SendSystem(GetConnection(invoker)!,
                $"[snowmen] Invaders linger {StartingZone.InvaderStealMs}ms at the tree before running off.");
            return true;
        }

        if (args.Length >= 1 && args[0].Equals("chestfx", System.StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || !int.TryParse(args[1], out var fxId) || fxId < 0)
            {
                Reply(invoker, $"[snowmen] chest glow is {StartingZone.TreasureShineFxId}. " +
                               "Usage: /snowmen chestfx <id> (16534 large, 16535 medium, 16536 small, 0 off).");
                return true;
            }

            StartingZone.TreasureShineFxId = fxId;
            Reply(invoker, $"[snowmen] chest glow set to {fxId} - takes effect on the next chest.");
            return true;
        }

        if (args.Length >= 1 && args[0].Equals("boss", System.StringComparison.OrdinalIgnoreCase))
        {
            zone.ForceSnowmenBoss();
            Reply(invoker, "[snowmen] Abominable Snowman spawned at the Gifting Tree.");
            return true;
        }

        zone.ForceStartSnowmenInvaders();
        Reply(invoker, "[snowmen] Invader wave started at the Gifting Tree.");
        return true;
    }
}
