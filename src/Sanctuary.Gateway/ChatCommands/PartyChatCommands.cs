using System.Linq;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Gateway.Handlers;
using Sanctuary.Packet;

namespace Sanctuary.Gateway.ChatCommands;

// Party/group commands. The accept path exists because the client's native accept packet format still
// isn't captured - see BaseGroupPacketHandler.
public class PartyAcceptChatCommand : GatewayChatCommand
{
    public PartyAcceptChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "paccept";
    public override string Usage => "";
    public override string Description => "Accepts a pending party invite.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        BaseGroupPacketHandler.AcceptInvite(invoker);
        return true;
    }
}

public class PartyLeaveChatCommand : GatewayChatCommand
{
    public PartyLeaveChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "pleave";
    public override string Usage => "";
    public override string Description => "Leaves your party.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        BaseGroupPacketHandler.LeaveParty(invoker);
        return true;
    }
}

// Sends a co-op dungeon (GAQ) encounter invitation - the popup that asks a group to accept an instance.
public class GroupInviteChatCommand : GatewayChatCommand
{
    public GroupInviteChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "ginvite";
    public override string Usage => "[encounterId] [instanceId] [a] [b] [guid|player]";
    public override string Description => "Sends an encounter invitation popup.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        int Arg(int index, int fallback) =>
            args.Length > index && int.TryParse(args[index], out var value) ? value : fallback;

        var packet = new EncounterInvitationPacket
        {
            Unknown = Arg(0, 0),    // EncounterId (header)
            Unknown2 = Arg(1, 1),   // InstanceId (header)
            Guid = invoker.Guid,    // inviter = self (names the popup)
            A = Arg(2, 0),
            B = Arg(3, 0),
        };

        // Optional last argument: a raw guid to override the guid field, OR a target player name to send
        // the popup to (so a leader can sweep A on a member's real cross-player popup). Default self.
        var target = invoker;

        if (args.Length > 4)
        {
            if (ulong.TryParse(args[4], out var guid))
            {
                packet.Guid = guid;
            }
            else if (!CommandSupport.ZoneManager.TryGetPlayer(args[4], out target))
            {
                Reply(invoker, $"Player '{args[4]}' not found.");
                return true;
            }
        }

        target.SendTunneled(packet);

        Reply(invoker, $"op41/102 enc={packet.Unknown} A={packet.A} B={packet.B} guid={packet.Guid} " +
                       $"-> {target.Name?.FullName}. Watch their screen.");
        return true;
    }
}
