using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Game.Resources.Definitions;

namespace Sanctuary.Game.Tests;

[TestClass]
public sealed class CollectionDefinitionTests
{
    [TestMethod]
    public void CreateClientCollection_DerivesProgressFromOwnedItems()
    {
        var definition = new CollectionDefinition
        {
            Id = 17054,
            NameId = 17055,
            CategoryId = 10,
            Entries =
            [
                new CollectionEntryDefinition { Id = 41, ItemDefinitionId = 11081 },
                new CollectionEntryDefinition { Id = 42, ItemDefinitionId = 11082 }
            ]
        };
        IReadOnlySet<int> ownedItems = new HashSet<int> { 11082 };

        var clientCollection = definition.CreateClientCollection(123, ownedItems);

        Assert.IsTrue(definition.IsStarted(ownedItems));
        Assert.IsFalse(clientCollection.Entries[0].Collected);
        Assert.IsTrue(clientCollection.Entries[1].Collected);
        Assert.AreEqual(123ul, clientCollection.PlayerGuid);
    }

    [TestMethod]
    public void IsComplete_OnlyWhenEveryEntryIsOwned()
    {
        var definition = TwoEntryDefinition();

        Assert.IsFalse(definition.IsComplete(new HashSet<int>()));
        Assert.IsFalse(definition.IsComplete(new HashSet<int> { 11081 }));
        Assert.IsTrue(definition.IsComplete(new HashSet<int> { 11081, 11082 }));

        // Owning unrelated items alongside the set doesn't change the verdict.
        Assert.IsTrue(definition.IsComplete(new HashSet<int> { 11081, 11082, 99999 }));
    }

    [TestMethod]
    public void IsComplete_IsFalseForAnEmptyCollection()
    {
        // An entry-less definition owns "all" of nothing, which List.All would call true - that would pay
        // a reward for a collection nobody could collect.
        var definition = new CollectionDefinition { Id = 1, CategoryId = 10 };

        Assert.IsFalse(definition.IsComplete(new HashSet<int>()));
    }

    [TestMethod]
    public void Contains_MatchesEntryItemDefinitionsOnly()
    {
        var definition = TwoEntryDefinition();

        Assert.IsTrue(definition.Contains(11082));
        Assert.IsFalse(definition.Contains(11083));

        // Entry ids are a different id space to item definition ids and must not match.
        Assert.IsFalse(definition.Contains(41));
    }

    [TestMethod]
    public void RewardDefaults_AreAdventurerAndTheCapturedPanelValue()
    {
        var definition = TwoEntryDefinition();

        Assert.AreEqual(1, definition.RewardProfileId);

        // RewardPoints is display-only and unconfirmed, so an untouched collection must still send the
        // value captured from the live server rather than anything derived from RewardXp.
        definition.RewardXp = 250;
        Assert.AreEqual(50, definition.CreateClientCollection(1, new HashSet<int>()).RewardPoints);

        definition.RewardPoints = 120;
        Assert.AreEqual(120, definition.CreateClientCollection(1, new HashSet<int>()).RewardPoints);
    }

    [TestMethod]
    public void CreateClientCollectionEntry_UsesCollectionMetadata()
    {
        var definition = new CollectionDefinition
        {
            Id = 17054,
            NameId = 17055,
            CategoryId = 10
        };
        var entry = new CollectionEntryDefinition
        {
            Id = 41,
            NameId = 17056,
            IconId = 2124,
            IconTintId = 99
        };

        var clientEntry = definition.CreateClientCollectionEntry(entry, 2, true);

        Assert.AreEqual(entry.Id, clientEntry.Id);
        Assert.AreEqual(entry.Id, clientEntry.DefinitionId);
        Assert.AreEqual(3, clientEntry.Index);
        Assert.AreEqual(definition.Id, clientEntry.CollectionId);
        Assert.AreEqual(entry.NameId, clientEntry.NameId);
        Assert.AreEqual(entry.IconId, clientEntry.IconId);
        Assert.AreEqual(entry.IconTintId, clientEntry.IconTintId);
        Assert.IsTrue(clientEntry.Collected);
    }

    private static CollectionDefinition TwoEntryDefinition() => new()
    {
        Id = 17054,
        NameId = 17055,
        CategoryId = 10,
        Entries =
        [
            new CollectionEntryDefinition { Id = 41, ItemDefinitionId = 11081 },
            new CollectionEntryDefinition { Id = 42, ItemDefinitionId = 11082 }
        ]
    };
}
