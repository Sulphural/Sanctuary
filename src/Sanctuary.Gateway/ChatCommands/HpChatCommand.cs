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

public class HpChatCommand : GatewayChatCommand
{
    public HpChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "hp";
    public override string Usage => "[full]";
    public override string Description => "Shows your health and mana, or heals you to full.";
    public override ChatCommandRole RequiredRole => ChatCommandRole.Admin;

    public override bool Handle(Player invoker, string[] args)
    {
        var conn = GetConnection(invoker);
        if (conn is null)
            return true;

        // The bodies moved over from CommandRouter index `parts` with the keyword at [0].
        var parts = new[] { KeyWord }.Concat(args).ToArray();

        if (parts.Length < 2)
        {
            var maxHp = conn.Player.Stats[CharacterStatId.MaxHealth].Int;
            CommandSupport.SendSystem(conn, $"HP: {conn.Player.CurrentHitpoints}/{maxHp} | Mana: {conn.Player.CurrentMana}/{conn.Player.Stats[CharacterStatId.MaxMana].Int} | In Combat: {conn.Player.InCombat}");
            return true;
        }

        // /hp set <value> — for testing
        if (parts[1].ToLower() == "set" && parts.Length >= 3 && int.TryParse(parts[2], out var newHp))
        {
            var maxHp = conn.Player.Stats[CharacterStatId.MaxHealth].Int;
            conn.Player.CurrentHitpoints = Math.Clamp(newHp, 0, maxHp);

            conn.Player.SendTunneled(new ClientUpdatePacketHitpoints
            {
                CurrentHitpoints = conn.Player.CurrentHitpoints,
                MaxHitpoints = maxHp
            });

            CommandSupport.SendSystem(conn, $"HP set to {conn.Player.CurrentHitpoints}/{maxHp}");
            return true;
        }

        // /hp full — heal to full
        if (parts[1].ToLower() == "full")
        {
            var maxHp = conn.Player.Stats[CharacterStatId.MaxHealth].Int;
            var maxMana = conn.Player.Stats[CharacterStatId.MaxMana].Int;
            conn.Player.CurrentHitpoints = maxHp;
            conn.Player.CurrentMana = maxMana;

            conn.Player.SendTunneled(new ClientUpdatePacketHitpoints
            {
                CurrentHitpoints = maxHp,
                MaxHitpoints = maxHp
            });

            conn.Player.SendTunneled(new ClientUpdatePacketMana
            {
                CurrentMana = maxMana,
                MaxMana = maxMana,
                ShowOverHead = false
            });

            CommandSupport.SendSystem(conn, $"Healed to full! HP: {maxHp}/{maxHp}, Mana: {maxMana}/{maxMana}");
            return true;
        }

        CommandSupport.SendSystem(conn, "Usage: /hp | /hp set <value> | /hp full");
        return true;
    }
}
