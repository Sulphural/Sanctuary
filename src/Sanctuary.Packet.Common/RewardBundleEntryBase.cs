using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

// One prize row inside a RewardBundle. The client surfaces these in several places, all fed from the same
// bundle: the quest offer / turn-in "Show Details" reward list, the NPC-talk offer popup's prize list
// (MinigameStartScreen reads "BaseClient.MiniGame.RewardPreview.Entries", up to 4 NON-hidden rows), and
// the victory score screen's loot-wheel slices (ScoreScreen:PopulateLootWheel, same data source).
// IsHidden rows are skipped by the popup list but still land in the data source and still render as
// wheel slices.
public abstract class RewardBundleEntryBase
{
    public abstract RewardBundleEntryType Type { get; }

    public bool IsHidden;
    public int IconId;          // @0x04 - the shown icon (ClientItemDefinition.Icon.Id)
    public int TintId;          // @0x08 - icon tint (ClientItemDefinition.Icon.TintId)
    public int NameId;          // @0x10 - name text id, resolved client-side
    public int Quantity = 1;    // @0x20 - a count of 1 hides the "xN" label (retail behaviour)
    public int DefinitionId;    // @0x24 - the ClientItemDefinitions item id ("Item Id" data-source column)
    public int Tint;            // @0x28
    public string Description = string.Empty;
    public int ItemTextColor;   // @0x3c - 0 = default
    public bool MembersOnly;    // @0x40

    // SERVER-SIDE ONLY (never written to the wire - the client resolves NameId itself). The item's plain
    // real name, used to build the blue "You receive 1 X" chat toast server-side, since
    // ClientItemDefinition carries no name field we could look up at runtime. See
    // BaseMiniGamePacketHandler.HandleLootWheelStopped and EncounterArenaZone.GrantBonusGoalReward.
    public string DisplayName = string.Empty;

    // carriesItemGuid comes from the bundle's lead byte - see RewardBundleBase.CarriesItemGuids. The
    // client reader pushes that flag into every entry, so whether an entry has a trailing guid is a
    // property of the BUNDLE, not of the entry.
    internal void Serialize(PacketWriter writer, bool carriesItemGuid)
    {
        writer.Write((int)Type);        // int32 on the wire - the client reads a full int
        writer.Write(IsHidden);
        writer.Write(IconId);
        writer.Write(TintId);
        writer.Write(NameId);
        writer.Write(Quantity);
        writer.Write(DefinitionId);
        writer.Write(Tint);
        writer.Write(Description);
        writer.Write(ItemTextColor);
        writer.Write(MembersOnly);

        SerializeData(writer, carriesItemGuid);
    }

    protected abstract void SerializeData(PacketWriter writer, bool carriesItemGuid);
}
