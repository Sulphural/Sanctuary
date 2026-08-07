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

public class DieChatCommand : GatewayChatCommand
{
    public DieChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "die";
    public override string Usage => "";
    public override string Description => "Knocks you out.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (conn.Player.IsDead)
        {
            CommandSupport.SendSystem(conn, "You are already knocked out. Use /respawn.");
            return true;
        }

        conn.Player.Knockout();
        CommandSupport.SendSystem(conn, "You collapsed. (Knockout triggered — /respawn to get back up.)");
        return true;
    }
}
