using Sanctuary.Game.Entities;

namespace Sanctuary.Game.Collections;

public interface ICollectionManager
{
    // Loads the character's already-paid collections into Player.CompletedCollections. Called once at
    // login, before any pickup can be processed.
    void LoadCompleted(Player player);

    // A collection item just landed in the player's inventory: pay out every collection that item just
    // finished. Returns true when at least one collection completed (the caller may want to refresh the
    // collections panel). Safe to call for any item id - non-collection items match nothing.
    bool OnItemCollected(Player player, int itemDefinitionId);

    // Pays out every unpaid collection the player already owns all of. The catch-all for entries that
    // arrived by a route with no completion hook - a quest reward, the coin store, /giveitem - since a
    // collection item is just an item and half a dozen places in the tree grant one. Called wherever the
    // collections panel is rebuilt, so at worst a payout lands on the next self-send instead of instantly.
    bool CheckAll(Player player);
}
