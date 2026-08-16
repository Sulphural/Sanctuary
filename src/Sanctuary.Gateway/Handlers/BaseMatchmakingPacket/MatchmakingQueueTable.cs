using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Gateway.Handlers;

// The queue list the Matchmaking panel shows. Lifted out of ListQueuesRequestPacketHandler so the stats
// path (141/13) and the dev probe can serve the same rows rather than keeping a second copy.
//
// ★★ THE "N Waiting / Avg Wait" NUMBERS ARE **NOT** IN THIS RECORD - settled the hard way. Reading
// lobby.lua's `Lobby:QueuesPopulate`, which does `GetData(row, 3/15/16)` around the "InQueue"/"AvgWait"
// code-strings, made it look like they were columns here. They aren't: stamping EVERY int field with a
// recognisable 1000+index marker showed Min Players = 1003, Max Players = 1004 and the label = string
// 1015, while **Waiting still read 0 and Avg Wait still read "-"**. The feed is QueueStatsResponse
// (141/14) - see SendStats below.
//
// ★ The live `/snowball queuecol` probes did pin three fields down, and they match the names already
// here: field 3 = MinPlayers, field 15 = EncounterDescriptionId, field 16 = EncounterIcon. So the Lua's
// column numbers are NOT our field indices - the data source has its own layout. Two attempts at
// inferring identities from those numbers were wrong; stamp markers and look at the screen instead
// (`/snowball queuescan`).
public static class MatchmakingQueueTable
{
    // Reflection over the public fields IN DECLARATION ORDER is exactly the order Serialize writes them,
    // so a "column index" here means the same thing it means on the wire. Used only by the dev probe.
    private static readonly FieldInfo[] _fields = typeof(MatchmakingQueueDefinition)
        .GetFields(BindingFlags.Public | BindingFlags.Instance);

    public static int ColumnCount => _fields.Length;

    // Retail's own five rows, in the order the panel lists them.
    public static readonly List<MatchmakingQueueDefinition> Queues =
    [
        new()
        {
            // Pirate's Plunder
            Id = 5,
            NameId = 427834,
            MatchType = 13,
            MinPlayers = 1,
            MaxPlayers = 5,
            MinTeams = 1,
            MaxTeams = 1,
            MaxGameStartDelay = 30,
            Param5 = 420998,
            Param6 = 30434,
            Param7 = 1,
            EncounterDescriptionId = 420998,
            EncounterIcon = 30434,
            Unknown2 = 26,
            MemberOnly = true,
            Unknown3 = true,
        },
        new()
        {
            // Soccer
            Id = 11,
            NameId = 31030,
            MatchType = 12,
            MinPlayers = 1,
            MaxPlayers = 3,
            MinTeams = 1,
            MaxTeams = 2,
            MaxGameStartDelay = 120,
            Param2 = 3,
            Param4 = 8,
            Param5 = 37947,
            Param6 = 20992,
            Param7 = 4,
            EncounterDescriptionId = 37947,
            EncounterIcon = 20992,
            Unknown = 8,
            Unknown3 = true,
        },
        new()
        {
            // Kart Racing
            Id = 21,
            NameId = 426304,
            MatchType = 10,
            MinPlayers = 1,
            MaxPlayers = 5,
            MinTeams = 1,
            MaxTeams = 1,
            MaxGameStartDelay = 120,
            Param1 = 1,
            Param2 = 3,
            Param4 = 5,
            Param5 = 407515,
            Param6 = 20991,
            Param7 = 2,
            EncounterDescriptionId = 407515,
            EncounterIcon = 20991,
            Unknown = 5,
            Unknown3 = true,
        },
        new()
        {
            // Demo Derby
            Id = 31,
            NameId = 382789,
            MatchType = 11,
            MinPlayers = 1,
            MaxPlayers = 5,
            MinTeams = 1,
            MaxTeams = 1,
            MaxGameStartDelay = 120,
            Param4 = 7,
            Param5 = 37942,
            Param6 = 9871,
            Param7 = 3,
            EncounterDescriptionId = 37942,
            EncounterIcon = 9871,
            Unknown = 7,
            Unknown3 = true,
        },
        new()
        {
            // Snowball Fighting - ours. NameId 419545, description 419546 ("Pick up snowballs and throw
            // them at the other team to win!"). MinPlayers/MaxPlayers 4 is what the capture carried.
            Id = SnowballQueueId,
            NameId = 419545,
            MatchType = 4,
            MinPlayers = 4,
            MaxPlayers = 4,
            MinTeams = 1,
            MaxTeams = 1,
            MaxGameStartDelay = 0,
            Param1 = 369,
            EncounterDescriptionId = 419546,
            EncounterIcon = 282,
            Unknown = 1,
            Unknown3 = true,
        },
    ];

    public const int SnowballQueueId = 51;

    public static MatchmakingQueueDefinition? Snowball => Queues.FirstOrDefault(q => q.Id == SnowballQueueId);

    // Who is actually sitting in each queue - the server-side truth, used to decide when a match can start.
    // ★ It is NOT yet mirrored into the packet, because which field the panel reads as "N Waiting" is the
    // open question above. Once queuescan names it, stamping it here is a one-liner.
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<ulong, byte>> _waiting = new();

    public static int WaitingIn(int queueId) =>
        _waiting.TryGetValue(queueId, out var players) ? players.Count : 0;

    public static IReadOnlyCollection<ulong> PlayersIn(int queueId) =>
        _waiting.TryGetValue(queueId, out var players) ? players.Keys.ToArray() : [];

    public static void Join(int queueId, ulong playerGuid) =>
        _waiting.GetOrAdd(queueId, _ => new ConcurrentDictionary<ulong, byte>())[playerGuid] = 0;

    // Called when a player leaves a queue, enters a match, disconnects - anything that stops them waiting.
    // Sweeps every queue because a player is only ever meant to be in one, and a stale entry would inflate
    // the count forever.
    public static void Leave(ulong playerGuid)
    {
        foreach (var (_, players) in _waiting)
            players.TryRemove(playerGuid, out _);
    }

    // Take one player out of the queue AND off their own screen: 141/5 ClearMatchRequest removes their row
    // from the client's Matchmaking.Requests, which is what makes the QuickMatch indicator and the row's
    // "*WAITING*" marker go away. 141/6 only ever arrives from whoever pressed the button, so every other
    // member of a group has to be withdrawn explicitly like this or they keep showing as queued.
    public static void Withdraw(Player player)
    {
        Leave(player.Guid);

        if (player.MatchmakingRequest is { } request)
            player.SendTunneled(new ClearMatchRequestPacket { Request = request });

        player.MatchmakingQueueId = 0;
        player.MatchmakingRequest = null;

        SendStats(player, player.Guid);
    }

    public static void ClearQueue(int queueId)
    {
        if (_waiting.TryGetValue(queueId, out var players))
            players.Clear();
    }

    // Average wait per queue, in seconds. Retail's own observed figures, read off a screenshot of this
    // panel ("Avg Wait: 0:20" and so on) - the client formats the m:ss itself.
    private static readonly Dictionary<int, int> _averageWaitSeconds = new()
    {
        [5] = 20,                  // Pirate's Plunder  0:20
        [11] = 87,                 // Soccer            1:27
        [21] = 76,                 // Kart Racing       1:16
        [31] = 63,                 // Demo Derby        1:03
        [SnowballQueueId] = 61,    // Snowball Fighting 1:01
    };

    // Forces a waiting count for the Snowball row regardless of who is really queued, so the pairing of the
    // two int lists in QueueStatsResponse can be checked without four testers. Negative = off.
    public static int WaitingOverride { get; set; } = -1;

    // The "N Waiting / Avg Wait" feed (141/14). Both lists run parallel to Queues, which is the order the
    // client received them in from 141/2.
    public static void SendStats(Player player, ulong guid)
    {
        var stats = new QueueStatsResponsePacket { Guid = guid };

        foreach (var queue in Queues)
        {
            var waiting = queue.Id == SnowballQueueId && WaitingOverride >= 0
                ? WaitingOverride
                : WaitingIn(queue.Id);

            stats.PlayersWaiting.Add(waiting);
            stats.AverageWaitSeconds.Add(_averageWaitSeconds.GetValueOrDefault(queue.Id));
        }

        player.SendTunneled(stats);
    }

    // ── The column probe ───────────────────────────────────────────────────────────────────────────────
    // Stamp EVERY int field with 1000+its index and re-send. Whatever the panel then shows as "N Waiting"
    // and "Avg Wait" reads back the index directly: "1013 Waiting" means field 13, and an Avg Wait of
    // 16:54 (=1014 seconds) means field 14. One pass, no more guessing from the Lua's column numbers.
    // Restore() puts the real row back.
    private static readonly Dictionary<int, int> _saved = [];

    public static void StampProbeMarkers(MatchmakingQueueDefinition queue)
    {
        for (var i = 0; i < _fields.Length; i++)
        {
            if (_fields[i].FieldType != typeof(int))
                continue;

            _saved.TryAdd(i, (int)_fields[i].GetValue(queue)!);
            _fields[i].SetValue(queue, 1000 + i);
        }
    }

    public static void Restore(MatchmakingQueueDefinition queue)
    {
        foreach (var (index, value) in _saved)
            _fields[index].SetValue(queue, value);

        _saved.Clear();
    }

    // Push the whole list to one player. Both 141/1 (the panel asking for the list) and 141/13 (the panel
    // asking for fresh stats) answer with this, because the stats live in these very rows.
    public static void Send(Player player, ulong guid)
    {
        var response = new ListQueuesResponsePacket { Guid = guid };

        response.Queues.AddRange(Queues);

        player.SendTunneled(response);
    }

    // Dev probe: set one column of one queue by wire index, so a live pass can identify which index the
    // client reads as "players waiting" and which as "average wait". Returns the field name it wrote.
    public static string? TrySetColumn(MatchmakingQueueDefinition queue, int column, int value)
    {
        if (column < 0 || column >= _fields.Length)
            return null;

        var field = _fields[column];

        if (field.FieldType == typeof(int))
            field.SetValue(queue, value);
        else if (field.FieldType == typeof(bool))
            field.SetValue(queue, value != 0);
        else
            return null;

        return field.Name;
    }

    public static string DescribeColumns() =>
        string.Join(", ", _fields.Select((f, i) => $"{i}={f.Name}"));
}
