using System.Collections.Generic;
using System.Linq;

using Sanctuary.Game.Entities;

namespace Sanctuary.Game.Party;

// A transient in-memory party (the client's "group"). Unlike a guild it is NOT persisted — it
// exists only while it has members and is discarded when it empties. One member is the leader
// (the only one who can invite/kick); the leader passes to another member if the leader leaves.
public sealed class Party
{
    // Retail groups cap at 4 (the combatGroupWindow shows up to 4 member panes).
    public const int MaxMembers = 4;

    private readonly object _lock = new();
    private readonly List<Player> _members = [];

    // Guids the leader has invited who haven't accepted/declined yet.
    private readonly HashSet<ulong> _pendingInvites = [];

    public ulong LeaderGuid { get; private set; }

    public Party(Player leader)
    {
        LeaderGuid = leader.Guid;
        _members.Add(leader);
    }

    public IReadOnlyList<Player> Members
    {
        get { lock (_lock) return [.. _members]; }
    }

    public int Count
    {
        get { lock (_lock) return _members.Count; }
    }

    public bool IsFull
    {
        get { lock (_lock) return _members.Count >= MaxMembers; }
    }

    public bool IsLeader(Player player) => LeaderGuid == player.Guid;

    public bool Contains(Player player)
    {
        lock (_lock) return _members.Any(m => m.Guid == player.Guid);
    }

    public void AddPendingInvite(ulong guid)
    {
        lock (_lock) _pendingInvites.Add(guid);
    }

    public bool HasPendingInvite(ulong guid)
    {
        lock (_lock) return _pendingInvites.Contains(guid);
    }

    public bool TryAcceptInvite(Player player)
    {
        lock (_lock)
        {
            if (!_pendingInvites.Remove(player.Guid))
                return false;
            if (_members.Count >= MaxMembers || _members.Any(m => m.Guid == player.Guid))
                return false;
            _members.Add(player);
            return true;
        }
    }

    public void ClearInvite(ulong guid)
    {
        lock (_lock) _pendingInvites.Remove(guid);
    }

    // ── Co-op dungeon invite ──────────────────────────────────────────────────────────────────────────────
    // When the LEADER starts a combat dungeon, the members aren't force-pulled — each is offered the dungeon
    // and joins by pressing GO! on their own panel. This tracks who still owes a response so the leader can be
    // told "X joined (a/n)" and the invite can be summarised/expired.
    public sealed class DungeonInvite
    {
        public required int ActivityId { get; init; }
        public required ulong LeaderGuid { get; init; }
        public HashSet<ulong> Pending { get; } = [];   // invited members still to answer
        public HashSet<ulong> Accepted { get; } = [];  // members who accepted — launched with the leader
    }

    private DungeonInvite? _dungeonInvite;

    // Open a fresh dungeon invite for every non-leader member, replacing any prior one. Returns the invite, or
    // null when there's nobody to invite (party of one).
    public DungeonInvite? OpenDungeonInvite(int activityId)
    {
        lock (_lock)
        {
            var others = _members.Where(m => m.Guid != LeaderGuid).Select(m => m.Guid).ToList();
            if (others.Count == 0)
            {
                _dungeonInvite = null;
                return null;
            }

            var invite = new DungeonInvite { ActivityId = activityId, LeaderGuid = LeaderGuid };
            foreach (var g in others)
                invite.Pending.Add(g);
            _dungeonInvite = invite;
            return invite;
        }
    }

    public DungeonInvite? CurrentDungeonInvite
    {
        get { lock (_lock) return _dungeonInvite; }
    }

    public void CloseDungeonInvite()
    {
        lock (_lock) _dungeonInvite = null;
    }

    // A member accepted (✓): move them Pending -> Accepted. Returns true once EVERY invited member has answered
    // (so the caller can launch the whole group together).
    public bool AcceptDungeon(ulong guid)
    {
        lock (_lock)
        {
            if (_dungeonInvite is null)
                return false;
            if (_dungeonInvite.Pending.Remove(guid))
                _dungeonInvite.Accepted.Add(guid);
            return _dungeonInvite.Pending.Count == 0;
        }
    }

    // A member declined (✗): drop them from Pending. Returns true once every invited member has answered.
    public bool DeclineDungeon(ulong guid)
    {
        lock (_lock)
        {
            if (_dungeonInvite is null)
                return false;
            _dungeonInvite.Pending.Remove(guid);
            return _dungeonInvite.Pending.Count == 0;
        }
    }

    // Atomically claim the invite for launch so the all-answered path and the expiry timer can't both launch it.
    // Returns the invite exactly once (then clears it); null if it was already claimed / replaced.
    public DungeonInvite? TakeDungeonInvite(DungeonInvite expected)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_dungeonInvite, expected))
                return null;
            _dungeonInvite = null;
            return expected;
        }
    }

    // Remove a member; returns true if the party is now empty (caller disposes it). If the
    // leader left, leadership passes to the next remaining member.
    public bool Remove(Player player)
    {
        lock (_lock)
        {
            _members.RemoveAll(m => m.Guid == player.Guid);
            _dungeonInvite?.Pending.Remove(player.Guid); // a leaver no longer owes a dungeon response
            if (_members.Count > 0 && LeaderGuid == player.Guid)
                LeaderGuid = _members[0].Guid;
            return _members.Count <= 1; // a party of one isn't a party — caller tears it down
        }
    }
}
