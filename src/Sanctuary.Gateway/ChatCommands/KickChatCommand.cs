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

public class KickChatCommand : GatewayChatCommand
{
    public KickChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "kick";
    public override string Usage => "<player> [reason]";
    public override string Description => "Kicks a player from the server.";
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
            CommandSupport.SendSystem(conn, "Usage: /kick <PlayerName> [reason]");
            return true;
        }

        string pattern = parts[1];
        string reason = parts.Length > 2 ? string.Join(' ', parts, 2, parts.Length - 2) : "Kicked by an Enforcer";

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

        // Don't allow kicking other admins
        if (CommandSupport.IsPlayerAdmin(target))
        {
            CommandSupport.SendSystem(conn, "You cannot kick other admins/Referees.");
            return true;
        }

        CommandSupport.Logger.LogWarning("Player {Player} kicked by Referee {Referee}. Reason: {Reason}",
            target.Name.FullName, conn.Player.Name.FullName, reason);

        CommandSupport.SendMessageToPlayer(target, $"You have been kicked from the server. Reason: {reason}");

        // Give them a moment to see the message, then disconnect
        System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
        {
            target.Disconnect();
        });

        CommandSupport.SendSystem(conn, $"Kicked {target.Name.FullName}. Reason: {reason}");
        return true;
    }
}
