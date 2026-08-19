using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Combat;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class CommandPacketSetProfileHandler
{
    private static ILogger _logger = null!;
    private static IZoneManager _zoneManager = null!;
    private static IResourceManager _resourceManager = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CommandPacketSetProfileHandler));

        _zoneManager = serviceProvider.GetRequiredService<IZoneManager>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!CommandPacketSetProfile.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(CommandPacketSetProfile));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(CommandPacketSetProfile), packet);

        TryActivateProfile(connection.Player, packet.Id);

        return true;
    }

    // The whole job-swap sequence, extracted so the server can drive a swap itself rather than only in
    // response to the player clicking one. Snowball Battles forces Adventurer on entry this way.
    //
    // Returns false when the player doesn't own that profile, or already has it active - in both cases
    // nothing is sent, because re-activating the current job would be a visible no-op swirl AND would
    // clear the ability toolbar for no reason (see the toolbar re-send below).
    public static bool TryActivateProfile(Player player, int profileId)
    {
        var profile = player.Profiles.FirstOrDefault(x => x.Id == profileId);

        if (profile is null || player.ActiveProfileId == profileId)
            return false;

        player.ActiveProfileId = profileId;

        // Each job has its own level, so switching jobs rescales HP/mana/stats to the new job's rank.
        player.RecalculateStats(refill: true);

        // Refresh the (now-active) job's trait list to its current rank so the Traits panel is right after a
        // swap too, not just at login.
        player.RefreshTraits();

        var clientUpdatePacketActivateProfile = new ClientUpdatePacketActivateProfile();

        using var packetWriter = new PacketWriter();

        profile.Serialize(packetWriter);

        clientUpdatePacketActivateProfile.Payload = packetWriter.Buffer;

        clientUpdatePacketActivateProfile.Attachments = player.GetAttachments();

        clientUpdatePacketActivateProfile.Animation = 3001; // emo_outfit_all
        clientUpdatePacketActivateProfile.CompositeEffect = 4005; // PFX_Job_Swirl

        player.SendTunneled(clientUpdatePacketActivateProfile);

        // COMBAT: on swap to any kit job (ninja/archer), populate the ability toolbar from the
        // EQUIPPED WEAPON (same builder zone-load + equip use, FX cache warmed for first casts).
        // No kit weapon equipped => empty bar.
        if (JobWeaponAbilities.SendToolbarWithFxPreload(player, _resourceManager))
        {
            _logger.LogInformation("Sent weapon-driven ability SetDefinition on swap to profile {id}.", profile.Id);
        }
        else
        {
            // ★ A NO-KIT JOB STILL NEEDS A BAR SENT. The ActivateProfile above has already cleared the
            // client's toolbar, so skipping the send here left the OUTGOING job's abilities drawn on the
            // bar until something else refreshed it (a zone change). Swapping to the Adventurer is the
            // case that shows it. This send carries the player's own slots - held power-up, snowball tool -
            // and when they have neither, the explicit empty bar is what clears the old job's abilities.
            JobWeaponAbilities.SendToolbar(player, _resourceManager);
        }

        var playerUpdatePacketEquippedItemsChange = new PlayerUpdatePacketEquippedItemsChange();

        playerUpdatePacketEquippedItemsChange.Guid = player.Guid;

        playerUpdatePacketEquippedItemsChange.Attachments = clientUpdatePacketActivateProfile.Attachments;

        player.SendTunneledToVisible(playerUpdatePacketEquippedItemsChange);

        var friendStatusPacket = new FriendStatusPacket
        {
            Guid = player.Guid,
            Status =
            {
                ProfileId = player.ActiveProfile.Id,
                ProfileRank = player.ActiveProfile.Rank,
                ProfileIconId = player.ActiveProfile.Icon,
                ProfileNameId = player.ActiveProfile.NameId,
                ProfileBackgroundImageId = player.ActiveProfile.BadgeImageSet
            }
        };

        foreach (var friend in player.Friends)
        {
            if (!_zoneManager.TryGetPlayer(friend.Guid, out var friendPlayer))
                continue;

            friendPlayer.SendTunneled(friendStatusPacket);
        }

        return true;
    }
}