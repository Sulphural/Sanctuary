using System;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// INSTANCE (Frostfang Fury): handles EncounterParticipantRequestEntrancePacket (op41/sub108, C2S) — the GO!
// button on the adventure offer popup. Wire format (client ctor sub_8B6E70):
//   [op41][sub108][int encounterId][int unk2][ulong playerGuid]
// NOTE it arrives on the WORLD tunnel (PacketTunneledClientWorldPacket), not the client tunnel.
//
// GO! -> ENTER: a REAL server-side zone transfer (Player.TeleportToZone) into the FrostfangArenaZone —
// world sg_random_encounter_clearing, identified from the client's own pack data (see the zone class +
// docs/STATUS.md). The proper transfer rebuilds tiles/visibility and sets OverrideUpdateRadius=true, which
// is what makes arena NPCs actually render (the earlier client-only fake zoning left them invisible).
[PacketHandler]
public static class EncounterParticipantRequestEntranceHandler
{
    private static ILogger _logger = null!;
    private static IZoneManager _zoneManager = null!;
    private static Sanctuary.Game.Party.IPartyManager _partyManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(EncounterParticipantRequestEntranceHandler));

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _partyManager = serviceProvider.GetRequiredService<Sanctuary.Game.Party.IPartyManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, PacketReader reader)
    {
        _logger.LogInformation("EncounterParticipantRequestEntrance (GO! pressed) | body={hex}",
            Convert.ToHexString(reader.Span));

        // The body's first int is the encounter/activity id ([int encounterId][int unk2][ulong
        // playerGuid], client ctor sub_8B6E70) — route to the right arena. Unparseable/unknown ids
        // fall back to Frostfang (the pre-routing behavior).
        reader.TryRead(out int encounterId);

        if (encounterId == TormentedSpiritsArenaZone.EncounterId)
            EnterSpiritArena(connection);
        else if (Sanctuary.Game.Dungeons.DungeonCatalog.ByActivity.ContainsKey(encounterId))
            EnterEncounterArena(connection, encounterId);
        else
            EnterFrostfangArena(connection);

        return true;
    }

    // How long the leader waits for members to answer before the group launches anyway with whoever accepted.
    private const int InviteExpiryMs = 60_000;

    // GO! -> a data-driven combat dungeon (DungeonCatalog). A grouped LEADER does NOT enter yet — each member
    // gets the native accept/reject popup (op41/102) and the leader waits (see InvitePartyMembers) until every
    // member has answered, then the whole group is launched into the instance together (TryLaunchGroup). A
    // soloist — or a non-leader starting their own dungeon — enters immediately.
    public static void EnterEncounterArena(GatewayConnection connection, int activityId)
    {
        var player = connection.Player;
        var party = _partyManager.GetParty(player);

        if (party is not null && party.IsLeader(player) && party.Count > 1
            && InvitePartyMembers(player, activityId))
        {
            return; // leader waits; TryLaunchGroup pulls the whole group in once all have answered
        }

        EnterPlayerIntoEncounter(player, activityId);
    }

    // Teleport one player into the encounter arena for an activity (shared by the solo GO! path and the group
    // launch). Arenas are keyed by activity id, so the whole group lands in one instance.
    public static void EnterPlayerIntoEncounter(Player player, int activityId)
    {
        var arena = _zoneManager.GetOrCreateEncounterArena(activityId);
        player.EncounterReturnPosition = player.Position;
        player.TeleportToZone(arena, arena.SpawnPosition, arena.SpawnRotation, sky: null, geometryId: 0);
        player.SendTunneled(new MiniGameGameStartPacket(0, -1, -1));
        _logger.LogInformation("Encounter enter: {name} -> {zone} ({id}) (activity {a}).",
            player.Name, arena.Name, arena.Id, activityId);
    }

    // Invite each party member to the leader's dungeon with the native GAQ popup (op41/102) and put the leader
    // into the waiting state. Returns true when the leader should wait (invites sent), false when there's
    // nothing to invite (caller enters the leader solo). No-op / false for a non-leader.
    private static bool InvitePartyMembers(Player leader, int activityId)
    {
        var party = _partyManager.GetParty(leader);
        if (party is null || !party.IsLeader(leader))
            return false;

        if (!Sanctuary.Game.Dungeons.DungeonCatalog.ByActivity.TryGetValue(activityId, out var dungeon))
            return false;

        // Leader mashed GO! again while already waiting — keep the existing invite rather than re-popping.
        if (party.CurrentDungeonInvite is not null)
            return true;

        var invite = party.OpenDungeonInvite(activityId);
        if (invite is null)
            return false; // party of one — nobody to invite

        foreach (var member in party.Members)
        {
            if (member.Guid == leader.Guid)
                continue;

            SendEncounterInvite(member, activityId, leader.Guid);

            _logger.LogInformation("Encounter invite (op41/102): {leader} -> {member} for activity {id} ({name}).",
                leader.Name, member.Name, activityId, dungeon.Comment);
        }

        SendLeaderWaiting(leader, activityId, invite.Pending.Count);
        ScheduleInviteExpiry(party, invite);
        return true;
    }

    // The retail S2C invite: raises the member's native accept/reject popup. A is the GAQ notification category
    // that picks the popup label — A=87 = "join encounter" (the dungeon/encounter category), found by sweeping
    // A live via the dev-command runner (A=1 was the "house" category → the bogus "Default Housing NPC" text;
    // A=86="join Minigame", A=87="join encounter"). EncId/InstId ride the header; Guid = the inviter.
    private const int EncounterInviteCategory = 87;
    private static void SendEncounterInvite(Player member, int activityId, ulong inviterGuid) =>
        member.SendTunneled(new EncounterInvitationPacket
        {
            Unknown = activityId,          // EncounterId
            Unknown2 = 1,                  // InstanceId
            Guid = inviterGuid,            // inviter
            A = EncounterInviteCategory,   // 87 = "join encounter" popup category
            B = 0,
        });

    // Keep the host's dungeon start panel UP while they wait for members to answer. Pressing GO! otherwise
    // drops the leader out of the panel with no feedback — they don't enter (the whole group launches together
    // once everyone responds, see TryLaunchGroup), so the menu would just vanish. Re-sending the offer holds the
    // panel in its ready state; when the group launches, the teleport closes it. (The exact native "Waiting for
    // all group members…" banner is a client Lua/SWF render we couldn't reach from the server — this keeps the
    // dungeon menu visible instead of leaving the host staring at nothing.)
    private static void SendLeaderWaiting(Player leader, int activityId, int pending)
    {
        if (Sanctuary.Game.Dungeons.DungeonCatalog.ByActivity.TryGetValue(activityId, out var dungeon))
            StartingZone.SendDungeonOffer(leader, dungeon);
    }

    // A member answered the native popup (op41/103): record accept/decline. Once EVERY invited member has
    // answered, the whole group (leader + accepters) is launched into the instance together.
    public static void HandleInviteResponse(Player member, bool accepted)
    {
        var party = _partyManager.GetParty(member);
        if (party is null || party.IsLeader(member))
            return;

        var invite = party.CurrentDungeonInvite;
        if (invite is null)
            return;

        var allAnswered = accepted ? party.AcceptDungeon(member.Guid) : party.DeclineDungeon(member.Guid);
        if (allAnswered)
            TryLaunchGroup(party, invite);
    }

    // Launch the leader + everyone who accepted into the arena together. Atomically claims the invite so a
    // member's final answer and the expiry timer can't both launch it (whichever arrives first wins).
    private static void TryLaunchGroup(Sanctuary.Game.Party.Party party, Sanctuary.Game.Party.Party.DungeonInvite invite)
    {
        var claimed = party.TakeDungeonInvite(invite);
        if (claimed is null)
            return; // already launched / replaced

        foreach (var m in party.Members)
        {
            if (m.Guid == claimed.LeaderGuid || claimed.Accepted.Contains(m.Guid))
                EnterPlayerIntoEncounter(m, claimed.ActivityId);
        }
    }

    // If some member never answers, launch after the window with whoever accepted (+ the leader) so the leader
    // isn't stuck waiting forever.
    private static void ScheduleInviteExpiry(Sanctuary.Game.Party.Party party, Sanctuary.Game.Party.Party.DungeonInvite invite)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(InviteExpiryMs);
                TryLaunchGroup(party, invite);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dungeon invite expiry failed.");
            }
        });
    }

    // The one true GO!->arena entry: proper server-side zone transfer + the minigame
    // GameStart ack (op39/sub17) that drives the client's minigame state machine. CO-OP: the leader's
    // whole party is pulled into the Frostfang instance (which has the multi-player encounter
    // lifecycle — see FrostfangArenaZone).
    public static void EnterFrostfangArena(GatewayConnection connection)
    {
        var arena = _zoneManager.GetOrCreateFrostfangArena();

        void Enter(Player player)
        {
            // Sky = null so the world's natural bright-green daytime renders (VIDEO GROUND TRUTH
            // 2026-07-03; the old gloam sky was too dark).
            player.TeleportToZone(arena, arena.EffectiveSpawn, arena.SpawnRotation, sky: null, geometryId: 0);
            player.SendTunneled(new MiniGameGameStartPacket(0, -1, -1));
            _logger.LogInformation("GO! -> TeleportToZone {zone} ({id}) for {name} at {pos}.",
                arena.Name, arena.Id, player.Name, arena.EffectiveSpawn);
        }

        EnterWithParty(connection.Player, Enter);
    }

    // GO! -> the Tormented Spirits graveyard arena (same transfer recipe as Frostfang).
    // CO-OP: the leader's whole party is pulled in (the spirit arena now has the same multi-player
    // encounter lifecycle as Frostfang).
    public static void EnterSpiritArena(GatewayConnection connection)
    {
        var arena = _zoneManager.GetOrCreateSpiritArena();

        void Enter(Player player)
        {
            // Stash the overworld spot so the exit door returns each member to where THEY were standing
            // in the Blackspore graveyard (not the world spawn).
            player.EncounterReturnPosition = player.Position;
            player.TeleportToZone(arena, arena.SpawnPosition, arena.SpawnRotation, sky: null, geometryId: 0);
            player.SendTunneled(new MiniGameGameStartPacket(0, -1, -1));
            _logger.LogInformation("GO! -> TeleportToZone {zone} ({id}) for {name} at {pos}.",
                arena.Name, arena.Id, player.Name, arena.SpawnPosition);
        }

        EnterWithParty(connection.Player, Enter);
    }

    // Enter the leader, then pull every other party member through the same enter action
    // (co-op). For a soloist this is just the single enter.
    private static void EnterWithParty(Player leader, Action<Player> enter)
    {
        enter(leader);

        var party = _partyManager.GetParty(leader);
        if (party is null || !party.IsLeader(leader))
            return;

        foreach (var member in party.Members)
        {
            if (member.Guid == leader.Guid)
                continue;
            _logger.LogInformation("GO! -> pulling party member {name} into the arena.", member.Name);
            enter(member);
        }
    }
}
