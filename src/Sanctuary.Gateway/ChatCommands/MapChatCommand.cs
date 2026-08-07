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

public class MapChatCommand : GatewayChatCommand
{
    public MapChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "map";
    public override string Usage => "[world]";
    public override string Description => "Travels to another world.";
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
            CommandSupport.SendSystem(conn, "Usage: /map <worldName> [x y z]   (world names: see Desktop\\loadable_worlds.txt; relog to go home)");
            return true;
        }

        string worldName = parts[1];

        float x = 0f, y = 60f, z = 0f;
        if (parts.Length >= 5
            && float.TryParse(parts[2], out var px)
            && float.TryParse(parts[3], out var py)
            && float.TryParse(parts[4], out var pz))
        {
            x = px; y = py; z = pz;
        }

        var spawn = new System.Numerics.Vector4(x, y, z, 1f);
        var zone = CommandSupport.ZoneManager.GetOrCreateDebugWorld(worldName, spawn);
        if (conn.Player.EncounterReturnPosition is null)
            conn.Player.EncounterReturnPosition = conn.Player.Position;
        conn.Player.TeleportToZone(zone, spawn, zone.SpawnRotation, sky: null, geometryId: 0);
        CommandSupport.SendSystem(conn, $"/map: loading '{worldName}' at ({x}, {y}, {z}). Relog to return home.");
        return true;
    }
}
