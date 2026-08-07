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

public class LuaReloadChatCommand : GatewayChatCommand
{
    public LuaReloadChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "luareload";
    public override string Usage => "";
    public override string Description => "Reloads and re-runs your zone's Lua script.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (!CommandSupport.RequireAdmin(conn))
            return true;

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            CommandSupport.SendSystem(conn, "You are not in a zone.");
            return true;
        }

        if (!zone.ReloadScript())
        {
            CommandSupport.SendSystem(conn, $"Reload failed for zone '{zone.Name}' - script missing or failed to load (check server logs). Previous script is still active.");
            return true;
        }

        CommandSupport.SendSystem(conn, $"Reloaded and re-ran the script for zone '{zone.Name}'.");
        return true;
    }
}
