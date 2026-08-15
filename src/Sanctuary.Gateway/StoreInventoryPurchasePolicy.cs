using Sanctuary.Gateway.Admin;
using Sanctuary.Packet.Common;

namespace Sanctuary.Gateway;

public static class StoreInventoryPurchasePolicy
{
    public static bool IsSupported(ClientItemDefinition itemDefinition)
    {
        return itemDefinition.Type is 1 or 12 ||
            HouseOwnershipService.IsFixtureInventoryItem(itemDefinition);
    }
}
