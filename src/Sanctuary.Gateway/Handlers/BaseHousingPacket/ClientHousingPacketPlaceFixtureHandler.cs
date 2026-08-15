using System;
using System.Linq;
using System.Numerics;

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
public static class ClientHousingPacketPlaceFixtureHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketPlaceFixtureHandler));
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketPlaceFixture.TryDeserialize(data, out var packet))
        {
            _logger.LogError(
                "Failed to deserialize {Packet}. Length={Length} Data={Data}",
                nameof(ClientHousingPacketPlaceFixture),
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouse(connection, out var houseId))
            return true;

        if (!HousingPlacementSession.TryGet(
            connection.Player.Guid,
            houseId,
            packet.ItemDefinitionId,
            out var placement))
        {
            _logger.LogWarning(
                "Ignored fixture placement data from player {PlayerGuid} for item {ItemDefinitionId} because no pending cursor exists in house {HouseId}. Position={Position} Rotation={Rotation} Scale={Scale}.",
                connection.Player.Guid,
                packet.ItemDefinitionId,
                houseId,
                packet.Position,
                packet.Rotation,
                packet.Scale);
            return true;
        }

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var house = dbContext.Houses
            .Include(h => h.Fixtures)
            .FirstOrDefault(h => h.Id == houseId && h.CharacterId == characterId);

        if (house is null)
            return true;

        var rotation = ResolvePlacementRotation(packet.Rotation);
        var position = ResolvePlacementPosition(packet.Position, connection.Player.Position, rotation);

        if (HousingPlacementSession.TryActivateHover(
            connection.Player.Guid,
            house.Id,
            placement.FixtureGuid,
            out placement))
        {
            // The first placement packet is automatic. Keep the fixture detached
            // from an NPC so the native housing cursor owns and moves its preview.
            if (!HouseOwnershipService.SendFixtureUpdate(
                connection,
                house,
                placement.FixtureGuid,
                npcGuid: 0,
                placement.ItemDefinitionId,
                placement.ItemRecordId,
                placement.TintId,
                position,
                rotation,
                packet.Scale,
                _resourceManager,
                isPreview: false,
                includeAsset: true))
            {
                HousingPlacementSession.TryTake(
                    connection.Player.Guid,
                    house.Id,
                    placement.FixtureGuid,
                    out _);
                _logger.LogWarning(
                    "Could not arm cursor placement for pending fixture {FixtureGuid} item {ItemDefinitionId} for player {PlayerGuid}.",
                    placement.FixtureGuid,
                    placement.ItemDefinitionId,
                    connection.Player.Guid);
                return true;
            }

            _logger.LogInformation(
                "Player {PlayerGuid} armed native cursor preview for pending fixture {FixtureGuid} item {ItemDefinitionId} in house {HouseId} at {Position} with rotation {Rotation} and scale {Scale}.",
                connection.Player.Guid,
                placement.FixtureGuid,
                placement.ItemDefinitionId,
                house.Id,
                position,
                rotation,
                packet.Scale);
            return true;
        }

        if (!placement.HoverActive)
            return true;

        if (IsEmptyPlacement(packet.Position))
            return true;

        if (!HousingPlacementSession.TryTake(
                connection.Player.Guid,
                house.Id,
                placement.FixtureGuid,
                out placement))
        {
            return true;
        }

        HousingFixturePlacementCommitService.TryCommit(
            connection,
            dbContext,
            house,
            placement,
            position,
            rotation,
            packet.Scale,
            _resourceManager,
            _logger);

        return true;
    }

    private static bool IsEmptyPlacement(Vector4 position)
    {
        return MathF.Abs(position.X) <= 0.001f &&
            MathF.Abs(position.Y) <= 0.001f &&
            MathF.Abs(position.Z) <= 0.001f;
    }

    private static Vector4 ResolvePlacementPosition(Vector4 requested, Vector4 playerPosition, Quaternion rotation)
    {
        if (MathF.Abs(requested.X) > 0.001f ||
            MathF.Abs(requested.Y) > 0.001f ||
            MathF.Abs(requested.Z) > 0.001f)
        {
            return requested.W == 0
                ? new Vector4(requested.X, requested.Y, requested.Z, 1f)
                : requested;
        }

        var forward = new Vector3(
            MathF.Sin(rotation.X),
            0f,
            MathF.Cos(rotation.X)) * 2.5f;
        return new Vector4(
            playerPosition.X + forward.X,
            playerPosition.Y,
            playerPosition.Z + forward.Z,
            1f);
    }

    private static Quaternion ResolvePlacementRotation(Quaternion requested)
    {
        return HousingFixtureActorService.ToHousingRotation(requested);
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
