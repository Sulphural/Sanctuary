using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Interactions;

public class OpenShopInteraction : IInteraction
{
    public int Id => Data.Id;

    public static InteractionData Data = new()
    {
        Id = IInteraction.UniqueId++,
        IconId = 0,
        ButtonText = 3413
    };

    private readonly IResourceManager _resourceManager;

    public OpenShopInteraction(IResourceManager resourceManager)
    {
        _resourceManager = resourceManager;
    }

    public void OnInteract(Player player, IEntity other)
    {
        if (other is not Npc npc || npc.VendorItems.Count == 0)
            return;

        var packet = new CoinStoreItemListPacket();

        foreach (var itemDefId in npc.VendorItems)
        {
            if (_resourceManager.CoinStoreItems.TryGetValue(itemDefId, out var meta))
            {
                packet.StaticItems[itemDefId] = meta;
            }
            else if (_resourceManager.ClientItemDefinitions.TryGetValue(itemDefId, out var def))
            {
                packet.StaticItems[itemDefId] = new ItemDefinitionMetaData
                {
                    Id = itemDefId,
                    CategoryId = def.CategoryId
                };
            }
        }

        player.SendTunneled(packet);
    }
}
