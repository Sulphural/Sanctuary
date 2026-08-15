using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Game;
using Sanctuary.Gateway;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.GameCommerce;

namespace Sanctuary.UdpLibrary.Tests;

[TestClass]
[DoNotParallelize]
public class HousingStoreCatalogAuditTests
{
    private static readonly HashSet<int> StationHousingGroupIds = [119, 123, 141, 142];
    private const int StationHouseGroupId = 125;
    private const int FertilizerItemDefinitionId = 15968;

    private static ResourceManager _resources = null!;
    private static MethodInfo _buildFixtureDefinition = null!;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        var sourceRoot = FindSourceRoot();
        var previousDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(sourceRoot);
            _resources = new ResourceManager(NullLogger<ResourceManager>.Instance);
            Assert.IsTrue(_resources.Load(), $"Failed to load resources from {sourceRoot}.");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }

        _buildFixtureDefinition = typeof(HouseOwnershipService).GetMethod(
            "BuildFixtureDefinition",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate the live fixture-definition builder.");
    }

    [TestMethod]
    public void EveryStationHousingListingIsPurchasableAndBuildable()
    {
        var failures = new List<string>();
        var fixtureEntries = 0;
        var houseEntries = 0;
        var nonPlaceableEntries = 0;

        foreach (var bundle in _resources.Stores.Values
            .SelectMany(store => store.Bundles.Values)
            .Where(bundle => StationHousingGroupIds.Contains(bundle.CategoryGroupId) ||
                bundle.CategoryGroupId == StationHouseGroupId))
        {
            foreach (var entry in bundle.Entries)
            {
                var definition = ResolveStoreItem(entry);
                if (definition is null)
                {
                    failures.Add($"Bundle {bundle.Id} {bundle.Comment}: no definition for {entry.MarketingItemId}/{entry.GameItemId}.");
                    continue;
                }

                if (bundle.CategoryGroupId == StationHouseGroupId)
                {
                    houseEntries++;
                    if (!HouseOwnershipService.IsHouseItem(definition))
                        failures.Add($"Bundle {bundle.Id} {bundle.Comment}: item {definition.Id} is not a house deed.");

                    continue;
                }

                if (definition.Id == FertilizerItemDefinitionId)
                {
                    nonPlaceableEntries++;
                    if (HouseOwnershipService.IsFixtureInventoryItem(definition))
                        failures.Add($"Bundle {bundle.Id} {bundle.Comment}: fertilizer incorrectly enters build inventory.");

                    continue;
                }

                fixtureEntries++;
                ValidateFixture(bundle.Id, bundle.Comment ?? string.Empty, definition, failures);
            }
        }

        TestContext.WriteLine(
            $"Station audit: {fixtureEntries} fixture entries, {houseEntries} house deeds, {nonPlaceableEntries} intentional non-placeable entries.");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void EveryCoinStoreHousingFixtureIsPurchasableAndBuildable()
    {
        var failures = new List<string>();
        var fixtureEntries = 0;

        foreach (var metadata in _resources.CoinStoreItems.Values)
        {
            if (!_resources.ClientItemDefinitions.TryGetValue(metadata.Id, out var definition) ||
                !HouseOwnershipService.IsFixtureInventoryItem(definition))
            {
                continue;
            }

            fixtureEntries++;
            ValidateFixture(0, "Coin Store", definition, failures);
        }

        TestContext.WriteLine($"Coin-store audit: {fixtureEntries} unique housing fixtures.");
        Assert.IsGreaterThan(1100, fixtureEntries, "The coin-store housing catalog was unexpectedly small.");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void PlacementTableIdCollisionsStayOutOfBuildInventory()
    {
        var collisions = _resources.ClientItemDefinitions.Values
            .Where(definition => definition.Type == 1 &&
                HousingPlacementCatalog.IsFixture(definition.Id) &&
                definition.CategoryId is not (52 or 53 or 54 or 56 or 57 or 147) &&
                !HasHousingModelPrefix(definition))
            .ToList();

        var admitted = collisions
            .Where(HouseOwnershipService.IsFixtureInventoryItem)
            .Select(definition => $"{definition.Id} {definition.Comment} (category {definition.CategoryId})")
            .ToList();

        TestContext.WriteLine($"Placement-table collision audit: {collisions.Count} non-housing definitions checked.");
        Assert.AreEqual(0, admitted.Count, string.Join(Environment.NewLine, admitted));
    }

    [TestMethod]
    public void BuildingBlockPurchasesPreserveTheSelectedTint()
    {
        var block = _resources.ClientItemDefinitions.Values
            .First(definition => definition.CategoryId == 147 && definition.IsTintable);

        const int selectedTintId = 246;
        Assert.AreEqual(
            selectedTintId,
            HouseOwnershipService.ResolveItemTintId(_resources, block.Id, selectedTintId));
        Assert.AreEqual(
            block.Icon.TintId,
            HouseOwnershipService.ResolveItemTintId(_resources, block.Id, 0));
    }

    [TestMethod]
    public void HousingPurchasePolicyDoesNotAbsorbUnrelatedStoreItems()
    {
        var mount = _resources.ClientItemDefinitions.Values
            .First(definition => definition.Type == 19);
        var unrelatedTypeThreeItem = _resources.ClientItemDefinitions.Values
            .First(definition => definition.Type == 3 &&
                !HouseOwnershipService.IsFixtureInventoryItem(definition));

        Assert.IsFalse(StoreInventoryPurchasePolicy.IsSupported(mount));
        Assert.IsFalse(StoreInventoryPurchasePolicy.IsSupported(unrelatedTypeThreeItem));
    }

    private static ClientItemDefinition? ResolveStoreItem(MarketingBundleDefinition.Entry entry)
    {
        if (_resources.ClientItemDefinitions.TryGetValue(entry.MarketingItemId, out var definition))
            return definition;

        return _resources.ClientItemDefinitions.TryGetValue(entry.GameItemId, out definition)
            ? definition
            : null;
    }

    private static void ValidateFixture(
        int bundleId,
        string listingName,
        ClientItemDefinition definition,
        List<string> failures)
    {
        var prefix = bundleId == 0
            ? $"Coin item {definition.Id} {definition.Comment}"
            : $"Bundle {bundleId} {listingName}, item {definition.Id}";

        if (!StoreInventoryPurchasePolicy.IsSupported(definition))
            failures.Add($"{prefix}: rejected by store purchase policy (type {definition.Type}).");

        if (!HouseOwnershipService.IsFixtureInventoryItem(definition))
            failures.Add($"{prefix}: omitted from housing build inventory.");

        var fixtureDefinition = _buildFixtureDefinition.Invoke(null, [_resources, definition.Id]);
        if (fixtureDefinition is null)
            failures.Add($"{prefix}: live fixture-definition builder could not resolve its model.");

        if (!HasValidExplicitModelId(definition))
            failures.Add($"{prefix}: references unknown model ID {definition.Param1}.");
    }

    private static bool HasValidExplicitModelId(ClientItemDefinition definition)
    {
        return definition.Param1 <= 0 ||
            HousingPlacementCatalog.IsFixtureCustomization(definition) ||
            _resources.Models.ContainsKey(definition.Param1);
    }

    private static bool HasHousingModelPrefix(ClientItemDefinition definition)
    {
        return !string.IsNullOrWhiteSpace(definition.ModelName) &&
            (definition.ModelName.StartsWith("hsg_", StringComparison.OrdinalIgnoreCase) ||
                definition.ModelName.StartsWith("mkt_boombox", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Sanctuary.Game")) &&
                File.Exists(Path.Combine(directory.FullName, "Resources", "ClientItemDefinitions.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Sanctuary source resource directory.");
    }
}
