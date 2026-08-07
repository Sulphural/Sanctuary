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

public class GiftChatCommand : GatewayChatCommand
{
    public GiftChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "gift";
    public override string Usage => "<player> <itemId> [quantity]";
    public override string Description => "Gifts items to a player.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

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
            CommandSupport.SendSystem(conn, "Usage: /gift <PlayerName> <ItemId> [quantity]");
            return true;
        }

        string pattern = parts[1];

        if (!int.TryParse(parts[2], out var itemId))
        {
            CommandSupport.SendSystem(conn, "ItemId must be a number.");
            return true;
        }

        int quantity = 1;
        if (parts.Length >= 4 && !int.TryParse(parts[3], out quantity))
        {
            CommandSupport.SendSystem(conn, "Quantity must be a number.");
            return true;
        }

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

        // Check if item exists
        if (!CommandSupport.ResourceManager.ClientItemDefinitions.TryGetValue(itemId, out var itemDef))
        {
            CommandSupport.SendSystem(conn, $"Item {itemId} not found in item definitions.");
            return true;
        }

        CommandSupport.Logger.LogInformation("Referee {Referee} gifted {Quantity}x Item {ItemId} to {Player}",
            conn.Player.Name.FullName, quantity, itemId, target.Name.FullName);

        // TODO: Actually add the item to player's inventory
        // This requires inventory system implementation

        CommandSupport.SendMessageToPlayer(target, $"[GIFT] A Referee has gifted you {quantity}x {itemDef.NameId}!");
        CommandSupport.SendSystem(conn, $"Gifted {quantity}x Item {itemId} to {target.Name.FullName}");
        CommandSupport.SendSystem(conn, "Note: Inventory system not yet implemented - item not actually added.");

        return true;
    }
}
