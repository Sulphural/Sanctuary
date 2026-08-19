using System.Linq;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Interactions;
using Sanctuary.Packet;

namespace Sanctuary.Gateway.ChatCommands;

// Live probe for the effect that rides the equipped yo-yo (and any other held prop). The yo-yo's standing
// effect lives on its ITEM DEFINITION (CompositeEffectId), so changing it normally means editing
// Resources/ClientItemDefinitions.json and restarting - far too slow to hunt for the right look by eye.
//
// WHY THIS EXISTS: the client ships NO yo-yo-specific effect. Its whole weapon-effect vocabulary is
// WFX_<look>_<colour>_skel_loop and there is no "wind" look anywhere in it, so the retail effect cannot be
// identified from the data - only recognized on sight. This sends op35/31 SlotCompositeEffectOverride,
// which binds a composite effect to an equipped item slot on the spot, so candidates can be flipped
// through in-game in seconds. Whatever wins gets written into the item definition permanently.
//
// The override is client-side only and lasts until replaced or until the item is re-equipped - it changes
// no server state and nothing is persisted.
public class YoYoFxChatCommand : GatewayChatCommand
{
    // The equipped-weapon slot: where a held prop hangs.
    private const int WeaponSlot = 7;

    // ★ THE WHOLE MODEL-BOUND WEAPON-EFFECT CATALOGUE - all 93 "skel" entries from the client's
    // ActorCompositeEffectDefinitions.xml, generated from it rather than typed. "skel" means the effect
    // binds to the whole model, which is the family real items use (756 items carry glints_white alone);
    // the other ~170 WFX entries are authored for one specific weapon and are unlikely to sit right on a
    // prop, though `!yoyofx <id>` will still send any id you name.
    private static readonly (int Id, string Name)[] Catalogue =
    [
        (15336, "archer-epic_skel_loop"),
        (16511, "balloons-confetti_blue-orange_skel_balloon-weapon-ultra-launch"),
        (16505, "balloons_blue-orange_skel_birthday-shard_loop"),
        (15319, "beam-chain-spiral_fire_orange_skel_ninja-sword_loop"),
        (15320, "beam-chain-spiral_skel_ninja-sword_loop"),
        (15111, "beam-spiral-sparkles_rainbow_skel_training-sword_rare"),
        (15275, "blobs_orange_skel_med_loop_fxweapon"),
        (15276, "blobs_red_skel_lg_loop_fxweapon"),
        (5721, "bubbles_green_up_skel_loop"),
        (5722, "bubbles_white_up_skel_loop"),
        (15354, "cards_falling_skel_med_loop"),
        (15195, "coins_gold_skel_loop_rare"),
        (16430, "coins_gold_skel_sm_loop"),
        (5723, "confetti_multi_falling_skel_loop"),
        (5725, "confetti_multi_falling_skel_sm_loop"),
        (5726, "drips_blue_falling_skel_loop"),
        (5727, "drips_green_falling_skel_loop"),
        (5728, "electric_blue_skel_loop"),
        (5815, "electric_green_skel_loop"),
        (5729, "electric_yellow_skel_loop"),
        (15114, "elements_multi_skel_med_loop_rare"),
        (15472, "embers_orange_skel_med_loop"),
        (15061, "fire_ice_skel_med_loop"),
        (16551, "fireflies_yellow_skel_flair-shard-firefly_loop"),
        (15099, "fireworks_multi_skel_loop_rare"),
        (15290, "flowers_multi_skel_fx-sword_loop"),
        (5730, "glints_white_skel_loop"),
        (5760, "glow_blue_skel_loop"),
        (5761, "glow_green_skel_loop"),
        (5733, "glow_orange_skel_loop"),
        (5762, "glow_pink_skel_loop"),
        (5763, "glow_purple_skel_loop"),
        (5764, "glow_red_skel_loop"),
        (5765, "glow_yellow_skel_loop"),
        (15904, "halloween_glow_orange-black_skel_med_loop"),
        (15903, "halloween_spiders_skel_med_loop"),
        (15101, "ice_white_falling_skel_loop_rare"),
        (15027, "ice_white_orbiting_skel_loop_z"),
        (15449, "ice_white_static_skel_loop_fxweapon"),
        (15288, "jewels_rainbow_skel_fx-sword_loop"),
        (15112, "jewels_rainbow_skel_loop_rare"),
        (15670, "lava-rocks_orange_skel_lg_loop"),
        (15669, "lava-rocks_orange_skel_med_loop"),
        (15265, "lava_orange_skel_lg_loop"),
        (15266, "lava_orange_skel_med_loop"),
        (5732, "leaves_multi_falling_skel_loop"),
        (5731, "leaves_multi_falling_skel_sm_loop"),
        (15083, "lightning_blue_skel_loop_rare"),
        (16892, "lightning_blue_skel_loop_rare_stormbreaker"),
        (16404, "lights_multi_skel_holiday_loop"),
        (16556, "macaroni_yellow_skel_macaroni-flair-shard_loop"),
        (15006, "magic-glow_blue_skel_loop_rare"),
        (15007, "magic-glow_green_skel_loop_rare"),
        (15060, "magic-glow_multi_skel_loop_rare"),
        (15008, "magic-glow_purple_skel_loop_rare"),
        (15009, "magic-glow_red_skel_loop_rare"),
        (15059, "magic-glow_white_skel_loop_rare"),
        (15010, "magic-sparkles_blue_skel_loop"),
        (15085, "magic-sparkles_blue_skel_loop_rare"),
        (15011, "magic-sparkles_green_skel_loop"),
        (15086, "magic-sparkles_green_skel_loop_rare"),
        (15012, "magic-sparkles_multi_skel_loop"),
        (15087, "magic-sparkles_multi_skel_loop_rare"),
        (15013, "magic-sparkles_orange_skel_loop"),
        (15088, "magic-sparkles_orange_skel_loop_rare"),
        (15014, "magic-sparkles_purple_skel_loop"),
        (15089, "magic-sparkles_purple_skel_loop_rare"),
        (15015, "magic-sparkles_white_skel_loop"),
        (15090, "magic-sparkles_white_skel_loop_rare"),
        (15561, "medic-epic_skel_loop"),
        (15594, "musical-notes_beam_blue_skel_loop_wand"),
        (15113, "musical-notes_rainbow_skel_loop_rare"),
        (16406, "ornaments_multi_skel_holiday_loop"),
        (15310, "prism_multi_skel_med_loop_fxweapon"),
        (15311, "rays_dark_skel_med_loop_fxweapon"),
        (15312, "rays_light_skel_med_loop_fxweapon"),
        (15024, "rocks_brown_orbiting_skel_loop_z"),
        (15025, "rocks_grey_orbiting_skel_loop_z"),
        (15026, "rocks_red_orbiting_skel_loop_z"),
        (15183, "shapes_multi_skel_loop_rare"),
        (5890, "shards_black_skel_loop_rare"),
        (5840, "shards_green_skel_loop_rare"),
        (5865, "shards_purple_skel_loop_rare"),
        (5915, "shards_red_skel_loop_rare"),
        (5940, "shards_white_skel_loop_rare"),
        (16590, "sparkles_gold_skeleton_tinkerbell-shard_loop"),
        (15284, "sparkles_pink_skel_med_loop_fxweapon"),
        (15285, "spikes_green_skel_lg_loop_fxweapon"),
        (15286, "spikes_green_skel_med_loop_fxweapon"),
        (15110, "star-ring_rainbow_skel_loop"),
        (5718, "stars_yellow_falling_skel_sm_loop"),
        (16504, "streamer-confetti_blue-orange_falling_skel_loop"),
        (15309, "wizard-epic_skel_loop"),
    ];

    public YoYoFxChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "yoyofx";
    public override string Usage => "<id|next|prev|list [filter]|windup [ms]|reach [units]|sweep [on/off]|refresh [on/off]|cast [on/off]|anim <id>|swap|trace|wield [on/off]|clear>";
    public override string Description => "Preview a composite effect on the equipped weapon/prop.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        if (args.Length == 0)
        {
            CommandSupport.SendSystem(conn, $"Usage: !yoyofx {Usage}. Equip the prop first.");
            return true;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
            {
                // `!yoyofx list blue` / `list glow` / `list orbit` - the catalogue is 93 entries, far too
                // many for one chat message, so an unfiltered list shows the look names only.
                var filter = args.Length > 1 ? args[1].ToLowerInvariant() : null;

                var matches = Catalogue
                    .Where(c => filter is null || c.Name.Contains(filter))
                    .ToArray();

                if (matches.Length == 0)
                {
                    CommandSupport.SendSystem(conn, $"No effect names contain \"{filter}\".");
                    return true;
                }

                CommandSupport.SendSystem(conn,
                    $"{matches.Length}/{Catalogue.Length} effects" + (filter is null ? "" : $" matching \"{filter}\"") + ":\n" +
                    string.Join("\n", matches.Select(c => $"  {c.Id} WFX_{c.Name}")));
                return true;
            }

            case "clear":
                Send(invoker, 0);
                CommandSupport.SendSystem(conn, "Cleared the prop's effect override.");
                return true;

            // How long the Light Strand Whip's crack plays before the transform lands. The clip's true
            // length isn't readable from the assets (Granny sections are Oodle-compressed), so the default
            // is inferred from the .adr's last event trigger at 2.063s - dial it here against the real thing.
            case "windup":
                if (args.Length > 1)
                {
                    if (!int.TryParse(args[1], out var ms) || ms < 0 || ms > 10000)
                    {
                        CommandSupport.SendSystem(conn, "Wind-up must be 0-10000 ms.");
                        return true;
                    }

                    LightStrandWhip.WindupMs = ms;
                }

                CommandSupport.SendSystem(conn, $"Whip wind-up before transform: {LightStrandWhip.WindupMs} ms.");
                return true;

            // The cooldown RADIAL (AbilityPacketLaunchAndLand). Off by default because it also makes the
            // client re-present the ability, which is the suspected cause of a prop's animation/effect
            // replaying on its own. Turn it on to compare.
            case "sweep":
                if (args.Length > 1)
                    PropAnimation.SendSweep = args[1].ToLowerInvariant() is "on" or "1" or "true";

                CommandSupport.SendSystem(conn,
                    $"Cooldown sweep (LaunchAndLand): {(PropAnimation.SendSweep ? "ON" : "off")}.");
                return true;

            // ★ PLAY ONE CLIP BY ID, so the two fidgets can be told apart by eye. The prop's clips are:
            //   yo-yo  43310025 (fidget_01) / 43320025 (fidget_02)
            //   whip   43310028 (fidget_01) / 43320028 (fidget_02)
            // Nothing in the client names them, which is why the slot->clip pairing is currently a guess.
            case "anim":
                if (args.Length < 2 || !int.TryParse(args[1], out var animId) || animId <= 0)
                {
                    CommandSupport.SendSystem(conn,
                        "Usage: !yoyofx anim <id>. Yo-yo 43310025/43320025, whip 43310028/43320028.");
                    return true;
                }

                PropAnimation.PlayOneShot(invoker, animId);
                CommandSupport.SendSystem(conn, $"Played animation {animId}.");
                return true;

            // ★ Whether a prop reports its real wield type. OFF (the default) keeps the client in the
            // ordinary locomotion branch so emotes/dances stop being interrupted; ON restores the prop's
            // own walk/run styling at the cost of that. Re-equip the prop to see the change.
            case "wield":
                if (args.Length > 1)
                    PropAnimation.SuppressPropWieldType = args[1].ToLowerInvariant() is not ("on" or "1" or "true");
                else
                    PropAnimation.SuppressPropWieldType = !PropAnimation.SuppressPropWieldType;

                CommandSupport.SendSystem(conn, PropAnimation.SuppressPropWieldType
                    ? "Prop wield type: SUPPRESSED (emotes work, no prop locomotion). Re-equip to apply."
                    : "Prop wield type: REAL (prop locomotion, emotes interrupted). Re-equip to apply.");
                return true;

            // ★ Echoes every prop press the server receives, rejected ones included. A random play WITH a
            // line means the CLIENT sent it (auto-repeat); with NO line, nothing reached the server and the
            // replay is client-side. See PropAnimation.Trace.
            case "trace":
                if (args.Length > 1)
                    PropAnimation.Trace = args[1].ToLowerInvariant() is "on" or "1" or "true";
                else
                    PropAnimation.Trace = !PropAnimation.Trace;

                CommandSupport.SendSystem(conn,
                    $"Prop press trace: {(PropAnimation.Trace ? "ON" : "off")}.");
                return true;

            // Flips which fidget each slot fires, for both props - see PropAnimation.SwapSlotAnimations.
            case "swap":
                if (args.Length > 1)
                    PropAnimation.SwapSlotAnimations = args[1].ToLowerInvariant() is "on" or "1" or "true";
                else
                    PropAnimation.SwapSlotAnimations = !PropAnimation.SwapSlotAnimations;

                CommandSupport.SendSystem(conn,
                    $"Slot->clip mapping swapped: {(PropAnimation.SwapSlotAnimations ? "ON (1=fidget_02, 2=fidget_01)" : "off (1=fidget_01, 2=fidget_02)")}.");
                return true;

            // StartCasting on use - the cast EVENT that starts the slot's own cooldown sweep, and the
            // ActionTime that locks the slot for its duration. See PropAnimation.SendCast.
            case "cast":
                if (args.Length > 1)
                    PropAnimation.SendCastLock = args[1].ToLowerInvariant() is "on" or "1" or "true";

                CommandSupport.SendSystem(conn,
                    $"Cast lock (StartCasting + ActionTime): {(PropAnimation.SendCastLock ? "ON" : "off")}.");
                return true;

            // The per-use toolbar re-send. Bisect switch for the self-replaying animation - see
            // PropAnimation.RefreshBarOnUse.
            case "refresh":
                if (args.Length > 1)
                    PropAnimation.RefreshBarOnUse = args[1].ToLowerInvariant() is "on" or "1" or "true";

                CommandSupport.SendSystem(conn,
                    $"Toolbar re-send on each use: {(PropAnimation.RefreshBarOnUse ? "ON" : "off")}.");
                return true;

            // How close a victim has to be for the whip's ability 1. For scale: melee reach is 7, a
            // collection node is clickable from 12, an archer's bow reaches 30.
            case "reach":
                if (args.Length > 1)
                {
                    if (!float.TryParse(args[1], out var units) || units <= 0 || units > 50)
                    {
                        CommandSupport.SendSystem(conn, "Reach must be between 0 and 50 units.");
                        return true;
                    }

                    LightStrandWhip.ReachUnits = units;
                }

                CommandSupport.SendSystem(conn, $"Whip reach: {LightStrandWhip.ReachUnits:0.#} units (melee is 7).");
                return true;

            case "next":
            case "prev":
                _index = args[0].ToLowerInvariant() == "next"
                    ? (_index + 1) % Catalogue.Length
                    : (_index - 1 + Catalogue.Length) % Catalogue.Length;

                var candidate = Catalogue[_index];
                Send(invoker, candidate.Id);
                CommandSupport.SendSystem(conn,
                    $"[{_index + 1}/{Catalogue.Length}] {candidate.Id} WFX_{candidate.Name}");
                return true;

            default:
                if (!int.TryParse(args[0], out var effectId) || effectId < 0)
                {
                    CommandSupport.SendSystem(conn, "Effect id must be a non-negative number.");
                    return true;
                }

                Send(invoker, effectId);
                CommandSupport.SendSystem(conn, $"Sent composite effect {effectId} to the weapon slot.");
                return true;
        }
    }

    private static int _index = -1;

    // Everyone who can see the player gets it, so the look can be judged from another character too.
    private static void Send(Player player, int compositeEffectId) =>
        player.SendTunneledToVisible(new PlayerUpdatePacketSlotCompositeEffectOverride
        {
            Guid = player.Guid,
            Slot = WeaponSlot,
            CompositeEffect = compositeEffectId,
        }, sendToSelf: true);
}
