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

public class GotoChatCommand : GatewayChatCommand
{
    public GotoChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "goto";
    public override string Usage => "<x> <y> <z>";
    public override string Description => "Teleports you to coordinates.";
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

        if (parts.Length < 4)
        {
            CommandSupport.SendSystem(conn, "Usage: /goto <x> <y> <z>");
            return true;
        }

        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
        {
            CommandSupport.SendSystem(conn, "Usage: /goto <x> <y> <z>");
            return true;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            CommandSupport.SendSystem(conn, "You are not in a zone.");
            return true;
        }

        var newPos = new System.Numerics.Vector4(x, y, z, 1);
        var rot = conn.Player.Rotation;

        // Use the same logic as zoning/teleporting between zones,
        // but allow same-zone teleports now that we patched TeleportToZone.
        conn.Player.TeleportToZone(zone, newPos, rot);

        CommandSupport.SendSystem(conn, $"Teleported to ({x:0.0}, {y:0.0}, {z:0.0}) in zone {zone.Id}.");
        return true;
    }
}
