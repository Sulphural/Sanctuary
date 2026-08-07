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

public class WarnChatCommand : GatewayChatCommand
{
    public WarnChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "warn";
    public override string Usage => "<player> <message>";
    public override string Description => "Sends a player a warning.";
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

        if (parts.Length < 3)
        {
            CommandSupport.SendSystem(conn, "Usage: /warn <PlayerName> <message>");
            return true;
        }

        string pattern = parts[1];
        string message = string.Join(' ', parts, 2, parts.Length - 2);

        if (!CommandSupport.TryResolvePlayerNamePattern(pattern, out var resolvedName, out var error))
        {
            CommandSupport.SendSystem(conn, error);
            return true;
        }

        if (!CommandSupport.ZoneManager.TryGetPlayer(resolvedName, out var target))
        {
            CommandSupport.SendSystem(conn, $"Player '{resolvedName}' not found.");
            return true;
        }

        CommandSupport.Logger.LogInformation("Player {Player} warned by Referee {Referee}. Message: {Message}",
            target.Name.FullName, conn.Player.Name.FullName, message);

        CommandSupport.SendMessageToPlayer(target, $"[REFEREE WARNING] {message}");
        CommandSupport.SendSystem(conn, $"Warning sent to {target.Name.FullName}");
        return true;
    }
}
