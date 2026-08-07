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

public class AnnounceChatCommand : GatewayChatCommand
{
    public AnnounceChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "announce";
    public override string Usage => "<message>";
    public override string Description => "Announces a message to everyone online.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Mod;

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
            CommandSupport.SendSystem(conn, "Usage: /announce <message>");
            return true;
        }

        string msg = string.Join(" ", parts, 1, parts.Length - 1);

        var chatPacket = new PacketChat
        {
            Channel = ChatChannel.System,
            FromGuid = 0,                    // system / anonymous
            FromName = new NameData(),       // empty name
            Message = "[ANNOUNCEMENT] " + msg
        };

        int sentCount = 0;

        // Send to starting zone players
        foreach (var player in CommandSupport.ZoneManager.StartingZone.Players)
        {
            player.SendTunneled(chatPacket);
            sentCount++;
        }

        CommandSupport.SendSystem(conn, $"Announcement sent to {sentCount} player(s).");
        return true;
    }
}
