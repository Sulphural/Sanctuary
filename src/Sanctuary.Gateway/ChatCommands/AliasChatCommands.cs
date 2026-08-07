using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;

namespace Sanctuary.Gateway.ChatCommands;

// The manager keys commands by a single word, so the aliases people already type get their own thin
// classes that forward to the real command.
public class CoordsChatCommand : PosChatCommand
{
    public CoordsChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "coords";
    public override string Description => "Alias for !pos.";
}

public class LocChatCommand : PosChatCommand
{
    public LocChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "loc";
    public override string Description => "Alias for !pos.";
}

public class NcChatCommand : NoclipChatCommand
{
    public NcChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "nc";
    public override string Description => "Alias for !noclip.";
}

public class TestWeaponsChatCommand : JobWeaponsChatCommand
{
    public TestWeaponsChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "testweapons";
    public override string Description => "Alias for !jobweapons.";
}
