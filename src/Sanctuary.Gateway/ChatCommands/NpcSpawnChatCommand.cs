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

public class NpcSpawnChatCommand : GatewayChatCommand
{
    public NpcSpawnChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "npc";
    public override string Usage => "spawn <nameId> [modelId] [textureAlias]";
    public override string Description => "Spawns an NPC.";
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

        if (parts.Length < 2)
        {
            CommandSupport.SendSystem(conn, "Usage: /npc spawn <NameId> <ModelId> [TextureAlias]");
            return true;
        }

        var sub = parts[1].ToLowerInvariant();
        return sub switch
        {
            "spawn" => HandleNpcSpawn(conn, parts),
            _ => CommandSupport.UnknownSubCommand(conn, "npc", sub)
        };
    }

    private static bool HandleNpcSpawn(GatewayConnection conn, string[] parts)
    {
        if (parts.Length < 4)
        {
            CommandSupport.SendSystem(conn, "Usage: /npc spawn <NameId> <ModelId> [TextureAlias]");
            return true;
        }

        if (!int.TryParse(parts[2], out var nameId) ||
            !int.TryParse(parts[3], out var modelId))
        {
            CommandSupport.SendSystem(conn, "Usage: /npc spawn <NameId> <ModelId> [TextureAlias]");
            return true;
        }

        string? texture = parts.Length >= 5 ? parts[4] : null;

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            CommandSupport.SendSystem(conn, "You are not in a zone.");
            return true;
        }

        if (!zone.TryCreateNpc(out var npc) || npc is null)
        {
            CommandSupport.SendSystem(conn, "Failed to create NPC.");
            return true;
        }

        npc.NameId = nameId;
        npc.ModelId = modelId;
        npc.TextureAlias = texture;
        npc.Scale = 1f;
        npc.Visible = true;

        npc.UpdatePosition(conn.Player.Position, conn.Player.Rotation);

        var tile = zone.GetTileFromPosition(conn.Player.Position);
        tile.Entities.TryAdd(npc.Guid, npc);

        CommandSupport.SendSystem(conn, $"NPC spawned (Guid={npc.Guid}, NameId={nameId}, ModelId={modelId}).");
        return true;
    }
}
