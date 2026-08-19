using System.Collections.Generic;
using System.Linq;

using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Resources.Definitions;

public sealed class CollectionDefinition
{
    // The Adventurer (Profiles.json id 1). Collections are the Adventurer job's XP source - the freestyle
    // job levels off completing collections and the main quest line - so a collection pays ITS OWN job
    // rather than whatever job the player happens to be wearing, and RewardProfileId only has to be set
    // for the handful of collections that belong to another job.
    public const int AdventurerProfileId = 1;

    public int Id { get; set; }
    public int NameId { get; set; }
    public int CategoryId { get; set; }
    public int DescriptionId { get; set; }
    public int IconId { get; set; }
    public int IconTintId { get; set; }
    public int HeaderMetadata { get; set; }
    public int RewardMetadata { get; set; }

    // Paid once, the first time the player owns every entry item. XP goes to RewardProfileId's job level,
    // not the active job.
    public int RewardXp { get; set; }
    public int RewardCoins { get; set; }
    public List<int> RewardItems { get; set; } = [];
    public int RewardProfileId { get; set; } = AdventurerProfileId;

    // What the PANEL advertises as the collection's reward, which is deliberately NOT wired to RewardXp:
    // ClientCollection.RewardPoints defaults to the 50 seen in the captured Briarwood row, and whether the
    // client renders that as XP, coins or collection points has never been confirmed live. 0 keeps the
    // captured default; set it only once the panel's meaning is known.
    public int RewardPoints { get; set; }

    public List<CollectionEntryDefinition> Entries { get; set; } = [];

    public bool IsStarted(IReadOnlySet<int> ownedItemDefinitionIds)
    {
        return Entries.Any(entry => ownedItemDefinitionIds.Contains(entry.ItemDefinitionId));
    }

    public bool IsComplete(IReadOnlySet<int> ownedItemDefinitionIds)
    {
        return Entries.Count > 0 && Entries.All(entry => ownedItemDefinitionIds.Contains(entry.ItemDefinitionId));
    }

    public bool Contains(int itemDefinitionId)
    {
        return Entries.Any(entry => entry.ItemDefinitionId == itemDefinitionId);
    }

    public ClientCollection CreateClientCollection(ulong playerGuid, IReadOnlySet<int> ownedItemDefinitionIds)
    {
        var collection = new ClientCollection
        {
            Id = Id,
            NameId = NameId,
            DescriptionId = DescriptionId,
            CategoryId = CategoryId,
            IconId = IconId,
            IconTintId = IconTintId,
            HeaderMetadata = HeaderMetadata,
            PlayerGuid = playerGuid,
            RewardMetadata = RewardMetadata
        };

        if (RewardPoints > 0)
            collection.RewardPoints = RewardPoints;

        for (var index = 0; index < Entries.Count; index++)
        {
            collection.Entries.Add(CreateClientCollectionEntry(
                Entries[index], index, ownedItemDefinitionIds.Contains(Entries[index].ItemDefinitionId)));
        }

        return collection;
    }

    public ClientCollectionEntry CreateClientCollectionEntry(CollectionEntryDefinition entry, int index, bool collected)
    {
        return new ClientCollectionEntry
        {
            Id = entry.Id,
            DefinitionId = entry.Id,
            Index = index + 1,
            CollectionId = Id,
            NameId = entry.NameId,
            IconId = entry.IconId,
            IconTintId = entry.IconTintId,
            Collected = collected
        };
    }
}
