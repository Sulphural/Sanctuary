using System;
using System.Linq;
using System.Numerics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class ClientHousingPacketEnterRequestHandler
{
    private static ILogger _logger = null!;
    private static IResourceManager _resourceManager = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(ClientHousingPacketEnterRequestHandler));

        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!ClientHousingPacketEnterRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(ClientHousingPacketEnterRequest));
            return false;
        }

        _logger.LogInformation("Player {name} requesting to enter house {guid}", 
            connection.Player.Name.FirstName, packet.HouseInstanceGuid);

        using var dbContext = _dbContextFactory.CreateDbContext();
        
        var playerId = GuidHelper.GetPlayerId(connection.Player.Guid);
        
        // Get or create player's house
        var dbHouse = dbContext.Houses
            .Include(h => h.Fixtures)
            .FirstOrDefault(h => h.OwnerId == playerId);

        if (dbHouse == null)
        {
            // Create default house for player
            dbHouse = CreateDefaultHouse(dbContext, playerId);
            _logger.LogInformation("Created new house for player {name}", connection.Player.Name.FirstName);
        }

        // Get house definition
        if (!_resourceManager.Houses.TryGetValue(dbHouse.HouseDefinitionId, out var houseDefinition))
        {
            _logger.LogError("Invalid house definition ID: {id}", dbHouse.HouseDefinitionId);
            return false;
        }

        // Store current house in player connection for furniture operations
        connection.Player.CurrentHouseGuid = dbHouse.Id;

        // Begin zone transition to house
        SendZoneTransition(connection, dbHouse, houseDefinition);

        // Send house instance data
        SendHouseData(connection, dbHouse, houseDefinition);

        // Send furniture data
        SendFurnitureData(connection, dbHouse);

        _logger.LogInformation("Player {name} entered house successfully", connection.Player.Name.FirstName);

        return true;
    }

    private static DbHouse CreateDefaultHouse(DatabaseContext dbContext, ulong playerId)
    {
        var house = new DbHouse
        {
            OwnerId = playerId,
            HouseDefinitionId = 1, // Default: Seaside Beach house
            NameId = 0,
            CustomName = null,
            IsLocked = false,
            IsMembersOnly = false,
            IsFloraAllowed = true,
            PetAutospawn = false,
            MaxFixtureCount = 200,
            MaxLandmarkCount = 0,
            IconId = 33439,
            Description = null,
            KeywordList = null,
            Rating = 0,
            Votes = 0,
            Created = DateTimeOffset.UtcNow,
            LastVisited = DateTimeOffset.UtcNow
        };

        dbContext.Houses.Add(house);
        dbContext.SaveChanges();

        return house;
    }

    private static void SendZoneTransition(GatewayConnection connection, DbHouse dbHouse, Game.Resources.Definitions.HouseDefinition houseDefinition)
    {
        // Get zone definition for this house
        string zoneName = "hsg_emptylot_seaside_beach_01"; // Default fallback
        string sky = "sky_seaside24.xml"; // Default sky
        int geometryId = houseDefinition.ZoneId; // Use ZoneId as geometry
        
        // Always use spawn position from Houses.json (more reliable than zone files)
        var spawnPosition = new Vector4(
            houseDefinition.SpawnPosition.X,
            houseDefinition.SpawnPosition.Y + 10f, // Add 10 units height to prevent falling
            houseDefinition.SpawnPosition.Z,
            houseDefinition.SpawnPosition.W
        );
        
        var spawnRotation = houseDefinition.SpawnRotation;

        if (_resourceManager.Zones.TryGetValue(houseDefinition.ZoneId, out var zoneDef))
        {
            zoneName = zoneDef.Name;
            
            _logger.LogInformation("Using house spawn position: ({X}, {Y}, {Z})", 
                spawnPosition.X, spawnPosition.Y, spawnPosition.Z);

            _logger.LogInformation("Using zone {ZoneName} (ID: {ZoneId}) for house def {HouseDefId}",
                zoneName, houseDefinition.ZoneId, houseDefinition.Id);
        }
        else
        {
            _logger.LogWarning("Zone {ZoneId} not found for house def {HouseDefId}, using default zone",
                houseDefinition.ZoneId, houseDefinition.Id);
        }

        // Send zone transition packet
        var packetClientBeginZoning = new PacketClientBeginZoning
        {
            Name = zoneName,
            Type = 2, // Housing instance
            Position = spawnPosition,
            Rotation = new Quaternion(spawnRotation.X, spawnRotation.Y, spawnRotation.Z, spawnRotation.W),
            Sky = sky,
            Unknown = 1,
            Id = houseDefinition.ZoneId, // CRITICAL: Use zone ID, not house instance ID!
            GeometryId = houseDefinition.ZoneId, // Match the zone ID for geometry
            OverrideUpdateRadius = true
        };

        connection.SendTunneled(packetClientBeginZoning);

        _logger.LogInformation("Sending zone transition to {ZoneName} (ZoneId={ZoneId}) for house {HouseId}", 
            zoneName, houseDefinition.ZoneId, dbHouse.Id);
    }

    private static void SendHouseData(GatewayConnection connection, DbHouse dbHouse, Game.Resources.Definitions.HouseDefinition houseDefinition)
    {
        var instanceData = new PlayerHousingInstanceData
        {
            HouseGuid = dbHouse.Id,
            OwnerGuid = connection.Player.Guid,
            OwnerName = connection.Player.Name.FirstName,
            NameId = dbHouse.NameId,
            Name = dbHouse.CustomName,
            IsLocked = dbHouse.IsLocked,
            PetAutospawn = dbHouse.PetAutospawn,
            MaxFixtureCount = dbHouse.MaxFixtureCount,
            MaxLandmarkCount = dbHouse.MaxLandmarkCount,
            CurFixtureCount = dbHouse.Fixtures.Count,
            IconId = dbHouse.IconId,
            IsMembersOnly = dbHouse.IsMembersOnly,
            BuildAreas = houseDefinition.BuildAreas
        };

        var packet = new HousingPacketInstanceData
        {
            InstanceData = instanceData
        };

        connection.SendTunneled(packet);
    }

    private static void SendFurnitureData(GatewayConnection connection, DbHouse dbHouse)
    {
        var fixtureItemList = new HousingPacketFixtureItemList();

        uint fixtureKey = 1;
        foreach (var dbFixture in dbHouse.Fixtures)
        {
            // Add fixture instance info
            fixtureItemList.Infos.Add(new FixtureInstanceInfo
            {
                FixtureGuid = dbFixture.Id,
                ItemDefinitionId = dbFixture.ItemDefinitionId,
                Unknown3 = 0,
                Unknown4 = 0,
                Unknown5 = 0
            });

            // Add fixture definition (properties)
            fixtureItemList.Definitions.Add(new FixtureDefinition
            {
                Id = (int)fixtureKey++,
                ItemDefinitionId = dbFixture.ItemDefinitionId,
                Unknown3 = 0,
                Unknown4 = 0,
                Unknown5 = false,
                Unknown6 = false,
                Unknown7 = true,
                Unknown8 = false,
                Unknown9 = false,
                Unknown10 = false,
                CompositeEffectId = 0,
                Unknown14 = 1.0f,
                Unknown15 = 1.0f,
                Unknown16 = false,
                Unknown17 = false,
                Unknown18 = false,
                Unknown19 = false
            });
        }

        connection.SendTunneled(fixtureItemList);

        _logger.LogInformation("Sent {count} furniture fixtures to player", dbHouse.Fixtures.Count);
    }
}