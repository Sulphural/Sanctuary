using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sanctuary.Core.Helpers;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Gateway.Admin;

public static class HouseOwnershipService
{
    private const int DefaultHouseDefinitionId = 1;
    private const int DefaultMaxFixtureCount = 2000;
    private const int HousingBuildingCategoryId = 147;
    public readonly record struct HouseZoningInfo(
        int HouseDefinitionId,
        int RequestedZoneId,
        int PacketZoneId,
        string ZoneName,
        Vector4 Position);

    private readonly record struct HouseTerrainInfo(int PacketZoneId, int GeometryId, string ZoneName, string Sky);
    private readonly record struct HouseDisplayInfo(string Name, int IconId);
    private sealed record HouseSurfaceCustomization(
        string FixtureGroup,
        string FixtureType,
        int ItemDefinitionId,
        int TintId);
    private sealed record FixtureAppearanceData(int TintId);

    private static readonly Dictionary<int, int> HouseDefinitionIdsByItemId = new()
    {
        [2213] = 27, // Small Wilds House
        [2214] = 26, // Large Wilds House
        [2215] = 33, // Briarwood Farm
        [7485] = 5, // Briarwood Lot
        [7486] = 19, // Snowhill Lot
        [10337] = 24, // Apartment
        [10338] = 8, // Shrouded Stairs Lot
        [10384] = 14, // Seaside Lot
        [10385] = 35, // Sandy Beach Lot
        [10386] = 10, // Lonely Island Lot
        [10999] = 21, // Wugachug Lot
        [11110] = 22, // Blackspore Swamp House
        [11140] = 49, // Club House
        [11435] = 34, // Wilds Farm
        [12117] = 55, // Snowhill Lodge
        [15955] = 30, // Rumbledome Lot
        [17182] = 9, // Shrouded Glade Lot
        [17183] = 17, // Shrouded Gloam Lot
        [17184] = 25, // Wilds Condo
        [17185] = 21, // Wugachug Lot
        [17186] = 68, // Merry Vale Lot
        [17187] = 69, // Sanctuary Lot
        [17188] = 35, // Sandy Beach Lot
        [17189] = 6, // Briarwall Towers Lot
        [17190] = 88, // Wilds Rapids Lot
        [17191] = 16, // Crystal Mines Lot
        [17192] = 3, // Bog Shore Lot
        [17193] = 4, // Briar Falls Lot
        [17194] = 20, // Vale Stream Lot
        [17195] = 15, // Bat Cavern Lot
        [17196] = 98, // Lake Tree Lot
        [17567] = 1, // Blackspore Swamp Lot
        [79149] = 2 // Sunset Party Boat
    };

    private static readonly Dictionary<int, int> CanonicalHouseDefinitionIds = new()
    {
        [28] = 49, // Old Club House entry
        [29] = 55 // Old Snowhill Lodge entry
    };

    private static readonly Dictionary<int, HouseDisplayInfo> HouseDisplaysByNameId = new()
    {
        [296] = new("Wilds Condo", 38460),
        [5184] = new("Briarwood Lot", 31260),
        [5185] = new("Snowhill Lot", 31266),
        [5409] = new("Wilds Farm", 35450),
        [6289] = new("Small Wilds House", 27232),
        [6290] = new("Large Wilds House", 27229),
        [6514] = new("Blackspore Swamp Lot", 33439),
        [6775] = new("Club House", 34759),
        [8320] = new("Seaside Lot", 32638),
        [8321] = new("Sandy Beach Lot", 33203),
        [8322] = new("Lonely Island Lot", 33121),
        [17432] = new("Shrouded Gloam Lot", 39401),
        [26345] = new("Snowhill Lodge", 38416),
        [420151] = new("Apartment", 27226),
        [435461] = new("Briarwood Farm", 36591),
        [442333] = new("Rumbledome Lot", 44470),
        [5103265] = new("Sunset Party Boat", 47735),
        [69964] = new("Shrouded Glade Lot", 33445),
        [69965] = new("Shrouded Stairs Lot", 33445),
        [69966] = new("Blackspore Swamp House", 33722),
        [69967] = new("Wugachug Lot", 33454),
        [69968] = new("Merry Vale Lot", 33442),
        [69969] = new("Sanctuary Lot", 27232),
        [69971] = new("Briarwall Towers Lot", 37055),
        [69972] = new("Wilds Rapids Lot", 27232),
        [69973] = new("Crystal Mines Lot", 33599),
        [69974] = new("Bog Shore Lot", 33593),
        [69975] = new("Briar Falls Lot", 33596),
        [69976] = new("Vale Stream Lot", 33617),
        [69977] = new("Bat Cavern Lot", 27232),
        [69978] = new("Lake Tree Lot", 34571)
    };

    // HousingBrowser appends .dds and _thumb.dds to these basenames.
    private static readonly Dictionary<int, string> HouseDirectorySnapshotsByDefinitionId = new()
    {
        [1] = "blackspore_lot",
        [2] = "yacht_lot",
        [3] = "bogshore_lot",
        [4] = "briarfalls_lot",
        [5] = "briarwood_lot",
        [6] = "briarwoodtowers_lot",
        [8] = "shroudedgladestairs_lot",
        [9] = "shroudedglade_lot",
        [10] = "lonelyisland_lot",
        [14] = "seaside_lot",
        [15] = "snowhillbatcave_lot",
        [16] = "crystalmines_lot",
        [17] = "shroudedgloam_lot",
        [18] = "dwarfdam_lot",
        [19] = "snowhill_lot",
        [20] = "valestream_lot",
        [21] = "wugachug_lot",
        [22] = "blacksporeswamphouse_lot",
        [23] = "sunstonevalley_lot",
        [24] = "apartment_home",
        [25] = "condo_lot",
        [26] = "largewilds_home",
        [27] = "smallwilds_home",
        [28] = "vipclub_lot",
        [29] = "snowhilllodge_lot",
        // These restored definitions use the closest native directory art that
        // actually exists in Assets_manifest. HousingBrowser appends .dds and
        // _thumb.dds, so invented basenames always fall back to undefined.dds.
        [30] = "crystalmines_lot",
        [33] = "briarwood_lot",
        [34] = "wildrapids_lot",
        [35] = "sandybeach_lot",
        [49] = "vipclub_lot",
        [55] = "snowhilllodge_lot",
        [68] = "merryvale_lot",
        [69] = "merryvale_lot",
        [88] = "wildrapids_lot",
        [98] = "laketree_lot"
    };

    private static readonly Dictionary<string, int> HouseDefinitionIdsByDisplayName = HouseDisplaysByNameId
        .SelectMany(pair => HouseDefinitionIdsByNameId(pair.Key).Select(definitionId => new KeyValuePair<string, int>(pair.Value.Name, definitionId)))
        .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

    public static bool IsHouseItem(ClientItemDefinition itemDefinition)
    {
        return itemDefinition.Type == 16;
    }

    public static bool IsFixtureInventoryItem(ClientItemDefinition itemDefinition)
    {
        if (IsHouseItem(itemDefinition))
            return false;

        if (HousingPlacementCatalog.IsFixtureCustomization(itemDefinition))
            return true;

        if (itemDefinition.Type == 29)
            return true;

        if (itemDefinition.Type == 1 &&
            itemDefinition.CategoryId is 52 or 53 or 54 or 56 or 57 or 147 &&
            HousingPlacementCatalog.IsFixture(itemDefinition.Id))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(itemDefinition.ModelName) &&
            itemDefinition.ModelName.StartsWith("hsg_", StringComparison.OrdinalIgnoreCase) &&
            itemDefinition.Type == 1)
            return true;

        // These catalog fixtures remain placeable as ordinary decorations.
        if (!string.IsNullOrWhiteSpace(itemDefinition.ModelName) &&
            itemDefinition.ModelName.StartsWith("mkt_boombox", StringComparison.OrdinalIgnoreCase) &&
            itemDefinition.Type == 1)
            return true;

        return false;
    }

    public static DbHouse GetOrCreateDefaultHouse(DatabaseContext dbContext, Player player, IResourceManager resourceManager)
    {
        var characterId = GuidHelper.GetPlayerId(player.Guid);
        var house = dbContext.Houses
            .Where(h => h.CharacterId == characterId)
            .OrderBy(h => h.Id)
            .FirstOrDefault();

        if (house is not null)
            return house;

        var definition = resourceManager.Houses.TryGetValue(DefaultHouseDefinitionId, out var defaultDefinition)
            ? defaultDefinition
            : resourceManager.Houses.Values.OrderBy(h => h.Id).First();

        return CreateHouse(dbContext, characterId, definition);
    }

    public static DbHouse? TryGetHouse(DatabaseContext dbContext, ulong houseInstanceGuid)
    {
        if (houseInstanceGuid == 0)
            return null;

        ulong houseId;

        try
        {
            houseId = GuidHelper.GetHouseId(houseInstanceGuid);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (houseId > int.MaxValue)
            return null;

        var id = (int)houseId;

        return dbContext.Houses
            .Include(h => h.Character)
            .Include(h => h.Fixtures)
            .FirstOrDefault(h => h.Id == id);
    }

    public static DbHouse PurchaseHouse(
        DatabaseContext dbContext,
        DbCharacter character,
        IResourceManager resourceManager,
        ClientItemDefinition itemDefinition,
        out bool alreadyOwned)
    {
        var definition = ResolveHouseDefinition(resourceManager, itemDefinition);
        var house = dbContext.Houses
            .Where(h => h.CharacterId == character.Id)
            .ToList()
            .FirstOrDefault(h => ResolveHouseDefinition(resourceManager, h).Id == definition.Id);

        alreadyOwned = house is not null;

        return house ?? CreateHouse(dbContext, character.Id, definition, itemDefinition);
    }

    public static void SendHouseList(GatewayConnection connection, DatabaseContext dbContext, IResourceManager resourceManager)
    {
        var houses = GetPlayerHouses(dbContext, connection.Player, resourceManager);

        var packet = new HousingPacketInstanceList
        {
            PlayerGuid = connection.Player.Guid,
            Instances = houses
                .Select(h => ToInstanceInfo(h, connection.Player, resourceManager))
                .ToList()
        };

        connection.SendTunneled(packet);
    }

    public static void SendHouseData(GatewayConnection connection, DbHouse house, IResourceManager resourceManager)
    {
        HousingFixtureActorService.PrepareHouse(connection, house, resourceManager);

        var (ownerGuid, ownerName) = ResolveOwnerIdentity(house, connection.Player);
        var instanceInfo = ToInstanceInfo(
            house,
            ownerGuid,
            ownerName,
            resourceManager,
            connection.Player.Guid,
            includeFactoryPlotId: false);
        var zoneInstanceInfo = ToInstanceInfo(
            house,
            ownerGuid,
            ownerName,
            resourceManager,
            connection.Player.Guid,
            includeFactoryPlotId: true);
        var instanceData = ToInstanceData(
            house,
            connection.Player,
            ownerGuid,
            ownerName,
            resourceManager);

        connection.SendTunneled(new HousingPacketInstanceList
        {
            PlayerGuid = ownerGuid,
            Instances = [instanceInfo]
        });

        connection.SendTunneled(new HousingPacketZoneData
        {
            IsPreview = false,
            HeadSize = 10,
            InstanceInfo = zoneInstanceInfo
        });

        connection.SendTunneled(new HousingPacketInstanceData
        {
            InstanceData = instanceData
        });

        SendFixtureItemList(connection, house, resourceManager);
        SendPersistedFixtureAssets(connection, house, resourceManager);
        // The player is hidden while zoning. Fixture actors are introduced and
        // transformed by the post-ready replay, so sending every transform here
        // only adds a large redundant burst to a client that is still loading.
        if (connection.Player.Visible)
            HousingFixtureActorService.SendPersistedFixtureTransforms(connection.Player, house);
        SendHouseCustomizations(connection.Player, house, resourceManager);
        HousingFixtureActorService.ReplayHouseRuntime(connection.Player, house.Id);

        connection.SendTunneled(new HousingPacketUpdateHouseInfo
        {
            InEditMode = false,
            IsLocked = instanceData.IsLocked,
            IsFloraAllowed = instanceData.IsFloraAllowed,
            PetAutospawn = house.PetAutospawn,
            CurFixtureCount = instanceData.CurFixtureCount,
            CurLandmarkCount = instanceData.CurLandmarkCount,
            FurnitureScore = instanceData.FurnitureScore
        });
    }

    public static void SendFixtureItemList(GatewayConnection connection, DbHouse house, IResourceManager resourceManager)
    {
        connection.SendTunneled(ToFixtureItemList(house, connection.Player, resourceManager));
    }

    public static void SendHouseGrantData(GatewayConnection connection, DbHouse house, IResourceManager resourceManager, bool inEditMode)
    {
        HousingFixtureActorService.EnsurePersistedActors(connection, house, resourceManager);

        var instanceData = ToInstanceData(
            house,
            connection.Player,
            connection.Player.Guid,
            connection.Player.Name.FullName,
            resourceManager);

        connection.SendTunneled(new HousingPacketInstanceData
        {
            InstanceData = instanceData
        });

        SendFixtureItemList(connection, house, resourceManager);
        SendPersistedFixtureAssets(connection, house, resourceManager);
        HousingFixtureActorService.SendPersistedFixtureTransforms(connection.Player, house);
        SendHouseCustomizations(connection.Player, house, resourceManager);
        HousingFixtureActorService.ReplayHouseRuntime(connection.Player, house.Id);
        SendHouseInfoUpdate(connection, house, inEditMode);
    }

    public static void SendHouseInfoUpdate(GatewayConnection connection, DbHouse house, bool inEditMode)
    {
        connection.SendTunneled(new HousingPacketUpdateHouseInfo
        {
            InEditMode = inEditMode,
            IsLocked = house.IsLocked,
            IsFloraAllowed = house.IsFloraAllowed,
            PetAutospawn = house.PetAutospawn,
            CurFixtureCount = house.Fixtures.Count,
            CurLandmarkCount = 0,
            FurnitureScore = house.FurnitureScore
        });
    }

    public static bool SendFixtureUpdate(
        GatewayConnection connection,
        DbHouse house,
        ulong fixtureGuid,
        ulong npcGuid,
        int itemDefinitionId,
        int itemRecordId,
        int tintId,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager,
        bool isPreview,
        bool includeAsset = true)
    {
        return SendFixtureUpdate(
            connection.Player,
            house,
            fixtureGuid,
            npcGuid,
            itemDefinitionId,
            itemRecordId,
            tintId,
            position,
            rotation,
            scale,
            resourceManager,
            isPreview,
            includeAsset);
    }

    public static bool SendFixtureUpdate(
        Player recipient,
        DbHouse house,
        ulong fixtureGuid,
        ulong npcGuid,
        int itemDefinitionId,
        int itemRecordId,
        int tintId,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager,
        bool isPreview,
        bool includeAsset = true)
    {
        var definition = BuildFixtureDefinition(resourceManager, itemDefinitionId);
        if (definition is null ||
            !resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
        {
            return false;
        }

        tintId = ResolveItemTintId(resourceManager, itemDefinitionId, tintId);

        var textureAlias = itemDefinition.TextureAlias ?? string.Empty;
        var tintAlias = itemDefinition.TintAlias ?? string.Empty;
        var normalizedScale = scale <= 0 ? 1.0f : scale;
        var housingRotation = HousingFixtureActorService.ToHousingRotation(rotation);
        var databaseFixtureId = HousingFixtureActorService.ResolveDatabaseFixtureId(
            recipient.Guid,
            house.Id,
            fixtureGuid);
        var fixtureRuntimeKey = GetFixtureRuntimeKey(fixtureGuid, databaseFixtureId);
        var instance = new FixtureInstance
        {
            Guid = fixtureGuid,
            HouseGuid = GetHouseGuid(house),
            Id = itemDefinitionId,
            Unknown4 = 0,
            Unknown5 = position,
            Unknown6 = housingRotation,
            Unknown7 = Quaternion.Identity,
            // Native housing selection resolves the scene actor through this
            // link before creating its active move/rotate object. Leaving the
            // field at zero makes every editor command operate on no target.
            Unknown8 = unchecked((long)npcGuid),
            Unknown9 = tintId,
            Unknown10 = 0,
            CustomizationDetails = new CustomizationDetail
            {
                Type = 1,
                TextureAlias = textureAlias,
                TintAlias = tintAlias,
                TintId = tintId,
                TextureOverride = string.Empty
            },
            Unknown11 = string.Empty,
            Unknown12 = string.Empty,
            Unknown13 = 0,
            Unknown14 = string.Empty,
            Unknown15 = normalizedScale,
            Unknown16 = false,
            Unknown17 = 0
        };

        var info = new FixtureInstanceInfo
        {
            FixtureGuid = fixtureGuid,
            ItemDefinitionId = itemDefinitionId,
            CouplingDisplay =
            {
                Id = itemRecordId,
                CompositeEffect = itemDefinition.CompositeEffectId,
                EffectType = 0
            }
        };

        recipient.SendTunneled(new HousingPacketFixtureUpdate
        {
            Instance = instance,
            Info = info,
            Definition = definition,
            Unknown1 = unchecked((int)fixtureRuntimeKey)
        });

        if (includeAsset)
        {
            recipient.SendTunneled(new HousingPacketFixtureAsset
            {
                ModelDefinitionId = definition.ModelId,
                ItemDefinitionId = itemDefinitionId,
                Definition = definition,
                TextureAlias = textureAlias,
                TintAlias = tintAlias,
                TintId = tintId,
                PreviewTintId = tintId,
                TextureOverride = string.Empty,
                IsPreview = isPreview
            });
        }

        return true;
    }

    internal static uint GetFixtureRuntimeKey(ulong fixtureGuid, int databaseFixtureId)
    {
        if (databaseFixtureId > 0)
            return (uint)databaseFixtureId;

        var runtimeKey = unchecked((uint)fixtureGuid);
        if (runtimeKey != 0)
            return runtimeKey;

        runtimeKey = unchecked((uint)(fixtureGuid >> 32));
        return runtimeKey != 0 ? runtimeKey : 1u;
    }

    public static bool SendFixtureAsset(
        GatewayConnection connection,
        int itemDefinitionId,
        int tintId,
        IResourceManager resourceManager,
        bool isPreview)
    {
        var definition = BuildFixtureDefinition(resourceManager, itemDefinitionId);
        if (definition is null ||
            !resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
        {
            return false;
        }

        tintId = ResolveItemTintId(resourceManager, itemDefinitionId, tintId);

        connection.SendTunneled(new HousingPacketFixtureAsset
        {
            ModelDefinitionId = definition.ModelId,
            ItemDefinitionId = itemDefinitionId,
            Definition = definition,
            TextureAlias = itemDefinition.TextureAlias ?? string.Empty,
            TintAlias = itemDefinition.TintAlias ?? string.Empty,
            TintId = tintId,
            PreviewTintId = tintId,
            TextureOverride = string.Empty,
            IsPreview = isPreview
        });

        return true;
    }

    public static bool ApplyHouseCustomization(
        DbHouse house,
        string fixtureGroup,
        string fixtureType,
        int itemDefinitionId,
        int tintId)
    {
        fixtureGroup = NormalizeFixtureSelector(fixtureGroup);
        fixtureType = NormalizeFixtureSelector(fixtureType);
        if (fixtureGroup.Length == 0 || fixtureType.Length == 0)
            return false;

        var customizations = DeserializeHouseCustomizations(house.CustomizationData);
        customizations.RemoveAll(customization =>
            string.Equals(customization.FixtureGroup, fixtureGroup, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(customization.FixtureType, fixtureType, StringComparison.OrdinalIgnoreCase));
        customizations.Add(new HouseSurfaceCustomization(
            fixtureGroup,
            fixtureType,
            itemDefinitionId,
            tintId));
        house.CustomizationData = JsonSerializer.Serialize(customizations);
        return true;
    }

    public static bool RemoveHouseCustomization(
        DbHouse house,
        string fixtureGroup,
        string fixtureType)
    {
        fixtureGroup = NormalizeFixtureSelector(fixtureGroup);
        fixtureType = NormalizeFixtureSelector(fixtureType);
        if (fixtureGroup.Length == 0 || fixtureType.Length == 0)
            return false;

        var customizations = DeserializeHouseCustomizations(house.CustomizationData);
        var removed = customizations.RemoveAll(customization =>
            string.Equals(customization.FixtureGroup, fixtureGroup, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(customization.FixtureType, fixtureType, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            return false;

        house.CustomizationData = customizations.Count == 0
            ? null
            : JsonSerializer.Serialize(customizations);
        return true;
    }

    public static void SendHouseCustomizations(
        Player recipient,
        DbHouse house,
        IResourceManager resourceManager)
    {
        foreach (var customization in DeserializeHouseCustomizations(house.CustomizationData))
        {
            SendHouseCustomization(
                recipient,
                house,
                customization.FixtureGroup,
                customization.FixtureType,
                customization.ItemDefinitionId,
                customization.TintId,
                resourceManager);
        }
    }

    public static bool SendHouseCustomization(
        Player recipient,
        DbHouse house,
        string fixtureGroup,
        string fixtureType,
        int itemDefinitionId,
        int tintId,
        IResourceManager resourceManager)
    {
        var definition = BuildFixtureDefinition(resourceManager, itemDefinitionId);
        if (definition is null ||
            !resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition) ||
            !HousingPlacementCatalog.IsFixtureCustomization(itemDefinition))
        {
            return false;
        }

        fixtureGroup = NormalizeFixtureSelector(fixtureGroup);
        fixtureType = NormalizeFixtureSelector(fixtureType);
        if (fixtureGroup.Length == 0 || fixtureType.Length == 0)
            return false;

        var houseDefinition = ResolveHouseDefinition(resourceManager, house);
        var terrainInfo = ResolveHouseTerrainInfo(resourceManager, houseDefinition);
        var targetModelIds = HousingSurfaceCatalog.GetTargetModelIds(terrainInfo.ZoneName, fixtureType);
        var textureOverride = HousingSurfaceCatalog.GetTextureOverride(itemDefinitionId);
        if (targetModelIds.Count == 0 || textureOverride.Length == 0)
            return false;

        definition.Unknown11 = fixtureGroup;
        definition.Category = fixtureGroup;
        definition.Unknown12 = fixtureType;
        definition.LuaCall = fixtureType;

        var sent = false;
        foreach (var targetModelId in targetModelIds)
        {
            if (!resourceManager.Models.ContainsKey(targetModelId))
                continue;

            definition.ModelId = targetModelId;
            definition.Unknown4 = targetModelId;
            recipient.SendTunneled(new HousingPacketFixtureAsset
            {
                ModelDefinitionId = targetModelId,
                ItemDefinitionId = itemDefinitionId,
                Definition = definition,
                TextureAlias = itemDefinition.TextureAlias ?? "customization",
                TintAlias = itemDefinition.TintAlias ?? "dyetint",
                TintId = tintId,
                PreviewTintId = tintId,
                TextureOverride = textureOverride,
                IsPreview = false
            });
            sent = true;
        }

        return sent;
    }

    private static List<HouseSurfaceCustomization> DeserializeHouseCustomizations(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<HouseSurfaceCustomization>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeFixtureSelector(string value)
    {
        value = value.Trim();
        return value.Length <= 128 ? value : value[..128];
    }

    internal static int GetFixtureTintId(DbHouseFixture fixture)
    {
        return TryGetFixtureAppearance(fixture, out var appearance)
            ? Math.Max(0, appearance.TintId)
            : 0;
    }

    internal static int GetFixtureTintId(DbHouseFixture fixture, IResourceManager resourceManager)
    {
        return ResolveItemTintId(
            resourceManager,
            fixture.ItemDefinitionId,
            GetFixtureTintId(fixture));
    }

    internal static int ResolveItemTintId(
        IResourceManager resourceManager,
        int itemDefinitionId,
        int requestedTintId)
    {
        if (requestedTintId > 0)
            return requestedTintId;

        if (resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition) &&
            itemDefinition.CategoryId == HousingBuildingCategoryId &&
            itemDefinition.Icon.TintId > 0)
        {
            return itemDefinition.Icon.TintId;
        }

        return 0;
    }

    internal static void SetFixtureTintId(DbHouseFixture fixture, int tintId)
    {
        fixture.CustomizationData = tintId == 0
            ? null
            : JsonSerializer.Serialize(new FixtureAppearanceData(Math.Max(0, tintId)));
    }

    private static bool TryGetFixtureAppearance(
        DbHouseFixture fixture,
        out FixtureAppearanceData appearance)
    {
        appearance = new FixtureAppearanceData(0);
        if (string.IsNullOrWhiteSpace(fixture.CustomizationData) ||
            fixture.CustomizationData[0] != '{')
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<FixtureAppearanceData>(fixture.CustomizationData);
            if (parsed is null)
                return false;

            appearance = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetFixtureXmlData(DbHouseFixture fixture)
    {
        return TryGetFixtureAppearance(fixture, out _)
            ? string.Empty
            : fixture.CustomizationData ?? string.Empty;
    }

    private static void SendPersistedFixtureAssets(
        GatewayConnection connection,
        DbHouse house,
        IResourceManager resourceManager)
    {
        foreach (var appearance in house.Fixtures
            .Select(fixture => new
            {
                fixture.ItemDefinitionId,
                TintId = GetFixtureTintId(fixture, resourceManager)
            })
            .Distinct())
        {
            SendFixtureAsset(
                connection,
                appearance.ItemDefinitionId,
                appearance.TintId,
                resourceManager,
                isPreview: false);
        }
    }

    public static HouseZoningInfo TeleportToHouse(GatewayConnection connection, DbHouse house, IResourceManager resourceManager)
    {
        var definition = ResolveHouseDefinition(resourceManager, house);
        var terrainInfo = ResolveHouseTerrainInfo(resourceManager, definition);
        var rotation = ResolveHouseSpawnRotation(definition);
        var baseSpawnPosition = ResolveHouseSpawnPosition(definition);
        var spawnPosition = new Vector4(
            baseSpawnPosition.X,
            baseSpawnPosition.Y + 10f,
            baseSpawnPosition.Z,
            baseSpawnPosition.W);

        if (connection.Player.CurrentHouseGuid == 0)
        {
            connection.Player.StartingZonePosition = connection.Player.Position;
            connection.Player.StartingZoneRotation = connection.Player.Rotation;
        }

        connection.Player.CurrentHouseGuid = GetHouseGuid(house);
        connection.Player.Visible = false;
        connection.Player.UpdatePosition(spawnPosition, rotation);

        connection.SendTunneled(new PacketClientBeginZoning
        {
            Name = terrainInfo.ZoneName,
            Type = 2,
            Position = spawnPosition,
            Rotation = rotation,
            Sky = terrainInfo.Sky,
            Unknown = 1,
            Id = terrainInfo.PacketZoneId,
            GeometryId = terrainInfo.GeometryId,
            OverrideUpdateRadius = true
        });

        return new HouseZoningInfo(definition.Id, definition.ZoneId, terrainInfo.PacketZoneId, terrainInfo.ZoneName, spawnPosition);
    }

    public static void CompleteHouseZoning(GatewayConnection connection)
    {
        connection.Player.UpdateCharacterStats(CharacterStats.HeadInflationPercent.Set(100));

        connection.SendTunneled(new PacketZoneDoneSendingInitialData());
        connection.SendTunneled(new ClientUpdatePacketDoneSendingPreloadCharacters());
    }

    private static List<DbHouse> GetPlayerHouses(DatabaseContext dbContext, Player player, IResourceManager resourceManager)
    {
        var characterId = GuidHelper.GetPlayerId(player.Guid);
        var houses = dbContext.Houses
            .Include(h => h.Fixtures)
            .Where(h => h.CharacterId == characterId)
            .OrderBy(h => h.Id)
            .ToList();

        if (houses.Count == 0)
        {
            houses.Add(GetOrCreateDefaultHouse(dbContext, player, resourceManager));
            dbContext.SaveChanges();
        }

        return houses
            .Select(h => new { House = h, Definition = ResolveHouseDefinition(resourceManager, h) })
            .GroupBy(h => h.Definition.Id)
            .Select(group => group
                .OrderBy(h => h.House.Definition == h.Definition.Id ? 0 : 1)
                .ThenBy(h => h.House.Id)
                .First()
                .House)
            .OrderBy(h => h.Id)
            .ToList();
    }

    private static DbHouse CreateHouse(
        DatabaseContext dbContext,
        ulong characterId,
        HouseDefinition definition,
        ClientItemDefinition? itemDefinition = null)
    {
        var now = DateTimeOffset.UtcNow;
        var house = new DbHouse
        {
            Id = GetNextHouseId(dbContext),
            CharacterId = characterId,
            Definition = definition.Id,
            Name = GetCanonicalHouseName(definition, itemDefinition?.Comment),
            IsLocked = false,
            IsMembersOnly = false,
            IsFloraAllowed = true,
            PetAutospawn = false,
            MaxFixtureCount = DefaultMaxFixtureCount,
            MaxLandmarkCount = 0,
            FurnitureScore = 0,
            Votes = 0,
            Rating = 0,
            Description = string.Empty,
            KeywordList = itemDefinition?.Comment ?? string.Empty,
            Created = now,
            LastVisited = now
        };

        dbContext.Houses.Add(house);

        return house;
    }

    private static int GetNextHouseId(DatabaseContext dbContext)
    {
        return (dbContext.Houses.Select(h => (int?)h.Id).Max() ?? 0) + 1;
    }

    private static IEnumerable<int> HouseDefinitionIdsByNameId(int nameId)
    {
        return nameId switch
        {
            296 => [25],
            5184 => [5],
            5185 => [19],
            5409 => [34],
            6289 => [27],
            6290 => [26],
            6514 => [1],
            6775 => [49, 28],
            8320 => [14],
            8321 => [35],
            8322 => [10],
            17432 => [17],
            26345 => [55],
            420151 => [24],
            435461 => [33],
            442333 => [30],
            5103265 => [2],
            69964 => [9],
            69965 => [8],
            69966 => [22],
            69967 => [21],
            69968 => [68],
            69969 => [69],
            69971 => [6],
            69972 => [88],
            69973 => [16],
            69974 => [3],
            69975 => [4],
            69976 => [20],
            69977 => [15],
            69978 => [98],
            _ => []
        };
    }

    private static string? GetCanonicalHouseName(HouseDefinition definition, string? itemName)
    {
        if (HouseDisplaysByNameId.TryGetValue(definition.NameId, out var display))
            return display.Name;

        return string.IsNullOrWhiteSpace(itemName) ? null : itemName;
    }

    private static string? GetDisplayHouseName(DbHouse house, HouseDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(house.Name) && !IsGenericHouseName(house.Name))
            return house.Name;

        return GetCanonicalHouseName(definition, house.Name);
    }

    public static string GetDirectoryHouseName(DbHouse house, IResourceManager resourceManager)
    {
        var definition = ResolveHouseDefinition(resourceManager, house);
        return GetDisplayHouseName(house, definition) ?? "Housing";
    }

    public static string GetDirectoryCandidateId(DbHouse house)
    {
        return GetDirectoryCandidateId(house.Id);
    }

    public static string GetDirectoryCandidateId(int houseId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(houseId);
        return GuidHelper.GetHouseGuid((ulong)houseId).ToString(CultureInfo.InvariantCulture);
    }

    public static string GetDirectorySnapshot(DbHouse house, IResourceManager resourceManager)
    {
        var definition = ResolveHouseDefinition(resourceManager, house);
        return GetDirectorySnapshot(definition.Id);
    }

    public static string GetDirectorySnapshot(int definitionId)
    {
        if (CanonicalHouseDefinitionIds.TryGetValue(definitionId, out var canonicalDefinitionId))
            definitionId = canonicalDefinitionId;

        return HouseDirectorySnapshotsByDefinitionId.TryGetValue(definitionId, out var snapshot)
            ? snapshot
            : "placeholder";
    }

    private static int GetDisplayHouseIcon(HouseDefinition definition)
    {
        if (HouseDisplaysByNameId.TryGetValue(definition.NameId, out var display))
            return display.IconId;

        return definition.Icon;
    }

    private static bool IsGenericHouseName(string name)
    {
        return name.Equals("Housing", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("House", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericOrDefaultDefinition(int definitionId)
    {
        return definitionId == DefaultHouseDefinitionId ||
            CanonicalHouseDefinitionIds.ContainsKey(definitionId);
    }

    private static HouseDefinition ResolveHouseDefinition(IResourceManager resourceManager, ClientItemDefinition itemDefinition)
    {
        if (HouseDefinitionIdsByItemId.TryGetValue(itemDefinition.Id, out var definitionId) &&
            resourceManager.Houses.TryGetValue(definitionId, out var byItem))
            return byItem;

        if (itemDefinition.Param1 > 0 &&
            resourceManager.Houses.TryGetValue(itemDefinition.Param1, out var byParam) &&
            byParam.NameId == itemDefinition.NameId)
            return byParam;

        var byName = resourceManager.Houses.Values
            .Where(h => h.NameId == itemDefinition.NameId)
            .ToList();

        var loadableByName = byName.FirstOrDefault(h => resourceManager.Zones.ContainsKey(h.ZoneId));

        if (loadableByName is not null)
            return loadableByName;

        var firstByName = byName.FirstOrDefault();

        if (firstByName is not null)
            return firstByName;

        if (resourceManager.Houses.TryGetValue(DefaultHouseDefinitionId, out var defaultDefinition))
            return defaultDefinition;

        return resourceManager.Houses.Values.OrderBy(h => h.Id).First();
    }

    private static HouseDefinition ResolveHouseDefinition(IResourceManager resourceManager, DbHouse house)
    {
        if (CanonicalHouseDefinitionIds.TryGetValue(house.Definition, out var canonicalDefinitionId) &&
            resourceManager.Houses.TryGetValue(canonicalDefinitionId, out var canonicalDefinition))
            return canonicalDefinition;

        if (!string.IsNullOrWhiteSpace(house.Name) &&
            IsGenericOrDefaultDefinition(house.Definition) &&
            HouseDefinitionIdsByDisplayName.TryGetValue(house.Name, out var definitionId) &&
            resourceManager.Houses.TryGetValue(definitionId, out var definition))
            return definition;

        return ResolveHouseDefinition(resourceManager, house.Definition);
    }

    private static HouseDefinition ResolveHouseDefinition(IResourceManager resourceManager, int definitionId)
    {
        if (CanonicalHouseDefinitionIds.TryGetValue(definitionId, out var canonicalDefinitionId))
            definitionId = canonicalDefinitionId;

        if (resourceManager.Houses.TryGetValue(definitionId, out var definition))
            return definition;

        if (resourceManager.Houses.TryGetValue(DefaultHouseDefinitionId, out var defaultDefinition))
            return defaultDefinition;

        return resourceManager.Houses.Values.OrderBy(h => h.Id).First();
    }

    private static HouseTerrainInfo ResolveHouseTerrainInfo(IResourceManager resourceManager, HouseDefinition definition)
    {
        // These values come from the original housing zoning captures. The client
        // will not finish loading either Seaside terrain when the house definition
        // id is reused as the zoning/geometry id.
        if (definition.NameId == 8320)
            return new HouseTerrainInfo(1, 214, "hsg_emptylot_seaside_cliffs_01", "sky_seaside24.xml");

        if (definition.NameId == 8321)
            return new HouseTerrainInfo(1, 214, "hsg_emptylot_seaside_beach_01", "sky_seaside24.xml");

        if (definition.Id == 49)
            return new HouseTerrainInfo(28, 28, "hsg_hum_sg_night_club_01", "sky_housing_wilds.xml");

        var clientTerrainName = ResolveClientTerrainName(definition);

        if (clientTerrainName is not null)
            return new HouseTerrainInfo(
                definition.ZoneId,
                definition.ZoneId,
                clientTerrainName,
                ResolveClientTerrainSky(clientTerrainName));

        if (resourceManager.Zones.TryGetValue(definition.ZoneId, out var zoneDefinition))
        {
            var terrainName = NormalizeClientTerrainName(zoneDefinition.Name);

            return new HouseTerrainInfo(
                definition.ZoneId,
                definition.ZoneId,
                terrainName,
                zoneDefinition.Sky ?? ResolveClientTerrainSky(terrainName));
        }

        return new HouseTerrainInfo(
            definition.ZoneId,
            definition.ZoneId,
            "hsg_emptylot_seaside_beach_01",
            "sky_seaside24.xml");
    }

    private static string? ResolveClientTerrainName(HouseDefinition definition)
    {
        if (definition.Id == 49)
            return "hsg_hum_sg_night_club_01";

        var terrainName = definition.NameId switch
        {
            6514 => "hsg_emptylot_blackspore_01",
            69966 => "hsg_hum_blackspore_01",
            5103265 => "hsg_emptylot_boat_01",
            69974 => "hsg_emptylot_bog_shore_01",
            69975 => "hsg_emptylot_briarfalls_01",
            435461 => "farming_briarwood_farmstead_01",
            5184 or 6293 or 6294 => "hsg_emptylot_briarwood_01",
            69971 => "hsg_emptylot_bw_towers_01",
            69969 => "hsg_emptylot_castle_01",
            69965 => "hsg_emptylot_gl_staircase_01",
            69964 => "hsg_emptylot_gl_valley_01",
            8322 => "hsg_emptylot_island_01",
            69978 => "hsg_emptylot_laketree_01",
            69968 => "hsg_emptylot_merryvale_01",
            8320 => "hsg_emptylot_seaside_cliffs_01",
            8321 => "hsg_emptylot_seaside_beach_01",
            69977 => "hsg_emptylot_sh_batcave_01",
            69973 => "hsg_emptylot_sh_crystalmines_01",
            17432 => "hsg_emptylot_shrouded_gloam_01",
            69976 => "hsg_emptylot_vale_steam_01",
            5409 => "farming_wilds_farmstead_02",
            6289 => "hsg_hum_economy_01",
            6290 => "hsg_hum_deluxe_01",
            296 => "hsg_hum_condo_w_lot",
            69972 => "hsg_emptylot_blackspore_01",
            69967 => "hsg_emptylot_wugachug_01",
            26345 or 6292 => "hsg_hum_snowhill_01",
            5185 => "hsg_emptylot_snowhill_01",
            442333 => "hsg_emptylot_sh_crystalmines_01",
            420151 => "hsg_hum_condo",
            6775 => "hsg_hum_condo_w_lot",
            _ => null
        };

        if (terrainName is not null)
            return terrainName;

        return definition.Id switch
        {
            35 => "hsg_emptylot_seaside_beach_01",
            55 => "hsg_hum_snowhill_01",
            _ => null
        };
    }

    private static Vector4 ResolveHouseSpawnPosition(HouseDefinition definition)
    {
        return definition.Id switch
        {
            14 => new Vector4(568.8f, 40.8f, 517.9f, 1f), // Captured Seaside Cliffs position after the zoning lift.
            35 => new Vector4(440.632f, -10.071f, 432.801f, 1f), // Captured Sandy Beach position after the zoning lift.
            24 => new Vector4(60f, -6f, 62f, 0f), // Apartment center; +10 zoning lift lands on the main floor.
            30 => new Vector4(378.6f, 8.8f, 427.8f, 0f), // Rumbledome terrain is absent; use the complete Crystal Mines lot.
            49 => new Vector4(560.9f, 100f, 552.2f, 0f),
            98 => new Vector4(370f, 20f, 455f, 0f), // Flat Lake Tree approach, west of the rocky lot edge.
            55 => new Vector4(375f, -10f, 422f, 0f), // Snowhill Lodge interior area; final Y should stay on terrain.
            _ => definition.SpawnPosition
        };
    }

    private static Quaternion ResolveHouseSpawnRotation(HouseDefinition definition)
    {
        return definition.Id switch
        {
            35 => new Quaternion(-0.9999741f, 0f, -0.0072035603f, 0f),
            _ => ToQuaternion(definition.SpawnRotation)
        };
    }

    private static List<BoundingBox> ResolveHouseBuildAreas(IResourceManager resourceManager, HouseDefinition definition)
    {
        var fallbackDefinitionId = definition.Id switch
        {
            30 => 16,
            49 => 28,
            88 => 1,
            _ => definition.Id
        };

        return resourceManager.Houses.TryGetValue(fallbackDefinitionId, out var fallback)
            ? fallback.BuildAreas
            : definition.BuildAreas;
    }

    private static string NormalizeClientTerrainName(string zoneName)
    {
        return zoneName switch
        {
            "hsg_emptylot_wilds_01" => "hsg_emptylot_wilds_halflot_01",
            "hsg_emptylot_snowhill_large_01" => "hsg_emptylot_snowhill_01",
            "hsg_emptylot_snowhill_small_01" => "hsg_emptylot_snowhill_01",
            "hsg_club_house_01" => "hsg_hum_sg_night_club_01",
            "hsg_snowhill_lodge_01" => "hsg_hum_snowhill_01",
            _ => zoneName
        };
    }

    private static string ResolveClientTerrainSky(string terrainName)
    {
        return terrainName switch
        {
            "hsg_emptylot_snowhill_01" => "sky_snowhill_housinglot_01.xml",
            "hsg_emptylot_wilds_halflot_01" => "sky_housing_wilds.xml",
            "hsg_emptylot_wilds_rapids_01" => "sky_housing_wilds.xml",
            "hsg_emptylot_bixie_field_01" => "sky_housing_wilds.xml",
            "hsg_hum_blackspore_01" => "sky_housing_wilds.xml",
            "hsg_hum_condo" => "sky_housing_wilds.xml",
            "hsg_hum_condo_w_lot" => "sky_housing_wilds.xml",
            "hsg_hum_deluxe_01" => "sky_housing_wilds.xml",
            "hsg_hum_economy_01" => "sky_housing_wilds.xml",
            "hsg_hum_economy_decorated_01" => "sky_housing_wilds.xml",
            "hsg_hum_sg_night_club_01" => "sky_housing_wilds.xml",
            "hsg_hum_snowhill_01" => "sky_snowhill_housinglot_01.xml",
            "farming_briarwood_farmstead_01" => "sky_briarwood24.xml",
            "farming_wilds_farmstead_02" => "sky_wilds24.xml",
            _ => "sky_seaside24.xml"
        };
    }

    private static (ulong OwnerGuid, string OwnerName) ResolveOwnerIdentity(DbHouse house, Player viewer)
    {
        if (house.CharacterId == GuidHelper.GetPlayerId(viewer.Guid))
            return (viewer.Guid, viewer.Name.FullName);

        var ownerName = house.Character?.FullName;
        if (string.IsNullOrWhiteSpace(ownerName) && house.Character is not null)
            ownerName = $"{house.Character.FirstName} {house.Character.LastName}".Trim();

        return (
            GuidHelper.GetPlayerGuid(house.CharacterId),
            string.IsNullOrWhiteSpace(ownerName) ? "Unknown" : ownerName);
    }

    private static PlayerHousingInstanceInfo ToInstanceInfo(DbHouse house, Player owner, IResourceManager resourceManager)
    {
        return ToInstanceInfo(
            house,
            owner.Guid,
            owner.Name.FullName,
            resourceManager,
            owner.Guid,
            includeFactoryPlotId: false);
    }

    private static PlayerHousingInstanceInfo ToInstanceInfo(
        DbHouse house,
        ulong ownerGuid,
        string ownerName,
        IResourceManager resourceManager,
        ulong viewerGuid,
        bool includeFactoryPlotId)
    {
        var definition = ResolveHouseDefinition(resourceManager, house);
        var houseName = GetDisplayHouseName(house, definition);
        var factoryPlotId = 0;

        return new PlayerHousingInstanceInfo
        {
            OwnerGuid = ownerGuid,
            InstanceGuid = GetHouseGuid(house),
            NameId = definition.NameId,
            OwnerName = ownerName,
            HouseName = houseName,
            IconId = GetDisplayHouseIcon(definition),
            FixtureCount = house.Fixtures.Count,
            FurnitureScore = house.FurnitureScore,
            LastVisited = house.LastVisited.UtcDateTime,
            IsLocked = house.IsLocked,
            IsMembersOnly = house.IsMembersOnly,
            IsFloraAllowed = house.IsFloraAllowed,
            Description = house.Description,
            KeywordList = house.KeywordList,
            Unknown21 = string.Empty,
            Rating = house.Rating,
            Votes = house.Votes,
            HasRating = house.IsPublished,
            CanVote = house.IsPublished && ownerGuid != viewerGuid,
            FactoryPlotId = factoryPlotId,
            WhenCreated = house.Created.ToUnixTimeSeconds()
        };
    }

    private static PlayerHousingInstanceData ToInstanceData(
        DbHouse house,
        Player viewer,
        ulong ownerGuid,
        string ownerName,
        IResourceManager resourceManager)
    {
        var definition = ResolveHouseDefinition(resourceManager, house);
        var houseName = GetDisplayHouseName(house, definition);

        return new PlayerHousingInstanceData
        {
            HouseGuid = GetHouseGuid(house),
            OwnerGuid = ownerGuid,
            OwnerName = ownerName,
            Unknown4 = 0,
            Unknown7 = 0,
            NameId = definition.NameId,
            Name = houseName,
            IsLocked = house.IsLocked,
            IsFloraAllowed = house.IsFloraAllowed,
            MaxFixtureCount = house.MaxFixtureCount,
            MaxLandmarkCount = house.MaxLandmarkCount,
            Unknown15 = 0,
            Unknown14 = false,
            Preview = false,
            CurFixtureCount = house.Fixtures.Count,
            CurLandmarkCount = 0,
            IconId = GetDisplayHouseIcon(definition),
            Unknown18 = false,
            FurnitureScore = house.FurnitureScore,
            Unknown20 = 0,
            IsMembersOnly = house.IsMembersOnly,
            Unknown22 = house.Description,
            Unknown23 = house.KeywordList,
            Unknown24 = false,
            Fixtures = ToFixtureInstances(house, viewer, resourceManager),
            Permissions = ToInstancePermissions(ownerGuid),
            BuildAreas = ResolveHouseBuildAreas(resourceManager, definition)
        };
    }

    private static Dictionary<int, InstancePermission> ToInstancePermissions(ulong ownerGuid)
    {
        return new Dictionary<int, InstancePermission>
        {
            [0] = new()
            {
                Guid = ownerGuid,
                Level = 3
            }
        };
    }

    private static HousingPacketFixtureItemList ToFixtureItemList(DbHouse house, Player player, IResourceManager resourceManager)
    {
        var packet = new HousingPacketFixtureItemList();
        var fixtureDefinitionIds = new HashSet<int>();

        foreach (var fixture in house.Fixtures.OrderBy(f => f.Id))
        {
            // Persisted fixtures belong to InstanceData. FixtureItemList.Infos is
            // strictly the available inventory list; adding placed fixtures here
            // makes a consumed quantity-one item appear placeable again.
            TryAddFixtureDefinition(packet, fixtureDefinitionIds, resourceManager, fixture.ItemDefinitionId);
        }

        foreach (var item in player.Items
            .Where(item => item.Count > 0 &&
                resourceManager.ClientItemDefinitions.TryGetValue(item.Definition, out var definition) &&
                IsFixtureInventoryItem(definition))
            .OrderBy(item => item.Definition)
            .ThenBy(item => item.Id))
        {
            if (!resourceManager.ClientItemDefinitions.TryGetValue(item.Definition, out var itemDefinition))
                continue;

            if (!TryAddFixtureDefinition(packet, fixtureDefinitionIds, resourceManager, item.Definition))
                continue;

            packet.Infos.Add(new FixtureInstanceInfo
            {
                FixtureGuid = (ulong)item.Id,
                ItemDefinitionId = item.Definition,
                CouplingDisplay =
                {
                    Id = item.Id,
                    CompositeEffect = itemDefinition.CompositeEffectId
                }
            });

            if (itemDefinition.CompositeEffectId != 0 && !packet.Effects.Contains(itemDefinition.CompositeEffectId))
                packet.Effects.Add(itemDefinition.CompositeEffectId);
        }

        return packet;
    }

    private static Dictionary<uint, FixtureInstance> ToFixtureInstances(
        DbHouse house,
        Player owner,
        IResourceManager resourceManager)
    {
        var result = new Dictionary<uint, FixtureInstance>();

        foreach (var fixture in house.Fixtures.OrderBy(f => f.Id))
        {
            var tintId = GetFixtureTintId(fixture, resourceManager);
            resourceManager.ClientItemDefinitions.TryGetValue(fixture.ItemDefinitionId, out var itemDefinition);
            var fixtureGuid = HousingFixtureActorService.GetClientFixtureGuid(
                owner.Guid,
                house.Id,
                fixture.Id);
            var fixtureInstanceKey = GetFixtureRuntimeKey(fixtureGuid, fixture.Id);

            result[fixtureInstanceKey] = new FixtureInstance
            {
                Guid = fixtureGuid,
                HouseGuid = GetHouseGuid(house),
                Id = fixture.ItemDefinitionId,
                Unknown4 = 0,
                Unknown5 = new Vector4(fixture.PositionX, fixture.PositionY, fixture.PositionZ, fixture.PositionW),
                Unknown6 = HousingFixtureActorService.ToHousingRotation(new Quaternion(
                    fixture.RotationX,
                    fixture.RotationY,
                    fixture.RotationZ,
                    fixture.RotationW)),
                Unknown7 = Quaternion.Identity,
                Unknown8 = unchecked((long)HousingFixtureActorService.GetNpcGuid(
                    owner.Guid,
                    house.Id,
                    fixtureGuid)),
                Unknown9 = tintId,
                Unknown10 = 0,
                CustomizationDetails = new CustomizationDetail
                {
                    Type = 1,
                    TextureAlias = itemDefinition?.TextureAlias ?? string.Empty,
                    TintAlias = itemDefinition?.TintAlias ?? string.Empty,
                    TintId = tintId,
                    TextureOverride = string.Empty
                },
                Unknown11 = string.Empty,
                Unknown12 = string.Empty,
                Unknown13 = 0,
                Unknown14 = GetFixtureXmlData(fixture),
                Unknown15 = fixture.Scale <= 0 ? 1.0f : fixture.Scale,
                Unknown16 = false,
                Unknown17 = 0
            };
        }

        return result;
    }

    private static bool TryAddFixtureDefinition(
        HousingPacketFixtureItemList packet,
        HashSet<int> fixtureDefinitionIds,
        IResourceManager resourceManager,
        int itemDefinitionId)
    {
        if (!fixtureDefinitionIds.Add(itemDefinitionId))
            return true;

        var fixtureDefinition = BuildFixtureDefinition(resourceManager, itemDefinitionId);
        if (fixtureDefinition is null)
        {
            fixtureDefinitionIds.Remove(itemDefinitionId);
            return false;
        }

        packet.Definitions.Add(fixtureDefinition);
        return true;
    }

    private static FixtureDefinition? BuildFixtureDefinition(IResourceManager resourceManager, int itemDefinitionId)
    {
        if (!resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
            return null;

        var isCustomization = HousingPlacementCatalog.IsFixtureCustomization(itemDefinition);
        var hasPlacementEntry = HousingPlacementCatalog.TryGet(itemDefinitionId, out var placementEntry);
        var modelId = ResolveFixtureModelId(resourceManager, itemDefinitionId);
        var assetName = hasPlacementEntry
            ? placementEntry.AssetName
            : itemDefinition.ModelName ?? string.Empty;
        var isAssetGroup = assetName.EndsWith(".agr", StringComparison.OrdinalIgnoreCase);

        if (modelId == 0 && !isCustomization && !isAssetGroup)
            return null;

        return new FixtureDefinition
        {
            Id = itemDefinitionId,
            ItemDefinitionId = itemDefinitionId,
            Unknown3 = isCustomization
                ? itemDefinition.Param1
                : hasPlacementEntry ? placementEntry.PlacementType : 1,
            Unknown4 = modelId,
            ModelId = modelId,
            Unknown5 = false,
            Unknown6 = false,
            Unknown7 = true,
            Unknown8 = false,
            Unknown9 = false,
            Unknown10 = false,
            Unknown11 = ResolveFixtureCategory(resourceManager, itemDefinitionId),
            Unknown12 = hasPlacementEntry || isAssetGroup ? assetName : string.Empty,
            Category = ResolveFixtureCategory(resourceManager, itemDefinitionId),
            LuaCall = string.Empty,
            CompositeEffectId = ResolveFixtureCompositeEffectId(resourceManager, itemDefinitionId),
            Unknown14 = 1.0f,
            Unknown15 = 1.0f,
            Unknown16 = false,
            Unknown17 = false,
            Unknown18 = false,
            Unknown19 = false
        };
    }

    private static string ResolveFixtureCategory(IResourceManager resourceManager, int itemDefinitionId)
    {
        return resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition)
            ? itemDefinition.CategoryId.ToString()
            : string.Empty;
    }

    internal static int ResolveFixtureModelId(IResourceManager resourceManager, int itemDefinitionId)
    {
        if (!resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
            return 0;

        if (!HousingPlacementCatalog.IsFixtureCustomization(itemDefinition) && itemDefinition.Param1 > 0)
            return itemDefinition.Param1;

        var modelName = HousingPlacementCatalog.TryGet(itemDefinitionId, out var placementEntry)
            ? placementEntry.AssetName
            : itemDefinition.ModelName;

        if (string.IsNullOrWhiteSpace(modelName))
            return 0;

        return ResolveModelId(resourceManager, modelName);
    }

    internal static int ResolveFixtureActorModelId(IResourceManager resourceManager, int itemDefinitionId)
    {
        if (!resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
            return 0;

        if (!HousingPlacementCatalog.IsFixtureCustomization(itemDefinition) && itemDefinition.Param1 > 0)
            return itemDefinition.Param1;

        var modelName = HousingPlacementCatalog.TryGet(itemDefinitionId, out var placementEntry)
            ? placementEntry.AssetName
            : itemDefinition.ModelName;

        if (string.IsNullOrWhiteSpace(modelName))
            return 0;

        if (modelName.EndsWith(".agr", StringComparison.OrdinalIgnoreCase))
        {
            var actorName = modelName[..^4];
            if (actorName.EndsWith("_complete", StringComparison.OrdinalIgnoreCase))
                actorName = actorName[..^9];

            actorName += ".adr";
            var actorModelId = ResolveModelId(resourceManager, actorName);
            if (actorModelId != 0)
                return actorModelId;
        }

        return ResolveModelId(resourceManager, modelName);
    }

    private static int ResolveModelId(IResourceManager resourceManager, string modelName)
    {
        return resourceManager.Models.Values
            .FirstOrDefault(candidate => string.Equals(candidate.ModelFileName, modelName, StringComparison.OrdinalIgnoreCase))
            ?.Id ?? 0;
    }

    private static int ResolveFixtureCompositeEffectId(IResourceManager resourceManager, int itemDefinitionId)
    {
        return resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition)
            ? itemDefinition.CompositeEffectId
            : 0;
    }

    private static ulong GetHouseGuid(DbHouse house)
    {
        return GuidHelper.GetHouseGuid((ulong)house.Id);
    }

    private static Quaternion ToQuaternion(System.Numerics.Vector4 value)
    {
        return new Quaternion(value.X, value.Y, value.Z, value.W);
    }
}
