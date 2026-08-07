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

public class PosChatCommand : GatewayChatCommand
{
    public PosChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "pos";
    public override string Usage => "[npc]";
    public override string Description => "Shows your coordinates, and nearby NPCs with 'npc'.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        var p = conn.Player;
        var pos = p.Position;
        var rot = p.Rotation;
        var heading = MathF.Atan2(rot.X, rot.Z);
        var deg = heading * 180f / MathF.PI;
        var world = p.Zone?.Name ?? "(no zone)";

        CommandSupport.SendSystem(conn, $"[POS] {p.Name?.FullName} @ {world}");
        CommandSupport.SendSystem(conn, $"  X={pos.X:0.00}  Y={pos.Y:0.00}  Z={pos.Z:0.00}  heading={deg:0}°");
        CommandSupport.SendSystem(conn, $"  new Vector4({pos.X:0.00}f, {pos.Y:0.00}f, {pos.Z:0.00}f, 1f)");
        CommandSupport.SendSystem(conn, $"  CenterX = {pos.X:0.00}f, CenterZ = {pos.Z:0.00}f, GroundY = {pos.Y:0.00}f");

        // NB: don't reuse {x}/{y}/{z} in this template — NLog binds named placeholders POSITIONALLY, so a
        // reused name needs another arg (an 11-placeholder template with 8 args threw FormatException on every
        // !pos). The copy-paste Vector4 form is already shown to the caller via SendSystem above.
        CommandSupport.Logger.LogInformation("[POS] {name} @ {world} | X={x:0.00} Y={y:0.00} Z={z:0.00} W={w:0.00} | heading={deg:0}deg ({h:0.000}rad)",
            p.Name?.FullName, world, pos.X, pos.Y, pos.Z, pos.W, deg, heading);

        if (parts.Length >= 2 && parts[1].StartsWith("npc", StringComparison.OrdinalIgnoreCase))
        {
            var zone = p.Zone;
            if (zone is not null)
            {
                float Dist(Npc n) { var dx = n.Position.X - pos.X; var dz = n.Position.Z - pos.Z; return MathF.Sqrt(dx * dx + dz * dz); }
                var near = zone.Npcs.Where(n => n.Visible).OrderBy(Dist).Take(12).ToList();
                CommandSupport.SendSystem(conn, $"  -- {near.Count} nearest NPCs --");
                foreach (var n in near)
                {
                    CommandSupport.SendSystem(conn, $"  model={n.ModelId} name={n.NameId} @ ({n.Position.X:0.00}, {n.Position.Y:0.00}, {n.Position.Z:0.00}) d={Dist(n):0.0}{(n.IsHostile ? " [hostile]" : "")}");
                    CommandSupport.Logger.LogInformation("[POS-NPC] {world} model={model} nameId={nameId} hostile={h} @ new Vector4({x:0.00}f,{y:0.00}f,{z:0.00}f,1f) dist={d:0.0}",
                        world, n.ModelId, n.NameId, n.IsHostile, n.Position.X, n.Position.Y, n.Position.Z, Dist(n));
                }
            }
        }
        return true;
    }
}
