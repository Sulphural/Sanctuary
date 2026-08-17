using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

public sealed class RewardBundleEntryItem : RewardBundleEntryBase
{
    public override RewardBundleEntryType Type => RewardBundleEntryType.Item;

    // The PLAYER'S INVENTORY item row id (not the definition id - that is DefinitionId on the base).
    // Only reaches the wire when the owning bundle sets CarriesItemGuids; a preview bundle describes
    // items the player does not own yet, so it has no row id to send.
    public int ItemGuid;

    protected override void SerializeData(PacketWriter writer, bool carriesItemGuid)
    {
        if (carriesItemGuid)
            writer.Write(ItemGuid);
    }
}
