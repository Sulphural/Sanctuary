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

public class GoHouseChatCommand : GatewayChatCommand
{
    public GoHouseChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "gohouse";
    public override string Usage => "[houseId]";
    public override string Description => "Enters one of your houses.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (parts.Length < 2)
        {
            CommandSupport.SendSystem(conn, "Usage: /gohouse <HouseId>");
            return true;
        }

        if (!long.TryParse(parts[1], out var houseId))
        {
            CommandSupport.SendSystem(conn, "House ID must be a number.");
            return true;
        }

        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(CommandSupport.DbConnectionString);
            db.Open();

            // Verify the house exists and get its info
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT h.CharacterId, h.Definition
                FROM Houses h
                WHERE h.Id = $houseId
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("$houseId", houseId);

            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
            {
                CommandSupport.SendSystem(conn, $"House #{houseId} not found.");
                return true;
            }

            var ownerId = reader.GetInt64(0);
            var houseDefId = reader.GetInt32(1);

            // For now, only allow owners to enter (you can add permissions later)
            if (ownerId != characterId)
            {
                CommandSupport.SendSystem(conn, $"You don't have permission to enter house #{houseId}.");
                return true;
            }

            // Get the house definition from the resource manager
            if (!CommandSupport.ResourceManager.Houses.TryGetValue(houseDefId, out var houseDef))
            {
                CommandSupport.SendSystem(conn, $"House definition {houseDefId} not found. Using default.");
                // Fall back to default housing zone
                var defaultPacket = new PacketClientBeginZoning
                {
                    Name = "hsg_emptylot_seaside_beach_01",
                    Type = 2,
                    Position = new System.Numerics.Vector4(440.632f, -0.071f, 432.801f, 1.0f),
                    Rotation = new System.Numerics.Quaternion(-0.9999741f, 0.0f, -0.0072035603f, 0.0f),
                    Sky = "sky_seaside24.xml",
                    Unknown = 1,
                    Id = (int)houseId,
                    GeometryId = 214,
                    OverrideUpdateRadius = true
                };
                conn.SendTunneled(defaultPacket);
                CommandSupport.SendSystem(conn, $"Entering house #{houseId}...");
                return true;
            }

            // Get the zone definition for this house
            string zoneName = "hsg_emptylot_seaside_beach_01"; // Default fallback
            string sky = "sky_seaside24.xml"; // Default sky
            int geometryId = 214; // Default geometry
            var spawnPosition = houseDef.SpawnPosition;
            var spawnRotation = new System.Numerics.Quaternion(
                houseDef.SpawnRotation.X,
                houseDef.SpawnRotation.Y,
                houseDef.SpawnRotation.Z,
                houseDef.SpawnRotation.W
            );

            if (CommandSupport.ResourceManager.Zones.TryGetValue(houseDef.ZoneId, out var zoneDef))
            {
                zoneName = zoneDef.Name;
                // Use zone definition spawn position if available (more reliable)
                if (zoneDef is Sanctuary.Game.Resources.Definitions.Zones.StartingZoneDefinition startingZone)
                {
                    spawnPosition = new System.Numerics.Vector4(
                        startingZone.SpawnPosition.X,
                        startingZone.SpawnPosition.Y + 2f, // Add 2 units height to prevent falling
                        startingZone.SpawnPosition.Z,
                        0
                    );

                    spawnRotation = new System.Numerics.Quaternion(
                        startingZone.SpawnRotation.X,
                        startingZone.SpawnRotation.Y,
                        0,
                        0
                    );

                    CommandSupport.Logger.LogInformation("Using zone spawn position: ({X}, {Y}, {Z})",
                        spawnPosition.X, spawnPosition.Y, spawnPosition.Z);
                }

                CommandSupport.Logger.LogInformation("Using zone {ZoneName} (ID: {ZoneId}) for house def {HouseDefId}",
                    zoneName, houseDef.ZoneId, houseDefId);
            }
            else
            {
                // Add safety height to Houses.json position
                spawnPosition = new System.Numerics.Vector4(
                    houseDef.SpawnPosition.X,
                    houseDef.SpawnPosition.Y + 2f,
                    houseDef.SpawnPosition.Z,
                    houseDef.SpawnPosition.W
                );

                CommandSupport.Logger.LogWarning("Zone {ZoneId} not found for house def {HouseDefId}, using default zone",
                    houseDef.ZoneId, houseDefId);
            }

            // Zone the player to the house
            var packetClientBeginZoning = new PacketClientBeginZoning
            {
                Name = zoneName,
                Type = 2,
                Position = spawnPosition,
                Rotation = spawnRotation,
                Sky = sky,
                Unknown = 1,
                Id = (int)houseId, // Use house ID as zone ID
                GeometryId = geometryId,
                OverrideUpdateRadius = true
            };

            conn.SendTunneled(packetClientBeginZoning);

            CommandSupport.SendSystem(conn, $"Entering house #{houseId} (Type: {houseDef.NameId})...");
            CommandSupport.Logger.LogInformation("Player {Player} entering house {HouseId} (Def: {DefId}, Zone: {ZoneName})",
                conn.Player.Name.FullName, houseId, houseDefId, zoneName);

            return true;
        }
        catch (Exception ex)
        {
            CommandSupport.Logger.LogError(ex, "Failed to enter house {HouseId} for character {CharId}", houseId, characterId);
            CommandSupport.SendSystem(conn, "Error entering house.");
            return true;
        }
    }
}
