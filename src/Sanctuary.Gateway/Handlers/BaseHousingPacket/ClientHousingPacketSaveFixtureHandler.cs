using System;
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
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketSaveFixtureHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    private readonly record struct ParsedSaveFixtureRequest(
        ulong FixtureGuid,
        Vector4 Position,
        Quaternion Rotation,
        float Scale);

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketSaveFixtureHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!TryParseSaveFixture(data, out var packet))
        {
            _logger.LogError(
                "Failed to deserialize {packet}. Length={length} Data={data}",
                nameof(ClientHousingPacketSaveFixture),
                data.Length,
                Convert.ToHexString(data));
            return false;
        }

        if (!TryGetActiveHouse(connection, out var houseId))
            return true;

        using var dbContext = _dbContextFactory.CreateDbContext();
        var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);
        var fixtureGuid = HousingFixtureActorService.ResolveClientFixtureGuid(
            connection.Player.Guid,
            houseId,
            packet.FixtureGuid);
        var fixtureId = HousingFixtureActorService.ResolveDatabaseFixtureId(
            connection.Player.Guid,
            houseId,
            fixtureGuid);

        if (HousingPlacementSession.TryTake(connection.Player.Guid, houseId, fixtureGuid, out var placement))
        {
            var pendingHouse = dbContext.Houses
                .Include(candidate => candidate.Fixtures)
                .FirstOrDefault(candidate => candidate.Id == houseId && candidate.CharacterId == characterId);

            if (pendingHouse is null)
            {
                _logger.LogWarning("Player {PlayerGuid} tried to save a fixture in non-owned house {HouseId}.", connection.Player.Guid, houseId);
                return true;
            }

            return HousingFixturePlacementCommitService.TryCommit(
                connection,
                dbContext,
                pendingHouse,
                placement,
                packet.Position,
                packet.Rotation,
                packet.Scale,
                _resourceManager,
                _logger);
        }

        var dbFixture = dbContext.HouseFixtures
            .Include(candidate => candidate.House)
            .FirstOrDefault(candidate =>
                candidate.Id == fixtureId &&
                candidate.HouseId == houseId &&
                candidate.House.CharacterId == characterId);

        if (dbFixture is null)
        {
            _logger.LogWarning("Fixture {FixtureGuid} not found for player {PlayerGuid} in house {HouseId}.", packet.FixtureGuid, connection.Player.Guid, houseId);
            return true;
        }

        var house = dbFixture.House;

        var persistedRotation = HousingFixtureActorService.ToHousingRotation(packet.Rotation);

        dbFixture.PositionX = packet.Position.X;
        dbFixture.PositionY = packet.Position.Y;
        dbFixture.PositionZ = packet.Position.Z;
        dbFixture.PositionW = packet.Position.W;
        dbFixture.RotationX = persistedRotation.X;
        dbFixture.RotationY = persistedRotation.Y;
        dbFixture.RotationZ = persistedRotation.Z;
        dbFixture.RotationW = persistedRotation.W;
        dbFixture.Scale = packet.Scale <= 0 ? 1.0f : packet.Scale;

        dbContext.SaveChanges();

        var tintId = HouseOwnershipService.GetFixtureTintId(dbFixture);
        if (tintId == 0)
            tintId = HousingFixtureActorService.GetTintId(connection.Player, houseId, fixtureGuid);
        tintId = HouseOwnershipService.ResolveItemTintId(
            _resourceManager,
            dbFixture.ItemDefinitionId,
            tintId);
        HousingFixtureActorService.PublishSavedTransform(
            connection.Player,
            house,
            fixtureGuid,
            dbFixture,
            tintId,
            _resourceManager);

        _logger.LogInformation(
            "Player {PlayerGuid} saved fixture {FixtureId} selected as {RequestedGuid}/{FixtureGuid} in house {HouseId}. Position={Position} Rotation={Rotation} Scale={Scale}.",
            connection.Player.Guid,
            dbFixture.Id,
            packet.FixtureGuid,
            fixtureGuid,
            houseId,
            packet.Position,
            persistedRotation,
            dbFixture.Scale);

        return true;
    }

    private static bool TryParseSaveFixture(ReadOnlySpan<byte> data, out ParsedSaveFixtureRequest result)
    {
        result = default;

        if (ClientHousingPacketSaveFixture.TryDeserialize(data, out var packet) &&
            IsReasonableTransform(packet.Position, packet.Rotation, packet.Scale))
        {
            result = new ParsedSaveFixtureRequest(
                packet.FixtureGuid,
                packet.Position,
                packet.Rotation,
                packet.Scale);
            return true;
        }

        var reader = new PacketReader(data);
        if (!reader.TryRead(out short opCode) || opCode != BaseHousingPacket.OpCode)
            return false;

        if (!reader.TryRead(out short subOpCode) || subOpCode != ClientHousingPacketSaveFixture.OpCode)
            return false;

        return TryReadSaveFixtureWithIntGuid(reader.RemainingSpan, out result);
    }

    private static bool TryReadSaveFixtureWithIntGuid(ReadOnlySpan<byte> payload, out ParsedSaveFixtureRequest result)
    {
        result = default;

        var reader = new PacketReader(payload);
        if (!reader.TryRead(out int fixtureGuid))
            return false;

        return TryReadTransform(ref reader, (ulong)fixtureGuid, out result);
    }

    private static bool TryReadTransform(ref PacketReader reader, ulong fixtureGuid, out ParsedSaveFixtureRequest result)
    {
        result = default;

        if (!reader.TryRead(out Vector4 position))
            return false;

        if (!reader.TryRead(out Quaternion rotation))
            return false;

        if (reader.RemainingLength != sizeof(float))
        {
            if (!reader.TryRead(out float _))
                return false;

            var customization = new CustomizationDetail();
            if (!customization.TryRead(ref reader))
                return false;
        }

        if (!reader.TryRead(out float scale))
            return false;

        if (reader.RemainingLength != 0)
            return false;

        if (!IsReasonableTransform(position, rotation, scale))
            return false;

        result = new ParsedSaveFixtureRequest(fixtureGuid, position, rotation, scale);
        return true;
    }

    private static bool IsReasonableTransform(Vector4 position, Quaternion rotation, float scale)
    {
        return IsFinite(position.X) &&
            IsFinite(position.Y) &&
            IsFinite(position.Z) &&
            IsFinite(position.W) &&
            IsFinite(rotation.X) &&
            IsFinite(rotation.Y) &&
            IsFinite(rotation.Z) &&
            IsFinite(rotation.W) &&
            IsFinite(scale) &&
            Math.Abs(position.X) < 100000f &&
            Math.Abs(position.Y) < 100000f &&
            Math.Abs(position.Z) < 100000f &&
            Math.Abs(position.W) < 100000f &&
            Math.Abs(rotation.X) <= 4f &&
            Math.Abs(rotation.Y) <= 4f &&
            Math.Abs(rotation.Z) <= 4f &&
            Math.Abs(rotation.W) <= 4f &&
            scale >= 0f &&
            scale <= 100f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
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
