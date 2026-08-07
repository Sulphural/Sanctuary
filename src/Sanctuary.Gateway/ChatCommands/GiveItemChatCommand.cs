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

public class GiveItemChatCommand : GatewayChatCommand
{
    public GiveItemChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "giveitem";
    public override string Usage => "<itemId> [quantity]";
    public override string Description => "Gives yourself an item.";
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

        if (parts.Length < 2 || !int.TryParse(parts[1], out var itemId))
        {
            CommandSupport.SendSystem(conn, "Usage: /giveitem <itemId> [count]");
            return true;
        }

        int count = 1;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out count) || count < 1))
        {
            CommandSupport.SendSystem(conn, "Count must be a positive number.");
            return true;
        }

        if (!CommandSupport.ResourceManager.ClientItemDefinitions.TryGetValue(itemId, out var def))
        {
            CommandSupport.SendSystem(conn, $"Item {itemId} not found.");
            return true;
        }

        var total = CommandSupport.GrantItem(conn, def, count);
        CommandSupport.SendSystem(conn, total < 0
            ? "Failed to save item to database."
            : $"Gave {count}x item {itemId} (NameId={def.NameId}, now have {total}).");
        return true;
    }
}
