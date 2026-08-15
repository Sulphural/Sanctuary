using System;
using System.Linq;
using System.Numerics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Gateway.Admin;

public static class HousingFixturePlacementCommitService
{
    public static bool TryCommit(
        GatewayConnection connection,
        DatabaseContext dbContext,
        DbHouse house,
        PendingHousingFixturePlacement placement,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager,
        ILogger logger)
    {
        if (house.Fixtures.Count >= house.MaxFixtureCount)
        {
            logger.LogWarning(
                "Player {PlayerGuid} tried to exceed fixture limit in house {HouseId}.",
                connection.Player.Guid,
                house.Id);
            CancelPreview(connection, house.Id, placement.FixtureGuid);
            return false;
        }

        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var sourceItem = dbContext.Items.FirstOrDefault(item =>
            item.CharacterId == characterId &&
            item.Id == placement.ItemRecordId &&
            item.Definition == placement.ItemDefinitionId &&
            item.Count > 0);

        if (sourceItem is null)
        {
            logger.LogWarning(
                "Player {PlayerGuid} tried to commit pending fixture {FixtureGuid} without owned item {ItemRecordId}/{ItemDefinitionId}.",
                connection.Player.Guid,
                placement.FixtureGuid,
                placement.ItemRecordId,
                placement.ItemDefinitionId);
            CancelPreview(connection, house.Id, placement.FixtureGuid);
            return false;
        }

        var normalizedScale = scale <= 0 ? 1.0f : scale;
        var persistedRotation = HousingFixtureActorService.ToHousingRotation(rotation);
        var dbFixture = new DbHouseFixture
        {
            HouseId = house.Id,
            ItemDefinitionId = placement.ItemDefinitionId,
            PositionX = position.X,
            PositionY = position.Y,
            PositionZ = position.Z,
            PositionW = position.W,
            RotationX = persistedRotation.X,
            RotationY = persistedRotation.Y,
            RotationZ = persistedRotation.Z,
            RotationW = persistedRotation.W,
            Scale = normalizedScale,
            Created = DateTimeOffset.UtcNow
        };
        HouseOwnershipService.SetFixtureTintId(dbFixture, placement.TintId);

        var sourceItemId = sourceItem.Id;
        var sourceItemCount = sourceItem.Count - 1;

        if (sourceItemCount <= 0)
            dbContext.Items.Remove(sourceItem);
        else
            sourceItem.Count = sourceItemCount;

        dbContext.HouseFixtures.Add(dbFixture);
        dbContext.SaveChanges();

        // A fixture-item-list refresh only clears the preview render; it does not
        // release the native editor object. End the temporary cursor explicitly,
        // then publish the committed fixture under its stable database identity.
        // Reusing the pending GUID leaves a quantity-one item attached to the
        // cursor and makes later selection state ambiguous.
        connection.SendTunneled(new HousingPacketRemoveFixture
        {
            FixtureGuid = placement.FixtureGuid
        });
        HousingFixtureActorService.Remove(connection.Player, house.Id, placement.FixtureGuid);

        var persistedFixtureGuid = (ulong)dbFixture.Id;

        HousingFixtureActorService.PublishFixture(
            connection.Player,
            house,
            persistedFixtureGuid,
            dbFixture,
            placement.TintId,
            resourceManager);

        UpdateConsumedInventoryItem(connection, sourceItemId, sourceItemCount);

        var refreshedHouse = dbContext.Houses
            .Include(candidate => candidate.Fixtures)
            .First(candidate => candidate.Id == house.Id);
        if (sourceItemCount <= 0)
            HouseOwnershipService.SendFixtureItemList(connection, refreshedHouse, resourceManager);
        HouseOwnershipService.SendHouseInfoUpdate(connection, refreshedHouse, inEditMode: true);

        PendingHousingFixturePlacement? nextPlacement = null;
        if (sourceItemCount > 0 && refreshedHouse.Fixtures.Count < refreshedHouse.MaxFixtureCount)
        {
            nextPlacement = HousingPlacementSession.Start(
                connection.Player.Guid,
                house.Id,
                placement.ItemDefinitionId,
                placement.ItemRecordId,
                placement.TintId);

            if (!HouseOwnershipService.SendFixtureAsset(
                    connection,
                    placement.ItemDefinitionId,
                    placement.TintId,
                    resourceManager,
                    isPreview: true))
            {
                HousingPlacementSession.TryTake(
                    connection.Player.Guid,
                    house.Id,
                    nextPlacement.Value.FixtureGuid,
                    out _);
                logger.LogWarning(
                    "Could not continue fixture placement for item {ItemDefinitionId} after fixture {FixtureId} was committed for player {PlayerGuid}.",
                    placement.ItemDefinitionId,
                    dbFixture.Id,
                    connection.Player.Guid);
                nextPlacement = null;
            }
        }

        logger.LogInformation(
            "Player {PlayerGuid} committed pending fixture {PendingFixtureGuid} as fixture {FixtureId}/{PersistedFixtureGuid} item {ItemDefinitionId} in house {HouseId} at {Position} with rotation {Rotation} and scale {Scale}; inventory row {ItemRecordId} now has count {ItemCount}; next preview is {NextPreviewGuid}.",
            connection.Player.Guid,
            placement.FixtureGuid,
            dbFixture.Id,
            persistedFixtureGuid,
            dbFixture.ItemDefinitionId,
            house.Id,
            position,
            persistedRotation,
            normalizedScale,
            sourceItemId,
            Math.Max(0, sourceItemCount),
            nextPlacement?.FixtureGuid ?? 0);

        return true;
    }

    private static void CancelPreview(GatewayConnection connection, int houseId, ulong fixtureGuid)
    {
        connection.SendTunneled(new HousingPacketRemoveFixture
        {
            FixtureGuid = fixtureGuid
        });
        HousingFixtureActorService.Remove(connection.Player, houseId, fixtureGuid);
    }

    private static void UpdateConsumedInventoryItem(GatewayConnection connection, int itemRecordId, int newCount)
    {
        var clientItem = connection.Player.Items.SingleOrDefault(item => item.Id == itemRecordId);

        if (newCount <= 0)
        {
            if (clientItem is not null)
                connection.Player.Items.Remove(clientItem);

            connection.SendTunneled(new ClientUpdatePacketItemDelete
            {
                ItemGuid = itemRecordId
            });
            return;
        }

        if (clientItem is not null)
            clientItem.Count = newCount;

        connection.SendTunneled(new ClientUpdatePacketItemUpdate
        {
            ItemGuid = itemRecordId,
            Count = newCount
        });
    }
}
