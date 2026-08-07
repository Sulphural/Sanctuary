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

public class BringChatCommand : GatewayChatCommand
{
    public BringChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "bring";
    public override string Usage => "<player>";
    public override string Description => "Teleports a player to you.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Mod;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (!CommandSupport.RequireEnforcer(conn))
            return true;

        if (parts.Length < 2)
        {
            CommandSupport.SendSystem(conn, "Usage: /bring <PlayerName>");
            return true;
        }

        string pattern = string.Join(' ', parts, 1, parts.Length - 1);

        if (!CommandSupport.TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
        {
            CommandSupport.SendSystem(conn, error);
            return true;
        }

        if (!CommandSupport.ZoneManager.TryGetPlayer(resolvedName, out var target))
        {
            CommandSupport.SendSystem(conn, $"Player '{resolvedName}' not found (after resolving pattern).");
            return true;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            CommandSupport.SendSystem(conn, "You are not in a zone.");
            return true;
        }

        target.TeleportToZone(zone, conn.Player.Position, conn.Player.Rotation);

        CommandSupport.SendSystem(conn, $"Brought {target.Name.FullName} to your position.");
        return true;
    }
}
