using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Game;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketSetEditModeHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketSetEditModeHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketSetEditMode.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketSetEditMode));
            return false;
        }

        _logger.LogTrace("Received {name} packet. ( {packet} )", nameof(ClientHousingPacketSetEditMode), packet);

        if (TryGetActiveHouse(connection, out var houseId))
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
            var house = dbContext.Houses
                .Include(h => h.Fixtures)
                .FirstOrDefault(h => h.Id == houseId && h.CharacterId == characterId);

            if (house is not null)
            {
                _logger.LogInformation(
                    "Player {PlayerGuid} set housing edit mode to {InEditMode} in house {HouseId}.",
                    connection.Player.Guid,
                    packet.InEditMode,
                    houseId);

                if (!packet.InEditMode)
                {
                    var canceledPreviewCount = CancelPendingPlacements(connection, houseId);
                    if (canceledPreviewCount > 0)
                    {
                        _logger.LogInformation(
                            "Canceled {PreviewCount} pending housing preview(s) for player {PlayerGuid} while leaving edit mode in house {HouseId}.",
                            canceledPreviewCount,
                            connection.Player.Guid,
                            houseId);
                    }
                }

                var wasInEditMode = HousingFixtureActorService.IsInEditMode(connection.Player);
                HousingFixtureActorService.SetEditMode(connection.Player, packet.InEditMode);

                // FixtureAsset only constructs the client's native housing
                // object while its local edit-mode flag is active. Publish that
                // state before replaying saved fixture updates; replaying first
                // leaves the actor visible but never registers its selectable
                // housing object.
                HouseOwnershipService.SendHouseInfoUpdate(connection, house, packet.InEditMode);

                if (packet.InEditMode && !wasInEditMode)
                {
                    var replayedFixtureCount = HousingFixtureActorService.ReplayPersistedFixtureUpdates(
                        connection.Player,
                        house,
                        _resourceManager);
                    _logger.LogInformation(
                        "Rebuilt {FixtureCount} housing fixture links while player {PlayerGuid} entered edit mode in house {HouseId}.",
                        replayedFixtureCount,
                        connection.Player.Guid,
                        houseId);
                }

                // The house instance is already resident when this packet arrives.
                // Re-sending InstanceData destroys and recreates the client's fixture
                // dictionary while the editor can still own a selected fixture. The
                // native move/rotate commands then dereference that stale selection.
                // Publish the small edit-state packet before the much larger
                // inventory list. Otherwise the client can keep displaying the
                // "Click to Decorate" button for several seconds while the
                // reliable fixture list is ahead of the state transition.
                if (packet.InEditMode)
                    HouseOwnershipService.SendFixtureItemList(connection, house, _resourceManager);

                return true;
            }
        }

        connection.SendTunneled(new HousingPacketUpdateHouseInfo
        {
            InEditMode = false
        });
        CancelPendingPlacements(connection, houseId: null);
        HousingFixtureActorService.SetEditMode(connection.Player, inEditMode: false);

        return true;
    }

    private static int CancelPendingPlacements(GatewayConnection connection, int? houseId)
    {
        var placements = houseId is { } activeHouseId
            ? HousingPlacementSession.TakeAll(connection.Player.Guid, activeHouseId)
            : HousingPlacementSession.TakeAll(connection.Player.Guid);

        foreach (var placement in placements)
        {
            connection.SendTunneled(new HousingPacketRemoveFixture
            {
                FixtureGuid = placement.FixtureGuid
            });
            HousingFixtureActorService.Remove(
                connection.Player,
                placement.HouseId,
                placement.FixtureGuid);
        }

        return placements.Count;
    }

    private static bool TryGetActiveHouse(GatewayConnection connection, out int houseId)
    {
        houseId = 0;

        if (connection.Player.CurrentHouseGuid == 0)
            return false;

        try
        {
            var id = GuidHelper.GetHouseId(connection.Player.CurrentHouseGuid);
            if (id > int.MaxValue)
                return false;

            houseId = (int)id;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
