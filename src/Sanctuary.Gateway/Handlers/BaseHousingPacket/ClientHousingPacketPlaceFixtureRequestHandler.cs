using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketPlaceFixtureRequestHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    private readonly record struct ParsedPlaceFixtureRequest(
        int ItemDefinitionId,
        int? ItemRecordId,
        Vector4 Position,
        Quaternion Rotation,
        float Scale,
        bool IsSelectionOnly);

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketPlaceFixtureRequestHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!TryParsePlaceRequest(connection.Player, data, out var packet))
        {
            _logger.LogError(
                "Failed to deserialize {packet}. Length={length} Data={data}",
                nameof(ClientHousingPacketPlaceFixtureRequest),
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouse(connection, out var houseId))
            return true;

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var house = dbContext.Houses
            .Include(h => h.Fixtures)
            .FirstOrDefault(h => h.Id == houseId && h.CharacterId == characterId);

        if (house is null)
        {
            _logger.LogWarning("Player {PlayerGuid} tried to place fixture in non-owned house {HouseId}.", connection.Player.Guid, houseId);
            return true;
        }

        if (house.Fixtures.Count >= house.MaxFixtureCount)
        {
            _logger.LogWarning("Player {PlayerGuid} tried to exceed fixture limit in house {HouseId}.", connection.Player.Guid, house.Id);
            return true;
        }

        var sourceItem = FindOwnedFixtureItem(dbContext, characterId, packet);
        if (sourceItem is null)
        {
            _logger.LogWarning(
                "Player {PlayerGuid} tried to place unowned fixture item {ItemDefinitionId}. ItemRecordId={ItemRecordId}",
                connection.Player.Guid,
                packet.ItemDefinitionId,
                packet.ItemRecordId);
            return true;
        }

        var tintId = HouseOwnershipService.ResolveItemTintId(
            _resourceManager,
            packet.ItemDefinitionId,
            sourceItem.Tint);

        if (packet.IsSelectionOnly)
        {
            foreach (var stalePlacement in HousingPlacementSession.TakeAll(connection.Player.Guid, house.Id))
            {
                connection.SendTunneled(new HousingPacketRemoveFixture
                {
                    FixtureGuid = stalePlacement.FixtureGuid
                });
                HousingFixtureActorService.Remove(connection.Player, house.Id, stalePlacement.FixtureGuid);
            }

            var placement = HousingPlacementSession.Start(
                connection.Player.Guid,
                house.Id,
                packet.ItemDefinitionId,
                sourceItem.Id,
                tintId);

            if (!HouseOwnershipService.SendFixtureAsset(
                connection,
                packet.ItemDefinitionId,
                tintId,
                _resourceManager,
                isPreview: true))
            {
                HousingPlacementSession.TryTake(connection.Player.Guid, house.Id, placement.FixtureGuid, out _);
                _logger.LogWarning(
                    "Could not build fixture preview packets for item {ItemDefinitionId} requested by player {PlayerGuid}.",
                    packet.ItemDefinitionId,
                    connection.Player.Guid);
                return true;
            }

            _logger.LogInformation(
                "Player {PlayerGuid} sent preview asset for pending fixture {FixtureGuid} item {ItemDefinitionId} in house {HouseId} through the regular client tunnel.",
                connection.Player.Guid,
                placement.FixtureGuid,
                placement.ItemDefinitionId,
                house.Id);

            return true;
        }

        if (HousingPlacementSession.TryGet(
                connection.Player.Guid,
                house.Id,
                packet.ItemDefinitionId,
                out var pendingPlacement) &&
            HousingPlacementSession.TryTake(
                connection.Player.Guid,
                house.Id,
                pendingPlacement.FixtureGuid,
                out pendingPlacement))
        {
            return HousingFixturePlacementCommitService.TryCommit(
                connection,
                dbContext,
                house,
                pendingPlacement,
                packet.Position,
                packet.Rotation,
                packet.Scale,
                _resourceManager,
                _logger);
        }

        var normalizedScale = packet.Scale <= 0 ? 1.0f : packet.Scale;
        var persistedRotation = HousingFixtureActorService.ToHousingRotation(packet.Rotation);

        var dbFixture = new DbHouseFixture
        {
            HouseId = house.Id,
            ItemDefinitionId = packet.ItemDefinitionId,
            PositionX = packet.Position.X,
            PositionY = packet.Position.Y,
            PositionZ = packet.Position.Z,
            PositionW = packet.Position.W,
            RotationX = persistedRotation.X,
            RotationY = persistedRotation.Y,
            RotationZ = persistedRotation.Z,
            RotationW = persistedRotation.W,
            Scale = normalizedScale,
            Created = DateTimeOffset.UtcNow
        };
        HouseOwnershipService.SetFixtureTintId(dbFixture, tintId);

        var sourceItemId = sourceItem.Id;
        var sourceItemCount = sourceItem.Count - 1;

        if (sourceItemCount <= 0)
        {
            dbContext.Items.Remove(sourceItem);
        }
        else
        {
            sourceItem.Count = sourceItemCount;
        }

        dbContext.HouseFixtures.Add(dbFixture);
        dbContext.SaveChanges();

        UpdateConsumedInventoryItem(connection, sourceItemId, sourceItemCount);

        HousingFixtureActorService.PublishFixture(
            connection.Player,
            house,
            (ulong)dbFixture.Id,
            dbFixture,
            tintId,
            _resourceManager);

        var refreshedHouse = dbContext.Houses
            .Include(h => h.Fixtures)
            .First(h => h.Id == house.Id);
        if (sourceItemCount <= 0)
            HouseOwnershipService.SendFixtureItemList(connection, refreshedHouse, _resourceManager);
        HouseOwnershipService.SendHouseInfoUpdate(connection, refreshedHouse, inEditMode: true);

        _logger.LogInformation(
            "Player {PlayerGuid} directly placed fixture {FixtureId} item {ItemDefinitionId} in house {HouseId} at {Position} with rotation {Rotation} and scale {Scale}.",
            connection.Player.Guid,
            dbFixture.Id,
            dbFixture.ItemDefinitionId,
            house.Id,
            packet.Position,
            persistedRotation,
            normalizedScale);

        return true;
    }

    private static bool TryParsePlaceRequest(Player player, ReadOnlySpan<byte> data, out ParsedPlaceFixtureRequest result)
    {
        result = default;

        var reader = new PacketReader(data);
        if (!reader.TryRead(out short opCode) || opCode != BaseHousingPacket.OpCode)
            return false;

        if (!reader.TryRead(out short subOpCode) || subOpCode != ClientHousingPacketPlaceFixtureRequest.OpCode)
            return false;

        var candidates = new List<ParsedPlaceFixtureRequest>();
        var payload = reader.RemainingSpan;

        TryAddCompactInventoryRequest(player, candidates, payload);
        TryAddItemDefinitionTransform(candidates, payload);
        TryAddItemDefinitionIntRecordTransform(candidates, payload);
        TryAddItemDefinitionLongRecordTransform(candidates, payload);
        TryAddIntRecordItemDefinitionTransform(candidates, payload);
        TryAddLongRecordItemDefinitionTransform(candidates, payload);

        var bestScore = 0;
        foreach (var candidate in candidates)
        {
            var score = ScoreCandidate(player, candidate);
            if (score <= bestScore)
                continue;

            bestScore = score;
            result = candidate;
        }

        return bestScore > 0;
    }

    private static void TryAddCompactInventoryRequest(Player player, List<ParsedPlaceFixtureRequest> candidates, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);

        if (!reader.TryRead(out int itemRecordId))
            return;

        if (!reader.TryRead(out ulong _))
            return;

        if (!reader.TryRead(out bool _))
            return;

        if (reader.RemainingLength != 0)
            return;

        var item = player.Items.FirstOrDefault(item => item.Id == itemRecordId && item.Count > 0);

        if (item is null)
            return;

        candidates.Add(new ParsedPlaceFixtureRequest(
            item.Definition,
            item.Id,
            player.Position,
            Quaternion.Identity,
            1.0f,
            true));
    }

    private static void TryAddItemDefinitionTransform(List<ParsedPlaceFixtureRequest> candidates, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out int itemDefinitionId))
            return;

        if (TryReadTransform(ref reader, itemDefinitionId, null, out var candidate))
            candidates.Add(candidate);
    }

    private static void TryAddItemDefinitionIntRecordTransform(List<ParsedPlaceFixtureRequest> candidates, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out int itemDefinitionId))
            return;

        if (!reader.TryRead(out int itemRecordId))
            return;

        if (TryReadTransform(ref reader, itemDefinitionId, itemRecordId > 0 ? itemRecordId : null, out var candidate))
            candidates.Add(candidate);
    }

    private static void TryAddItemDefinitionLongRecordTransform(List<ParsedPlaceFixtureRequest> candidates, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out int itemDefinitionId))
            return;

        if (!reader.TryRead(out ulong itemRecordId))
            return;

        if (TryReadTransform(ref reader, itemDefinitionId, ToItemRecordId(itemRecordId), out var candidate))
            candidates.Add(candidate);
    }

    private static void TryAddIntRecordItemDefinitionTransform(List<ParsedPlaceFixtureRequest> candidates, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out int itemRecordId))
            return;

        if (!reader.TryRead(out int itemDefinitionId))
            return;

        if (TryReadTransform(ref reader, itemDefinitionId, itemRecordId > 0 ? itemRecordId : null, out var candidate))
            candidates.Add(candidate);
    }

    private static void TryAddLongRecordItemDefinitionTransform(List<ParsedPlaceFixtureRequest> candidates, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryRead(out ulong itemRecordId))
            return;

        if (!reader.TryRead(out int itemDefinitionId))
            return;

        if (TryReadTransform(ref reader, itemDefinitionId, ToItemRecordId(itemRecordId), out var candidate))
            candidates.Add(candidate);
    }

    private static bool TryReadTransform(
        ref PacketReader reader,
        int itemDefinitionId,
        int? itemRecordId,
        out ParsedPlaceFixtureRequest candidate)
    {
        candidate = default;

        if (!reader.TryRead(out Vector4 position))
            return false;

        if (!reader.TryRead(out Quaternion rotation))
            return false;

        if (!reader.TryRead(out float scale))
            return false;

        if (reader.RemainingLength != 0)
            return false;

        candidate = new ParsedPlaceFixtureRequest(itemDefinitionId, itemRecordId, position, rotation, scale, false);
        return IsReasonableTransform(candidate);
    }

    private static int ScoreCandidate(Player player, ParsedPlaceFixtureRequest candidate)
    {
        if (!_resourceManager.ClientItemDefinitions.TryGetValue(candidate.ItemDefinitionId, out var definition))
            return 0;

        if (!HouseOwnershipService.IsFixtureInventoryItem(definition))
            return 0;

        if (candidate.ItemRecordId is { } itemRecordId &&
            player.Items.Any(item => item.Id == itemRecordId && item.Definition == candidate.ItemDefinitionId && item.Count > 0))
        {
            return 3;
        }

        return player.Items.Any(item => item.Definition == candidate.ItemDefinitionId && item.Count > 0) ? 2 : 0;
    }

    private static bool IsReasonableTransform(ParsedPlaceFixtureRequest candidate)
    {
        return IsFinite(candidate.Position.X) &&
            IsFinite(candidate.Position.Y) &&
            IsFinite(candidate.Position.Z) &&
            IsFinite(candidate.Position.W) &&
            IsFinite(candidate.Rotation.X) &&
            IsFinite(candidate.Rotation.Y) &&
            IsFinite(candidate.Rotation.Z) &&
            IsFinite(candidate.Rotation.W) &&
            IsFinite(candidate.Scale) &&
            Math.Abs(candidate.Position.X) < 100000f &&
            Math.Abs(candidate.Position.Y) < 100000f &&
            Math.Abs(candidate.Position.Z) < 100000f &&
            Math.Abs(candidate.Position.W) < 100000f &&
            Math.Abs(candidate.Rotation.X) <= 4f &&
            Math.Abs(candidate.Rotation.Y) <= 4f &&
            Math.Abs(candidate.Rotation.Z) <= 4f &&
            Math.Abs(candidate.Rotation.W) <= 4f &&
            candidate.Scale >= 0f &&
            candidate.Scale <= 100f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static int? ToItemRecordId(ulong value)
    {
        return value is > 0 and <= int.MaxValue ? (int)value : null;
    }

    private static DbItem? FindOwnedFixtureItem(DatabaseContext dbContext, ulong characterId, ParsedPlaceFixtureRequest packet)
    {
        var items = dbContext.Items
            .Where(i => i.CharacterId == characterId &&
                i.Definition == packet.ItemDefinitionId &&
                i.Count > 0);

        if (packet.ItemRecordId is { } itemRecordId)
        {
            var exactItem = items.FirstOrDefault(i => i.Id == itemRecordId);
            if (exactItem is not null)
                return exactItem;
        }

        return items
            .OrderBy(i => i.Id)
            .FirstOrDefault();
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
