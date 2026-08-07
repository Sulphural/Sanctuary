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

public class NoclipChatCommand : GatewayChatCommand
{
    public NoclipChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "noclip";
    public override string Usage => "<forward|back|left|right|up|down> [distance]";
    public override string Description => "Steps you through walls by teleporting.";
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
            CommandSupport.SendSystem(conn, "Usage: /noclip <forward|back|left|right|up|down> [distance=10]");
            return true;
        }

        var dir = parts[1].ToLowerInvariant();
        var distance = 10f;
        if (parts.Length >= 3 && !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out distance))
        {
            CommandSupport.SendSystem(conn, "Usage: /noclip <forward|back|left|right|up|down> [distance=10]");
            return true;
        }

        var zone = conn.Player.Zone;
        if (zone == null)
        {
            CommandSupport.SendSystem(conn, "You are not in a zone.");
            return true;
        }

        var rotation = conn.Player.Rotation;
        var heading = MathF.Atan2(rotation.X, rotation.Z);
        var forward = new System.Numerics.Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
        var right = new System.Numerics.Vector3(forward.Z, 0f, -forward.X);

        var offset = dir switch
        {
            "forward" or "f" => forward * distance,
            "back" or "b" => -forward * distance,
            "right" or "r" => right * distance,
            "left" or "l" => -right * distance,
            "up" or "u" => new System.Numerics.Vector3(0f, distance, 0f),
            "down" or "d" => new System.Numerics.Vector3(0f, -distance, 0f),
            _ => (System.Numerics.Vector3?)null,
        };

        if (offset is not { } o)
        {
            CommandSupport.SendSystem(conn, "Usage: /noclip <forward|back|left|right|up|down> [distance=10]");
            return true;
        }

        var current = conn.Player.Position;
        var newPos = new System.Numerics.Vector4(current.X + o.X, current.Y + o.Y, current.Z + o.Z, 1f);

        conn.Player.TeleportToZone(zone, newPos, rotation);

        CommandSupport.SendSystem(conn, $"Noclip {dir} {distance:0.#} -> ({newPos.X:0.0}, {newPos.Y:0.0}, {newPos.Z:0.0}).");
        return true;
    }
}
