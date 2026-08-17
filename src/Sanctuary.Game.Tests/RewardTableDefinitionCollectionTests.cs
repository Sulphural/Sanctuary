using System;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Game.Resources;
using Sanctuary.Game.Resources.Definitions.Rewards;

namespace Sanctuary.Game.Tests;

[TestClass]
public sealed class RewardTableDefinitionCollectionTests
{
    [TestMethod]
    public void Load_ParsesPreviewStandIn()
    {
        var collection = Load("""
            [
                {
                    "Key": "mystery-gift",
                    "PreviewItemDefinitionId": 76538,
                    "PreviewQuantity": 2,
                    "DropTable": [
                        { "RewardType": "Item", "ItemDefinitionId": 76620, "Weight": 1 }
                    ]
                }
            ]
            """, out var loaded);

        Assert.IsTrue(loaded);
        Assert.IsTrue(collection.TryGetValue("mystery-gift", out var table));
        Assert.AreEqual(76538, table!.PreviewItemDefinitionId);
        Assert.AreEqual(2, table.PreviewQuantity);
    }

    [TestMethod]
    public void Load_DefaultsPreviewToThePoolItself()
    {
        var collection = Load("""
            [
                {
                    "Key": "no-stand-in",
                    "DropTable": [
                        { "RewardType": "Item", "ItemDefinitionId": 76620, "Weight": 1 }
                    ]
                }
            ]
            """, out var loaded);

        Assert.IsTrue(loaded);
        Assert.IsTrue(collection.TryGetValue("no-stand-in", out var table));
        // 0 = no stand-in, so the preview falls through to the drops themselves.
        Assert.AreEqual(0, table!.PreviewItemDefinitionId);
        Assert.AreEqual(1, table.PreviewQuantity);
    }

    [TestMethod]
    public void Load_RejectsNonPositivePreviewQuantity()
    {
        Load("""
            [
                {
                    "Key": "zero-preview",
                    "PreviewItemDefinitionId": 76538,
                    "PreviewQuantity": 0,
                    "DropTable": [
                        { "RewardType": "Item", "ItemDefinitionId": 76620, "Weight": 1 }
                    ]
                }
            ]
            """, out var loaded);

        Assert.IsFalse(loaded);
    }

    // The same item listed at several weights is one outcome to the player, and previews at the largest
    // of its quantities - the grouping TryBuildPreview relies on.
    [TestMethod]
    public void Load_KeepsDuplicateItemDefinitionsAsSeparateWeightedDrops()
    {
        var collection = Load("""
            [
                {
                    "Key": "repeated-item",
                    "DropTable": [
                        { "RewardType": "Item", "ItemDefinitionId": 76620, "Quantity": 1, "Weight": 3 },
                        { "RewardType": "Item", "ItemDefinitionId": 76620, "Quantity": 5, "Weight": 1 }
                    ]
                }
            ]
            """, out var loaded);

        Assert.IsTrue(loaded);
        Assert.IsTrue(collection.TryGetValue("repeated-item", out var table));

        var outcomes = table!.DropTable.OfType<ItemRewardDropDefinition>()
            .GroupBy(drop => drop.ItemDefinitionId)
            .ToArray();

        Assert.AreEqual(1, outcomes.Length);
        Assert.AreEqual(5, outcomes[0].Max(drop => drop.Quantity));
        Assert.AreEqual(4, table.Table.TotalWeight);
    }

    private static RewardTableDefinitionCollection Load(string json, out bool loaded)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"rewards-{Guid.NewGuid():N}.json");
        File.WriteAllText(filePath, json);

        try
        {
            var collection = new RewardTableDefinitionCollection(NullLogger.Instance);
            loaded = collection.Load(filePath);
            return collection;
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
