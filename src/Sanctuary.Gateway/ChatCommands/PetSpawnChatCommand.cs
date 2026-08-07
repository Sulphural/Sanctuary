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

public class PetSpawnChatCommand : GatewayChatCommand
{
    public PetSpawnChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "petspawn";
    public override string Usage => "[petId]";
    public override string Description => "Spawns one of your pets.";
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
            // List available pets
            if (conn.Player.Pets.Count == 0)
            {
                CommandSupport.SendSystem(conn, "You don't own any pets. Usage: /petspawn [DbPetId]");
                return true;
            }

            CommandSupport.SendSystem(conn, "Your pets: " + string.Join(", ", conn.Player.Pets.Select(p => $"DbId:{p.Id}")));
            return true;
        }

        if (!uint.TryParse(parts[1], out var dbPetId))
        {
            CommandSupport.SendSystem(conn, "Invalid pet ID.");
            return true;
        }

        // Find the pet in the player's collection by database ID (not Definition ID)
        var petInfo = conn.Player.Pets.FirstOrDefault(x => x.Id == (int)dbPetId);
        if (petInfo is null)
        {
            CommandSupport.SendSystem(conn, $"You don't own a pet with database ID {dbPetId}. Your pets: " + string.Join(", ", conn.Player.Pets.Select(p => $"DbId:{p.Id}")));
            return true;
        }

        // Check if a pet is already active
        if (conn.Player.Pet is not null)
        {
            CommandSupport.SendSystem(conn, "You already have a pet active. Use /petdespawn first.");
            return true;
        }

        // Reuse the real client-facing spawn path (PetSummonRecallPacketHandler, opcode 4/9 - the
        // only pet-lifecycle opcodes that actually exist in the retail wire protocol; the
        // PacketPetSpawn/PacketPetDismount/PetSpawnResponsePacket/PetDismountResponsePacket classes
        // this used to duplicate were sending made-up opcodes 33-36 that collide with real, unrelated
        // retail packets - PetPlayWithToy/PetMoodList/PetEquipByItemRecord/PetPacketOfferUpsell).
        PetSummonRecallPacketHandler.SpawnPet(conn, petInfo);

        if (conn.Player.Pet is null)
        {
            CommandSupport.SendSystem(conn, "Failed to spawn pet.");
            return true;
        }

        CommandSupport.SendSystem(conn, "Pet spawned!");
        return true;
    }
}
