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

public class LuaSpawnChatCommand : GatewayChatCommand
{
    public LuaSpawnChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "luaspawn";
    public override string Usage => "[npcId]";
    public override string Description => "Spawns an NPC through the zone-script spawn API.";
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

        var npcId = 1186;
        if (parts.Length >= 2 && !int.TryParse(parts[1], out npcId))
        {
            CommandSupport.SendSystem(conn, "Usage: /luaspawn [NpcId]");
            return true;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            CommandSupport.SendSystem(conn, "You are not in a zone.");
            return true;
        }

        var position = conn.Player.Position;
        var rotation = conn.Player.Rotation;
        var heading = MathF.Atan2(rotation.X, rotation.Z);

        if (!zone.TrySpawnNpc(npcId, null, position.X, position.Y, position.Z, heading))
        {
            CommandSupport.SendSystem(conn, $"TrySpawnNpc failed for NpcId {npcId} (no definition found for that id?).");
            return true;
        }

        CommandSupport.SendSystem(conn, $"Spawned NpcId {npcId} on top of you via TrySpawnNpc (the Lua spawn API).");
        return true;
    }
}
