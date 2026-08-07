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

public class SpawnEnemyChatCommand : GatewayChatCommand
{
    public SpawnEnemyChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "spawnenemy";
    public override string Usage => "[modelId] [level] [name]";
    public override string Description => "Spawns a combat NPC.";
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

        // /spawnenemy <ModelId> [Level] [Name]
        if (parts.Length < 2 || !int.TryParse(parts[1], out var modelId))
        {
            CommandSupport.SendSystem(conn, "Usage: /spawnenemy <ModelId> [Level] [Name]");
            return true;
        }

        var level = parts.Length >= 3 && int.TryParse(parts[2], out var lvl) ? lvl : 1;
        var name = parts.Length >= 4 ? string.Join(" ", parts[3..]) : "Enemy";

        var zone = conn.Player.Zone;

        if (!zone.TryCreateCombatNpc(out var combatNpc))
        {
            CommandSupport.SendSystem(conn, "Failed to create combat NPC.");
            return true;
        }

        combatNpc.ModelId = modelId;
        combatNpc.Name = name;
        combatNpc.Scale = 1.0f;
        combatNpc.Disposition = 0; // Hostile
        combatNpc.IsInteractable = true;
        combatNpc.InteractRange = 100;
        combatNpc.Speed = 6.0f;

        // Set combat stats based on level
        combatNpc.InitializeFromLevel(level);

        // Position slightly in front of the player
        var forward = new System.Numerics.Vector3(
            2.0f * (conn.Player.Rotation.X * conn.Player.Rotation.Z + conn.Player.Rotation.W * conn.Player.Rotation.Y),
            0f,
            1.0f - 2.0f * (conn.Player.Rotation.X * conn.Player.Rotation.X + conn.Player.Rotation.Y * conn.Player.Rotation.Y)
        );

        var spawnPos = new System.Numerics.Vector4(
            conn.Player.Position.X + forward.X * 8f,
            conn.Player.Position.Y,
            conn.Player.Position.Z + forward.Z * 8f,
            1f
        );

        combatNpc.SpawnPosition = spawnPos;
        combatNpc.SpawnRotation = conn.Player.Rotation;
        combatNpc.UpdatePosition(spawnPos, conn.Player.Rotation);
        combatNpc.LastSentPosition = spawnPos;
        combatNpc.Visible = true;
        combatNpc.UpdateZoneTile();

        // Explicitly send the AddNpc packet to the spawning player
        // so they see it immediately (tile system also handles visibility
        // for other nearby players)
        var addPacket = combatNpc.GetAddNpcPacket();
        conn.Player.SendTunneled(addPacket);
        conn.Player.VisibleNpcs.TryAdd(combatNpc.Guid, combatNpc);

        CommandSupport.SendSystem(conn, $"Spawned combat NPC '{name}' (Level {level}, HP: {combatNpc.MaxHitpoints}, DMG: {combatNpc.AttackDamage}, XP: {combatNpc.XpReward})");
        return true;
    }
}
