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

public class DungeonChatCommand : GatewayChatCommand
{
    public DungeonChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "dungeon";
    public override string Usage => "[activityId]";
    public override string Description => "Enters a combat dungeon, or lists them.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        var catalog = Sanctuary.Game.Dungeons.DungeonCatalog.ByActivity;
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id) || !catalog.ContainsKey(id))
        {
            CommandSupport.SendSystem(conn, "Usage: !dungeon <id>. Available:");
            foreach (var d in catalog.Values)
                CommandSupport.SendSystem(conn, $"  {d.ActivityId} - {d.Comment}");
            return true;
        }
        EncounterParticipantRequestEntranceHandler.EnterEncounterArena(conn, id);
        CommandSupport.SendSystem(conn, $"Entering dungeon {id} ({catalog[id].Comment})...");
        return true;
    }
}
