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

public class CreateHouseChatCommand : GatewayChatCommand
{
    public CreateHouseChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "createhouse";
    public override string Usage => "[houseDefinitionId]";
    public override string Description => "Creates a house.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        // Default house definition ID (you can change this based on your house definitions)
        int houseDefId = 1;

        if (parts.Length >= 2 && int.TryParse(parts[1], out var customDefId))
        {
            houseDefId = customDefId;
        }

        // Validate the house definition exists
        if (!CommandSupport.ResourceManager.Houses.TryGetValue(houseDefId, out var houseDef))
        {
            var availableIds = string.Join(", ", CommandSupport.ResourceManager.Houses.Keys.OrderBy(k => k).Take(10));
            CommandSupport.SendSystem(conn, $"House definition {houseDefId} not found.");
            CommandSupport.SendSystem(conn, $"Available house types: {availableIds}...");
            return true;
        }

        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(CommandSupport.DbConnectionString);
            db.Open();

            // Create a new house for the player. Houses.Id is ValueGeneratedNever in the EF model, so the id
            // is allocated here rather than by SQLite's rowid - last_insert_rowid() would come back 0.
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Houses (Id, CharacterId, Definition, Name, IsLocked, IsMembersOnly, IsFloraAllowed,
                                   PetAutospawn, MaxFixtureCount, MaxLandmarkCount, FurnitureScore, IsPublished,
                                   Rating, Votes, Description, KeywordList, Created, LastVisited)
                VALUES ((SELECT COALESCE(MAX(Id), 0) + 1 FROM Houses), $characterId, $houseDefId, NULL,
                        0, 0, 1, 0, 2000, 0, 0, 0, 0.0, 0, '', '', datetime('now'), datetime('now'));

                SELECT MAX(Id) FROM Houses;
            ";
            cmd.Parameters.AddWithValue("$characterId", characterId);
            cmd.Parameters.AddWithValue("$houseDefId", houseDefId);

            var newHouseId = cmd.ExecuteScalar();

            CommandSupport.SendSystem(conn, $"Created house #{newHouseId} (Type: {houseDef.NameId}). Use /gohouse {newHouseId} to enter!");
            return true;
        }
        catch (Exception ex)
        {
            CommandSupport.Logger.LogError(ex, "Failed to create house for character {CharId}", characterId);
            CommandSupport.SendSystem(conn, "Error creating house.");
            return true;
        }
    }
}
