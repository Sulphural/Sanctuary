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

public class PetListChatCommand : GatewayChatCommand
{
    public PetListChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "petlist";
    public override string Usage => "";
    public override string Description => "Lists your pets.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Player;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (conn.Player.Pets.Count == 0)
        {
            CommandSupport.SendSystem(conn, "You don't own any pets.");
            return true;
        }

        var petList = string.Join("\n", conn.Player.Pets.Select((p, i) =>
            $"Pet {i + 1}: DB ID={p.Id}, NameId={p.NameId}, ImageSetId={p.ImageSetId}, TintId={p.TintId}"));

        CommandSupport.SendSystem(conn, "Your pets:\n" + petList);
        return true;
    }
}
