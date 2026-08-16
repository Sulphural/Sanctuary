using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// 141/3 AddMatchRequest - the Matchmaking panel's "Join!" button.
//
// Puts the player in the queue, pushes the refreshed counts back so the row updates immediately, and
// launches the match once enough people are waiting.
//
// ★ The launch threshold is the queue's own MinPlayers, which for Snowball Fighting is retail's 4 - not
// something you can test alone. `/snowball queuecol 3 1` drops it to 1 live, no rebuild.
[PacketHandler]
public static class AddMatchRequestPacketHandler
{
    private static ILogger _logger = null!;
    private static IZoneManager _zoneManager = null!;
    private static Sanctuary.Game.Party.IPartyManager _partyManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(AddMatchRequestPacketHandler));

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _partyManager = serviceProvider.GetRequiredService<Sanctuary.Game.Party.IPartyManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!AddMatchRequestPacket.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(AddMatchRequestPacket));
            return false;
        }

        var player = connection.Player;

        // The packet carries its own player guid, but this connection's own player is the authority - a
        // client shouldn't be able to queue somebody else. Logged when they disagree rather than trusted.
        if (packet.PlayerGuid != player.Guid)
        {
            _logger.LogWarning("Matchmaking: join for queue {queue} claimed guid {claimed} but arrived on {actual}'s connection.",
                packet.QueueId, packet.PlayerGuid, player.Guid);
        }

        // ★ A GROUP QUEUES AS A GROUP. "Invite Friends" in the panel builds a real party and then only the
        // leader presses Join, so queueing just the sender showed "1 Waiting" for a party of four and the
        // match launched without them. The client thinks in whole groups too - lobby.lua checks
        // GroupHandler.getGroupSize against the queue's max and refuses with "GroupTooBig" - so the server
        // has to enrol every member off the one request.
        var party = _partyManager.GetParty(player);
        var joining = party?.Members ?? [player];

        foreach (var member in joining)
        {
            MatchmakingQueueTable.Join(packet.QueueId, member.Guid);

            member.MatchmakingQueueId = packet.QueueId;
            member.MatchmakingRequest = packet.RawRequest; // the leader's, for members who sent none
        }

        var waiting = MatchmakingQueueTable.WaitingIn(packet.QueueId);

        _logger.LogInformation("Matchmaking: {player} joined queue {queue} with {size} group member(s) ({waiting} waiting).",
            player.Name?.FullName, packet.QueueId, joining.Count, waiting);

        // Confirm the join by handing the request straight back (141/4). This is what populates the
        // client's Matchmaking.Requests data source - i.e. what makes the QuickMatch "in queue" indicator
        // appear and keeps the Lobby in its waiting state instead of falling back to the game list.
        //
        // Every member gets it, not just whoever pressed the button - they are all in the queue now, so
        // they all need the indicator and the refreshed count. The panel only refreshes its numbers when it
        // asks, so the stats ride along rather than waiting for each client's next poll.
        var confirmation = new AddMatchRequestResponsePacket { Request = packet.RawRequest };

        foreach (var member in joining)
        {
            member.SendTunneled(confirmation);
            MatchmakingQueueTable.SendStats(member, member.Guid);
        }

        TryLaunch(packet.QueueId);

        return true;
    }

    // Enough players are waiting: pull them all out of the queue and into the arena. Only Snowball
    // Fighting has a zone behind it - the other four queues fill up and simply sit there for now.
    private static void TryLaunch(int queueId)
    {
        if (queueId != MatchmakingQueueTable.SnowballQueueId)
            return;

        var queue = MatchmakingQueueTable.Snowball;
        var minimum = Math.Max(1, queue?.MinPlayers ?? 1);

        if (MatchmakingQueueTable.WaitingIn(queueId) < minimum)
            return;

        var guids = MatchmakingQueueTable.PlayersIn(queueId);

        // Clear the queue FIRST: teleporting is what makes each player stop waiting, and leaving them on
        // the roster while that happens would let a second join re-trigger the launch mid-flight.
        MatchmakingQueueTable.ClearQueue(queueId);

        var launched = 0;

        foreach (var guid in guids)
        {
            if (!_zoneManager.TryGetPlayer(guid, out var player))
                continue; // logged off between joining and launching

            // Out of the queue: they're starting a match, not waiting for one, so the QuickMatch indicator
            // goes now rather than following them in.
            MatchmakingQueueTable.Withdraw(player);

            // ★ NO STRAIGHT TELEPORT. A found match puts the MINIGAME START SCREEN up first - the framed
            // panel with the game's name, blurb and the spinner that flips to a green GO! - and the player
            // enters when they press it. That is how every other encounter in this codebase starts, and
            // pressing GO! comes back as EncounterParticipantRequestEntrance, routed by encounter id (71)
            // to EnterSnowballArena.
            SendStartScreen(player);

            launched++;
        }

        _logger.LogInformation("Matchmaking: Snowball Fighting start screen sent to {count} player(s) (needed {minimum}).",
            launched, minimum);
    }

    // The offer/start panel, in the order the real server used (mirrored from FrostfangArenaZone's proven
    // path): encounter state 2 -> 3 -> 4, the details packet with Launch=false (offer form, not launch),
    // then a beat later the ready ack that turns the spinner into GO!, and state 5 behind it.
    // Snowball Battles is played as the ADVENTURER (profile 1) - the snowball is thrown from the combat
    // toolbar, and whatever combat job the player wandered in with would otherwise bring its own weapon
    // abilities to a fight that is meant to be snowballs only. Forced at the start screen, which is the
    // last moment before the match, so the swirl and the new toolbar land while the panel is up.
    //
    // TryActivateProfile no-ops when they already are an Adventurer, which matters: re-activating the
    // current job re-sends the profile, and any profile re-send clears the ability toolbar.
    private const int AdventurerProfileId = 1;

    public static void ForceAdventurerJob(Player player)
    {
        if (CommandPacketSetProfileHandler.TryActivateProfile(player, AdventurerProfileId))
        {
            _logger.LogInformation("Snowball Fighting: switched {player} to the Adventurer job for the match.",
                player.Name?.FullName);
        }
    }

    private static void SendStartScreen(Player player)
    {
        ForceAdventurerJob(player);

        foreach (var state in new[] { 2, 3, 4 })
        {
            player.SendTunneled(new EncounterStatePacket
            {
                EncounterId = SnowballArenaZone.EncounterId,
                InstanceId = SnowballArenaZone.EncounterInstanceId,
                State = state,
            });
        }

        player.SendTunneled(new EncounterDetailsResponsePacket
        {
            Unknown = SnowballArenaZone.EncounterId,
            Unknown2 = SnowballArenaZone.EncounterInstanceId,
            NameId = SnowballArenaZone.ArenaNameId,
            DescriptionId = SnowballArenaZone.ArenaDescriptionId,
            IconId = SnowballArenaZone.ArenaIconId,
            Difficulty = 1,
            MiniGameType = 4, // COMBAT
            ActivityId = SnowballArenaZone.EncounterId,
            // Launch deliberately left false: this is the OFFER form, i.e. the start screen. The LAUNCH
            // form is sent later, inside the arena, by SnowballArenaZone.
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600);

                player.SendTunneled(new EncounterZoneIsReadyPacket());
                player.SendTunneled(new EncounterStatePacket
                {
                    EncounterId = SnowballArenaZone.EncounterId,
                    InstanceId = SnowballArenaZone.EncounterInstanceId,
                    State = 5,
                });
            }
            catch { }
        });
    }
}
