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

public class ListHousesChatCommand : GatewayChatCommand
{
    public ListHousesChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "listhouses";
    public override string Usage => "";
    public override string Description => "Lists your houses.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(CommandSupport.DbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, HouseDefinitionId, CustomName, Created
                FROM Houses
                WHERE OwnerId = $charId
                ORDER BY Created DESC;
            ";
            cmd.Parameters.AddWithValue("$charId", characterId);

            using var reader = cmd.ExecuteReader();

            var houses = new List<string>();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var defId = reader.GetInt32(1);
                var customName = reader.IsDBNull(2) ? null : reader.GetString(2);
                var created = reader.GetString(3);

                var name = customName ?? $"House #{id}";
                houses.Add($"#{id}: {name} (Def: {defId}, Created: {created})");
            }

            if (houses.Count == 0)
            {
                CommandSupport.SendSystem(conn, "You don't have any houses. Use /createhouse to get one!");
            }
            else
            {
                CommandSupport.SendSystem(conn, "Your houses:\n" + string.Join("\n", houses));
            }

            return true;
        }
        catch (Exception ex)
        {
            CommandSupport.Logger.LogError(ex, "Failed to list houses for character {CharId}", characterId);
            CommandSupport.SendSystem(conn, "Error listing houses.");
            return true;
        }
    }
}
