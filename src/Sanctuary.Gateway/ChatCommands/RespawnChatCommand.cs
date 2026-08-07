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

public class RespawnChatCommand : GatewayChatCommand
{
    public RespawnChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "respawn";
    public override string Usage => "";
    public override string Description => "Revives you after a knockout.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (!conn.Player.IsDead)
        {
            CommandSupport.SendSystem(conn, "You are not dead!");
            return true;
        }

        // Context-aware: overworld revives in place, dungeons revive at the dungeon spawn (see the zone
        // overrides of OnPlayerRespawn).
        conn.Player.Zone.OnPlayerRespawn(conn.Player);
        CommandSupport.SendSystem(conn, "You have been revived!");
        return true;
    }
}
