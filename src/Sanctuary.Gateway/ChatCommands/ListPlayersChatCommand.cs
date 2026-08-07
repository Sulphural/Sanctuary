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

public class ListPlayersChatCommand : GatewayChatCommand
{
    public ListPlayersChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "listplayers";
    public override string Usage => "";
    public override string Description => "Lists the players who are online.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Mod;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (!CommandSupport.RequireAdmin(conn))
            return true;

        var list = new List<string>();

        // Get all players from starting zone
        foreach (var p in CommandSupport.ZoneManager.StartingZone.Players)
        {
            // Show GUID + Name so you can distinguish players
            list.Add($"{p.Guid} — {p.Name.FullName}");
        }

        if (list.Count == 0)
        {
            CommandSupport.SendSystem(conn, "No players online.");
            return true;
        }

        // Build a nice readable list
        string msg = "Online players:\n" + string.Join("\n", list);

        CommandSupport.SendSystem(conn, msg);
        return true;
    }
}
