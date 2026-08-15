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
    public override string Usage => "[boss]";
    public override string Description => "Starts the Snowmen Invaders battle at the Gifting Tree now.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        if (invoker.Zone is not StartingZone zone)
        {
            Reply(invoker, "[snowmen] Only runs in the overworld.");
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
