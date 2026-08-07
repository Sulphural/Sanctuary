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

public class XpChatCommand : GatewayChatCommand
{
    public XpChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "xp";
    public override string Usage => "[amount]";
    public override string Description => "Grants your active job experience.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        var profile = conn.Player.ActiveProfile;

        if (parts.Length < 2)
        {
            CommandSupport.SendSystem(conn, $"Job {profile.NameId}: level {profile.Rank}/{Sanctuary.Game.Leveling.JobLeveling.MaxLevel}, " +
                $"{profile.LevelXpRaw}/{Sanctuary.Game.Leveling.JobLeveling.XpForLevel(profile.Rank)} XP ({profile.RankPercent}%). Usage: /xp <amount>");
            return true;
        }

        if (!int.TryParse(parts[1], out var amount) || amount <= 0)
        {
            CommandSupport.SendSystem(conn, "Usage: /xp <amount>");
            return true;
        }

        int before = profile.Rank;
        conn.Player.AwardXp(amount);

        if (profile.Rank > before)
            CommandSupport.SendSystem(conn, $"Gained {amount} XP - leveled up to {profile.Rank}! (HP {conn.Player.CurrentHitpoints}/{conn.Player.Stats[CharacterStatId.MaxHealth].Int})");
        else
            CommandSupport.SendSystem(conn, $"Gained {amount} XP. Level {profile.Rank}, {profile.LevelXpRaw}/{Sanctuary.Game.Leveling.JobLeveling.XpForLevel(profile.Rank)} ({profile.RankPercent}%)");

        return true;
    }
}
