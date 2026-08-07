using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Game.ChatCommands;
using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Gateway.Handlers;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.ChatCommands;

public class LuaChatCommand : GatewayChatCommand
{
    public LuaChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "lua";
    public override string Usage => "<code>";
    public override string Description => "Asks the client to run a Lua snippet.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();
        var message = string.Join(' ', parts);

        if (string.IsNullOrWhiteSpace(message))
            return true;

        int sp = message.IndexOf(' ');
        if (sp < 0 || sp + 1 >= message.Length)
        {
            CommandSupport.SendSystem(conn, "Usage: /lua <script>");
            return true;
        }

        string script = message.Substring(sp + 1).Trim();

        // There are TWO candidate "run this Lua" packets and it's not settled which one this client build
        // actually honours, so fire both:
        //   * ExecuteScriptPacket        (BaseUi op47/sub7)  string + List<int>
        //   * AbilityPacketExecuteClientLua (op36/sub17)     string + 3 floats  (the layout EDITz specified)
        // If a script has a visible effect, whichever landed is the working one.
        conn.SendTunneled(new ExecuteScriptPacket { Script = script });
        conn.SendTunneled(new AbilityPacketExecuteClientLua { Script = script });

        CommandSupport.SendSystem(conn, $"[lua] sent (both packets): {script}");
        CommandSupport.Logger.LogInformation("/lua from {Player}: {Script}", conn.Player.Name.FullName, script);
        return true;
    }
}
