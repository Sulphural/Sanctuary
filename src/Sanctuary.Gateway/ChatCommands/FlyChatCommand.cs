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

public class FlyChatCommand : GatewayChatCommand
{
    public FlyChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "fly";
    public override string Usage => "";
    public override string Description => "Toggles fly mode.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        var guid = conn.Player.Guid;
        bool enabling = CommandSupport.FlyingPlayers.Add(guid); // returns false if already present → toggle off
        if (!enabling)
            CommandSupport.FlyingPlayers.Remove(guid);

        var packet = new ClientUpdatePacketUpdateStat { Guid = guid };

        if (enabling)
        {
            packet.Stats.AddRange([
                new CharacterStat(CharacterStatId.GlideEnabled, 1),
                new CharacterStat(CharacterStatId.GlideDefaultForwardSpeed, 50f),
                new CharacterStat(CharacterStatId.GlideMinForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideMaxForwardSpeed, 100f),
                new CharacterStat(CharacterStatId.GlideAccel, 50f),
                // EXPERIMENT: negative fall speed instead of 0. There's no dedicated "climb" stat in the
                // client's stat list, so this is a guess at whether GlideFallSpeed is signed (rise) or
                // gets clamped to a magnitude (still just floats). If this doesn't visibly make you climb
                // while gliding, it's a dead end — say so and we'll build noclip via teleport-stepping
                // instead, which doesn't depend on undocumented client behavior.
                new CharacterStat(CharacterStatId.GlideFallSpeed, -15f),
                new CharacterStat(CharacterStatId.GlideFallTime, 999999f),
                new CharacterStat(CharacterStatId.MaxMovementSpeed, 50f),
            ]);
            CommandSupport.SendSystem(conn, "Fly mode ON — jump to glide. Testing negative fall speed for climb; tell me if altitude actually changes.");
        }
        else
        {
            packet.Stats.AddRange([
                new CharacterStat(CharacterStatId.GlideEnabled, 0),
                new CharacterStat(CharacterStatId.GlideDefaultForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideMinForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideMaxForwardSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideAccel, 0f),
                new CharacterStat(CharacterStatId.GlideFallSpeed, 0f),
                new CharacterStat(CharacterStatId.GlideFallTime, 0f),
                new CharacterStat(CharacterStatId.MaxMovementSpeed, 8f),
            ]);
            CommandSupport.SendSystem(conn, "Fly mode OFF.");
        }

        conn.Player.SendTunneled(packet);
        return true;
    }
}
