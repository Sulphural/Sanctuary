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

public class WaypointsChatCommand : GatewayChatCommand
{
    public WaypointsChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "waypoints";
    public override string Usage => "[clear]";
    public override string Description => "Shows the navigation graph nodes near you.";
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

        if (parts.Length >= 2 && parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            if (conn.Player.Zone is Sanctuary.Game.Zones.StartingZone clearZone)
            {
                foreach (var guid in CommandSupport.WaypointMarkerGuids)
                {
                    if (clearZone.TryGetNpc(guid, out var marker))
                        marker.Dispose();
                }
            }
            CommandSupport.SendSystem(conn, $"/waypoints clear -> removed {CommandSupport.WaypointMarkerGuids.Count} markers.");
            CommandSupport.WaypointMarkerGuids.Clear();
            return true;
        }

        if (conn.Player.Zone is not Sanctuary.Game.Zones.StartingZone startingZone)
        {
            CommandSupport.SendSystem(conn, "You must be in the starting zone to use /waypoints.");
            return true;
        }

        var radius = 60f;
        if (parts.Length >= 2 && !float.TryParse(parts[1], out radius))
        {
            CommandSupport.SendSystem(conn, "Usage: /waypoints [radius=60] | /waypoints clear");
            return true;
        }

        foreach (var guid in CommandSupport.WaypointMarkerGuids)
        {
            if (startingZone.TryGetNpc(guid, out var oldMarker))
                oldMarker.Dispose();
        }
        CommandSupport.WaypointMarkerGuids.Clear();

        const int maxMarkers = 60;
        var nodes = startingZone.GetNearbyWaypoints(conn.Player.Position, radius);
        var truncated = nodes.Count > maxMarkers;
        if (truncated)
            nodes = nodes.GetRange(0, maxMarkers);

        foreach (var (id, position, neighbors) in nodes)
        {
            if (!startingZone.TryCreateNpc(out var marker))
                continue;

            marker.ModelId = CommandSupport.WaypointMarkerModelId;
            marker.Name = $"wp {id}";
            marker.NameId = 0;
            marker.Static = true;
            marker.Scale = 1f;
            marker.Visible = true;
            marker.IsInteractable = false;
            marker.HideNamePlate = false;
            marker.UpdatePosition(position, System.Numerics.Quaternion.Identity);

            CommandSupport.WaypointMarkerGuids.Add(marker.Guid);
            CommandSupport.Logger.LogInformation("wp {id} @ ({x:F1}, {y:F1}, {z:F1}) -> neighbors [{neighbors}]",
                id, position.X, position.Y, position.Z, string.Join(",", neighbors));
        }

        CommandSupport.SendSystem(conn, $"/waypoints -> spawned {nodes.Count} markers within {radius} units{(truncated ? " (capped, use a smaller radius for full coverage)" : "")}. Node ids + neighbor lists are in the server log. /waypoints clear to remove.");
        return true;
    }
}
