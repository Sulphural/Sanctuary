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

public class PetDespawnChatCommand : GatewayChatCommand
{
    public PetDespawnChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "petdespawn";
    public override string Usage => "";
    public override string Description => "Despawns your active pet.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (conn.Player.Pet is null)
        {
            CommandSupport.SendSystem(conn, "You don't have an active pet.");
            return true;
        }

        conn.Player.Pet.Dispose();
        conn.Player.Pet = null;

        CommandSupport.SendSystem(conn, "Pet despawned!");
        return true;
    }
}
