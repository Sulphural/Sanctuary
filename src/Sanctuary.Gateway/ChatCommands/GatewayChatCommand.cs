using System.Linq;

using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Helpers;

namespace Sanctuary.Gateway.ChatCommands;

// Base for chat commands that need Gateway-layer state. IChatCommand hands Handle() a Player, but a lot
// of what these commands drive (packet handlers, minigame state, the connection itself) only exists in
// Sanctuary.Gateway, so they live here rather than in Sanctuary.Game and ChatCommandManager finds them by
// scanning every loaded Sanctuary assembly.
public abstract class GatewayChatCommand : IChatCommand
{
    private readonly GatewayServer _server;

    protected GatewayChatCommand(GatewayServer server)
    {
        _server = server;
    }

    public abstract string KeyWord { get; }
    public abstract string Usage { get; }
    public abstract string Description { get; }
    public abstract ChatCommandRole RequiredRole { get; }

    public abstract bool Handle(Player invoker, string[] args);

    // The invoker's connection, or null if they've gone (a command can be mid-flight when that happens).
    protected GatewayConnection? GetConnection(Player player) =>
        _server.Connections.FirstOrDefault(x => ReferenceEquals(x.Player, player));

    protected static void Reply(Player player, string message) =>
        ChatHelper.SendSystemMessage(player, message);

    // Same, but for text that carries its own markup (the client renders <font color> in these).
    protected static void ReplyFormatted(Player player, string message) =>
        ChatHelper.SendSystemMessage(player, message, formatted: true);
}
