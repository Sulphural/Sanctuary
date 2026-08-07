using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;

namespace Sanctuary.Gateway.ChatCommands;

// The no-UI fallback: rolls the daily reward straight into the player's coins with no wheel on screen.
// Kept from before the real widget worked, as a way to test the daily gate without the client.
public class SpinWheelChatCommand : GatewayChatCommand
{
    public SpinWheelChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "spinwheel";
    public override string Usage => "";
    public override string Description => "Rolls the Spin For The Win daily reward directly, with no wheel UI.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        if (invoker.Zone is not Sanctuary.Game.Zones.StartingZone startingZone)
        {
            Reply(invoker, "You must be in the starting zone to use this.");
            return true;
        }

        startingZone.SpinDailyWheel(invoker);

        return true;
    }
}
