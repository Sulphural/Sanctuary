using System;
using System.Numerics;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;

namespace Sanctuary.Gateway.ChatCommands;

// The Snow Days band on Bruce's stage - two robgoblin guitarists and a drummer.
//
// The two guitarists sit on measured !pos values and need no tuning. The DRUMMER'S spot was never
// measured (it was derived from the guitarists' midpoint), so this exists mainly to walk him into place:
// stand where the kit should be, run `/band drums here`, look, repeat - then paste the final numbers back
// into StartingZone.SnowDaysBand as a measured constant.
//
// ★ Bruce and the band NEVER share the stage - they each bring their own music, neither track can be
// stopped early, and together they just overlap into noise. Every command here honours that: staging one
// act always clears the other first.
//
//   /band                   put the BAND on now (clears Bruce if he is mid-set)
//   /band bruce             put BRUCE on now (clears the band)
//   /band stop              clear the stage
//   /band where             report the drummer's spot and who is currently playing
//   /band drums here        put the drummer where YOU are standing, facing the way you face
//   /band drums <x> <y> <z> [heading]   put him at explicit coordinates
public class BandChatCommand : GatewayChatCommand
{
    public BandChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "band";
    public override string Usage => "[drums here|<x> <y> <z> [heading]] [where]";
    public override string Description => "Restage the Snow Days band, or place its drummer.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        if (invoker.Zone is not StartingZone zone)
        {
            Reply(invoker, "[band] They play the stage in Snowhill - you have to be in the overworld.");
            return true;
        }

        var verb = args.Length >= 1 ? args[0].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "where":
                Reply(invoker, $"[band] Drummer at {StartingZone.BandDrummerPosition} " +
                               $"heading {StartingZone.BandDrummerHeadingDegrees:0}deg. " +
                               $"Right now: {zone.StageStatus}.");
                return true;

            // Bruce's set. Clears the band first - one act at a time.
            case "bruce":
                zone.StageBruceNow();
                Reply(invoker, zone.IsBrucePerforming
                    ? "[band] Bruce is on. The band is off the stage - they never play together."
                    : "[band] Couldn't bring Bruce on (npc budget?). Stage left as it was.");
                return true;

            case "stop":
                zone.ClearStage();
                Reply(invoker, "[band] Stage cleared.");
                return true;

            case "drums" when args.Length >= 2 && args[1].Equals("here", StringComparison.OrdinalIgnoreCase):
                // The client's "rotation" is a facing DIRECTION packed as (sin h, 0, cos h), so the heading
                // reads back as Atan2(x, z) - the same conversion !pos uses.
                var heading = MathF.Atan2(invoker.Rotation.X, invoker.Rotation.Z) * 180f / MathF.PI;
                StartingZone.BandDrummerPosition = invoker.Position;
                StartingZone.BandDrummerHeadingDegrees = heading;
                zone.StageSnowDaysBandNow();
                Reply(invoker, $"[band] Drummer moved to your spot: " +
                               $"new Vector4({invoker.Position.X:0.00}f, {invoker.Position.Y:0.00}f, {invoker.Position.Z:0.00}f, 1f) " +
                               $"heading {heading:0}deg. Paste that into StartingZone.SnowDaysBand when it looks right.");
                return true;

            case "drums" when args.Length >= 4
                              && float.TryParse(args[1], out var x)
                              && float.TryParse(args[2], out var y)
                              && float.TryParse(args[3], out var z):
                StartingZone.BandDrummerPosition = new Vector4(x, y, z, 1f);
                if (args.Length >= 5 && float.TryParse(args[4], out var explicitHeading))
                    StartingZone.BandDrummerHeadingDegrees = explicitHeading;
                zone.StageSnowDaysBandNow();
                Reply(invoker, $"[band] Drummer at ({x:0.00}, {y:0.00}, {z:0.00}) " +
                               $"heading {StartingZone.BandDrummerHeadingDegrees:0}deg.");
                return true;
        }

        // Bare `/band` - put the band on.
        zone.StageSnowDaysBandNow();
        Reply(invoker, zone.IsBandOnStage
            ? "[band] Band is on (Bruce cleared off - they never play together). `/band bruce` swaps back."
            : "[band] Couldn't stage the band (npc budget?). Stage left as it was.");
        return true;
    }
}
