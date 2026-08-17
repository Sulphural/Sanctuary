using System;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;

namespace Sanctuary.Gateway.ChatCommands;

// Dev tooling for Trina Turtledove's 12 Days of Presents panel (op207 ProgressiveQuest).
//
// ★ WHY A PROBE COMMAND EXISTS AT ALL. The panel's WIRE LAYOUT is exact - it was read straight out of the
// client's own deserializer at 0x00bde1f0 (see ProgressiveQuestClientDataPacket for the full trail) - but
// which of the definition's ten ints carries the title, the icon, the countdown and each button label is
// matched from the data source's column list, and that kind of inference has been wrong in this codebase
// twice before. The rule learned from the matchmaking queue columns was: don't reason about it, stamp
// every field with a recognisable number and read the answer off the screen.
//
//   /trina                 re-open the panel with the real 12 Days content
//   /trina probe           re-open it with every definition field stamped 7100+its index
//   /trina probe off       back to the real content
//   /trina secs <n>        set the countdown (the timer in the panel's corner)
//
// With `probe` on, whichever number shows as the title is that field, the one counting down is the
// countdown, and so on - then rename the fields in the packet and delete the guesswork.
public class TrinaChatCommand : GatewayChatCommand
{
    public TrinaChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "trina";
    public override string Usage => "[probe [off]] [secs <n>]";
    public override string Description => "Open Trina Turtledove's 12 Days of Presents panel, or probe its field layout.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var verb = args.Length >= 1 ? args[0].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "probe":
                StartingZone.ProbeFields = args.Length < 2 || !args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
                Reply(invoker, StartingZone.ProbeFields
                    ? "[trina] Probe ON - definition fields stamped 7100+index. Re-opening: whichever number " +
                      "appears as the title is that field, the one ticking down is the countdown, and the " +
                      "button labels give away the rest. Report what shows where."
                    : "[trina] Probe OFF - real 12 Days content.");
                break;

            case "secs" when args.Length >= 2 && int.TryParse(args[1], out var seconds):
                StartingZone.PresentsSecondsRemaining = Math.Max(0, seconds);
                Reply(invoker, $"[trina] Countdown = {StartingZone.PresentsSecondsRemaining}s.");
                break;

            // The reward popup's icon - the one value in the panel that is a stand-in rather than a
            // recovered id (no gift-box icon ships under a snowdays/gift_box name).
            // ★ An IMAGE SET id here, not a raw image id - the popup and the tiles use different tables.
            // Gift sets are 8195..8223 (box(day) = 8196 + day*2, 8221..8223 the uber presents).
            case "rewardicon" when args.Length >= 2 && int.TryParse(args[1], out var rewardIcon):
                StartingZone.PresentRewardIconSetId = rewardIcon;
                Reply(invoker, $"[trina] Reward popup icon SET = {StartingZone.PresentRewardIconSetId} " +
                               "(sets, not image ids - 8195..8223 are the gift ones). Open a present to see it.");
                break;

            // Forget which presents have been opened, so the panel can be walked through again.
            case "reset":
                if (invoker.Zone is StartingZone resetZone)
                    resetZone.ResetTwelveDaysClaims(invoker);
                Reply(invoker, "[trina] Opened-present record cleared.");
                break;

            // How many of the twelve days are unlocked - retail unlocks one per day from the 13th.
            case "days" when args.Length >= 2 && int.TryParse(args[1], out var days):
                StartingZone.PresentsUnlockedDays = Math.Clamp(days, 0, 12);
                Reply(invoker, $"[trina] {StartingZone.PresentsUnlockedDays}/12 days unlocked - " +
                               "earned presents get their icon, the Big Present bars fill 25% per day.");
                break;
        }

        if (invoker.Zone is not StartingZone zone)
        {
            Reply(invoker, "[trina] You have to be in the overworld - the panel is hers, and she lives in Snowhill.");
            return true;
        }

        zone.OpenTwelveDaysOfPresents(invoker);
        Reply(invoker, "[trina] Sent the 12 Days state + ShowWindow (op207/1 then 207/0). " +
                       "Nothing at all on screen means ShowWindow wants a body; an empty grid means the " +
                       "state packet is being rejected.");
        return true;
    }
}
