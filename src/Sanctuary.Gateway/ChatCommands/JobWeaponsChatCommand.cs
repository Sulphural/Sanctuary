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

public class JobWeaponsChatCommand : GatewayChatCommand
{
    public JobWeaponsChatCommand(GatewayServer server) : base(server)
    {
    }

    public override string KeyWord => "jobweapons";
    public override string Usage => "";
    public override string Description => "Grants one weapon per unique special ability across every combat job.";
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

        var totalGranted = 0;
        var perJob = new List<string>();

        foreach (var kit in JobKits.All)
        {
            // Dedup by (special NameId, weapon rank) not just special NameId - collapses true reskin/dye
            // duplicates (same name AND same rank, e.g. Warrior's Whirlwind color variants) without also
            // hiding real distinct tiers that happen to share a special's name (e.g. Medic's Triage exists at
            // 5 different ranks with 5 different real damage numbers - the old NameId-only dedup granted just
            // the first one and silently skipped the other 4, which is exactly backwards from what testing
            // per-tier data needs. Bug found live 2026-07-29 while verifying the Medic weapon-data fix).
            var seen = new HashSet<(int NameId, int Rank)>();
            var grantedThisJob = 0;

            foreach (var weaponDefId in kit.WeaponDefIds)
            {
                if (!CommandSupport.ResourceManager.ClientItemDefinitions.TryGetValue(weaponDefId, out var def))
                    continue;

                var special = kit.SlotNameIcon(weaponDefId, 1);
                if (!seen.Add((special.NameId, def.MinProfileRank)))
                    continue; // same special AND same rank as an already-granted weapon - a true duplicate

                CommandSupport.GrantItem(conn, def, 1);
                grantedThisJob++;
                totalGranted++;
            }

            perJob.Add($"profile {kit.ProfileId}: {grantedThisJob}");
        }

        CommandSupport.SendSystem(conn, $"Gave {totalGranted} job weapons (one per unique special ability PER RANK) - {string.Join(", ", perJob)}.");
        return true;
    }
}
