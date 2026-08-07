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

public class TutorialChatCommand : GatewayChatCommand
{
    public TutorialChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "tut";
    public override string Usage => "";
    public override string Description => "Enters the combat tutorial instance.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        var player = conn.Player;

        // The retail combat tutorial is a SUMMONED DUNGEON INSTANCE: Darkthorne teleports the player into
        // Briarheart Palace (the CombatTutorialZone). The tutorial auto-starts once the client finishes
        // loading the world (CombatTutorialZone.OnClientFinishedLoading), then teleports the player back out.
        if (player.Zone is Sanctuary.Game.Zones.CombatTutorialZone)
        {
            CommandSupport.SendSystem(conn, "!tut: you're already in the tutorial.");
            return true;
        }

        var tutorial = CommandSupport.ZoneManager.GetOrCreateCombatTutorial();
        player.EncounterReturnPosition = player.Position; // return here when the tutorial ends
        player.TeleportToZone(tutorial, tutorial.SpawnPosition, tutorial.SpawnRotation, sky: null, geometryId: 0);
        CommandSupport.SendSystem(conn, "!tut: entering the combat tutorial (Briarheart Palace)...");
        return true;
    }
}
