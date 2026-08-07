using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Helpers;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Chat;

namespace Sanctuary.Gateway.ChatCommands;

// Shared plumbing for the ported chat commands: permission checks, name resolution, item granting and
// the little reply helpers. Lifted verbatim out of the old CommandRouter so the commands behave exactly
// as they did before the move to the ChatCommandManager.
internal static class CommandSupport
{
    private static ILogger _logger = null!;
    private static IZoneManager _zoneManager = null!;
    private static IResourceManager _resourceManager = null!;
    private static string _dbConnectionString = "Data Source=sanctuary.db";

    internal static ILogger Logger => _logger;
    internal static string DbConnectionString => _dbConnectionString;

    // State the old router held: who is flying, and the markers the last /waypoints call spawned.
    internal static readonly HashSet<ulong> FlyingPlayers = [];
    internal static readonly List<ulong> WaypointMarkerGuids = [];
    internal const int WaypointMarkerModelId = 9240;
    internal static IZoneManager ZoneManager => _zoneManager;
    internal static IResourceManager ResourceManager => _resourceManager;

    internal static void Initialize(IServiceProvider serviceProvider, ILogger logger,
        IZoneManager zoneManager, IResourceManager resourceManager, string dbConnectionString)
    {
        _logger = logger;
        _zoneManager = zoneManager;
        _resourceManager = resourceManager;
        _dbConnectionString = dbConnectionString;
    }

    // Prints a single clean line to the player's chat window via ChatPacketDebugChat (op15/3) — no
    // "[System] PlayerName:" prefix, unlike PacketChat. The text supports the client's inline markup
    // (<font color='#rrggbb' size='n'>…<br>…</font>), so command output reads as a plain message.
    internal static void SendSystem(GatewayConnection conn, string text)
    {
        conn.Player.SendTunneled(new ChatPacketDebugChat
        {
            PrintToChat = true,
            Message = text,
        });
    }

    internal static void SendMessageToPlayer(Player player, string message)
    {
        // Clean one-line message (no speaker prefix), same as SendSystem — see it for details.
        player.SendTunneled(new ChatPacketDebugChat
        {
            PrintToChat = true,
            Message = message,
        });
    }

    internal static bool IsAdmin(GatewayConnection conn)
    {
        // Use the database character ID, not the runtime GUID
        long characterId = (long)conn.Player.CharacterId;

        try
        {
            _logger.LogInformation("Checking admin status for character ID: {CharId}, DB: {DbConn}", characterId, _dbConnectionString);

            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT u.IsAdmin
            FROM Users u
            JOIN Characters c ON c.UserId = u.Id
            WHERE c.Id = $charId
            LIMIT 1;
        ";

            cmd.Parameters.AddWithValue("$charId", characterId);

            var result = cmd.ExecuteScalar();

            _logger.LogInformation("Admin check result for char {CharId}: {Result}", characterId, result);

            if (result == null || result is DBNull)
            {
                _logger.LogWarning("No admin result found for character {CharId}", characterId);
                return false;
            }

            bool isAdmin = Convert.ToInt32(result) == 1;
            _logger.LogInformation("Character {CharId} admin status: {IsAdmin}", characterId, isAdmin);
            return isAdmin;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking admin status for character {CharId}", characterId);
            return false;
        }
    }

    internal static bool IsPlayerAdmin(Player player)
    {
        long characterId = (long)player.CharacterId;

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT u.IsAdmin
                FROM Users u
                JOIN Characters c ON c.UserId = u.Id
                WHERE c.Id = $charId
                LIMIT 1;
            ";

            cmd.Parameters.AddWithValue("$charId", characterId);

            var result = cmd.ExecuteScalar();

            if (result == null || result is DBNull)
                return false;

            return Convert.ToInt32(result) == 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check admin status for player {Player}", player.Name.FullName);
            return false;
        }
    }

    internal static bool RequireAdmin(GatewayConnection conn)
    {
        if (!IsAdmin(conn))
        {
            SendSystem(conn, "You do not have permission to use this command.");
            return false;
        }
        return true;
    }

    internal static bool IsEnforcer(GatewayConnection conn)
    {
        // Only users with IsAdmin = 1 in the database can use Referee commands
        return IsAdmin(conn);
    }

    internal static bool RequireEnforcer(GatewayConnection conn)
    {
        if (!IsEnforcer(conn))
        {
            SendSystem(conn, "You must be a Referee (admin) to use this command.");
            return false;
        }
        return true;
    }

    internal static bool RequireOwnerForAdminManagement(GatewayConnection conn)
    {
        var userGuid = GetUserGuid(conn);

        if (userGuid != 1)
        {
            SendSystem(conn, "Only the server owner can manage admins.");
            return false;
        }

        return true;
    }

    internal static bool TryResolvePlayerNamePattern(string pattern, out string resolvedName, out string error)
    {
        resolvedName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Player name cannot be empty.";
            return false;
        }

        pattern = pattern.Trim();

        // Get all player full names from starting zone
        var allNames = _zoneManager.StartingZone.Players
            .Select(p => p.Name.FullName)
            .Distinct()
            .ToList();

        if (allNames.Count == 0)
        {
            error = "No players online.";
            return false;
        }

        // PASS 1: exact match (case-insensitive)
        var exact = allNames
            .Where(n => string.Equals(n, pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 1)
        {
            resolvedName = exact[0];
            return true;
        }
        if (exact.Count > 1)
        {
            error = $"Pattern '{pattern}' matches multiple players exactly: {string.Join(", ", exact)}. Please be more specific.";
            return false;
        }

        // PASS 2: prefix match (case-insensitive)
        var prefix = allNames
            .Where(n => n.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (prefix.Count == 1)
        {
            resolvedName = prefix[0];
            return true;
        }
        if (prefix.Count > 1)
        {
            error = $"Pattern '{pattern}' is ambiguous (prefix of: {string.Join(", ", prefix)}). Please type more of the name.";
            return false;
        }

        // PASS 3: contains match (case-insensitive)
        var contains = allNames
            .Where(n => n.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (contains.Count == 1)
        {
            resolvedName = contains[0];
            return true;
        }
        if (contains.Count > 1)
        {
            error = $"Pattern '{pattern}' matches multiple players: {string.Join(", ", contains)}. Please be more specific.";
            return false;
        }

        error = $"No player found matching '{pattern}'.";
        return false;
    }

    internal static bool TryResolveUsernamePattern(string pattern, out string resolvedUsername, out string error)
    {
        resolvedUsername = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Username cannot be empty.";
            return false;
        }

        pattern = pattern.Trim();

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Username FROM Users;";
            using var reader = cmd.ExecuteReader();

            var allUsernames = new List<string>();
            while (reader.Read())
            {
                allUsernames.Add(reader.GetString(0));
            }

            if (allUsernames.Count == 0)
            {
                error = "No users exist in the database.";
                return false;
            }

            // PASS 1: exact match (case-insensitive)
            var exact = allUsernames
                .Where(u => string.Equals(u, pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count == 1)
            {
                resolvedUsername = exact[0];
                return true;
            }
            if (exact.Count > 1)
            {
                error = $"Pattern '{pattern}' matches multiple usernames exactly: {string.Join(", ", exact)}. Please be more specific.";
                return false;
            }

            // PASS 2: prefix match (case-insensitive)
            var prefix = allUsernames
                .Where(u => u.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (prefix.Count == 1)
            {
                resolvedUsername = prefix[0];
                return true;
            }
            if (prefix.Count > 1)
            {
                error = $"Pattern '{pattern}' is ambiguous (prefix of: {string.Join(", ", prefix)}). Please type more of the username.";
                return false;
            }

            // PASS 3: contains match (case-insensitive)
            var contains = allUsernames
                .Where(u => u.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (contains.Count == 1)
            {
                resolvedUsername = contains[0];
                return true;
            }
            if (contains.Count > 1)
            {
                error = $"Pattern '{pattern}' matches multiple usernames: {string.Join(", ", contains)}. Please be more specific.";
                return false;
            }

            error = $"No user found matching '{pattern}'.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve username pattern.");
            error = "Error while resolving username pattern.";
            return false;
        }
    }

    internal static long? GetUserGuid(GatewayConnection conn)
    {
        long characterId = (long)conn.Player.CharacterId;

        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT c.UserId
            FROM Characters c
            WHERE c.Id = $charId
            LIMIT 1;
        ";
            cmd.Parameters.AddWithValue("$charId", characterId);

            var result = cmd.ExecuteScalar();
            if (result == null || result is DBNull)
                return null;

            return Convert.ToInt64(result);
        }
        catch
        {
            return null;
        }
    }

    internal static int ExecuteNonQuery(string sql, params (string name, object value)[] parameters)
    {
        try
        {
            using var db = new SqliteConnection(_dbConnectionString);
            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;

            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);

            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteNonQuery failed.");
            return 0;
        }
    }

    // Shared item-grant logic (definition push, stack-onto-existing-or-add-new, DB persist, client packets)
    // - factored out of /giveitem so other debug commands (e.g. /jobweapons) can silently grant a batch of
    // items without each one printing its own chat line. Returns the resulting stack count, or -1 on a DB
    // failure (new-item path only - stacking failures are logged but still treated as best-effort success
    // since the in-memory/client state is already correct either way, matching the original /giveitem behavior).
    internal static int GrantItem(GatewayConnection conn, ClientItemDefinition def, int count)
    {
        using var defWriter = new PacketWriter();
        defWriter.Write(new[] { def });
        conn.SendTunneled(new PlayerUpdatePacketItemDefinitions { Payload = defWriter.Buffer });

        // Stack onto existing item if the player already has one with matching tint
        var existing = conn.Player.Items.FirstOrDefault(x => x.Definition == def.Id && x.Tint == 0);
        if (existing is not null)
        {
            existing.Count += count;
            conn.SendTunneled(new ClientUpdatePacketItemUpdate { ItemGuid = existing.Id, Count = existing.Count });

            // Persist updated count
            try
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection(_dbConnectionString);
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE Items SET Count = $count WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$count", existing.Count);
                cmd.Parameters.AddWithValue("$id", existing.Id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update item count for item {Id}", existing.Id);
            }

            return existing.Count;
        }

        var newItem = new ClientItem { Definition = def.Id, Count = count, Tint = 0 };

        if (!conn.SaveItemToDatabase(newItem))
            return -1;

        conn.Player.Items.Add(newItem);

        using var itemWriter = new PacketWriter();
        newItem.Serialize(itemWriter);
        conn.SendTunneled(new ClientUpdatePacketItemAdd { Payload = itemWriter.Buffer });

        return newItem.Count;
    }

    internal static bool UnknownSubCommand(GatewayConnection conn, string root, string sub)
    {
        SendSystem(conn, $"Unknown /{root} subcommand '{sub}'. Try /help.");
        return true;
    }
}
