using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet.Common;

// The shared reward blob (client reader sub_8E7930 / FUN_008e7930). Carried standalone by
// RewardBundlePacket (50/1) and embedded in the quest offer (QuestInfoPacket), quest turn-in
// (QuestEndPacket), the encounter details response, MiniGameInfo and the loot-wheel packets.
//
// Ported from upstream (Open-Source-Free-Realms/Sanctuary) with three corrections our own reversing
// had already established - see the comments on CarriesItemGuids, Trailing, and Unknown3.
public sealed class RewardBundleBase : ISerializableType
{
    // The wire's LEAD BYTE. Upstream calls this "Success" and always leaves it true, which works for the
    // one shape upstream sends (a coin-shop grant). It is not a status flag: the client reader pushes it
    // into every entry, and it gates whether each one carries a trailing 4-byte inventory ItemGuid.
    // PREVIEW bundles (quest offers, prize lists, wheel slices) describe items the player does not own
    // yet and send false; real GRANTS send true and reference the row the player now holds.
    public bool CarriesItemGuids;

    // Upstream "Unknown1". IDA-verified as the RewardDataSource's "Num Coins" column.
    public int Coins;

    // Upstream "RewardKind". IDA-verified as the RewardDataSource's "Experience" column.
    public int Experience;

    public int Unknown2;

    // Upstream defaults this to 3; every live bundle we decoded carries 0 here. Left at upstream's
    // value deliberately so the difference is testable - flip to 0 if reward banners misbehave.
    public int Unknown3 = 3;

    public int Unknown4;
    public int Unknown5;

    // 1.0f in every live bundle.
    public float Multiplier = 1.0f;

    public int Unknown6;
    public int Unknown7;

    // Live bundles carry a session guid here; unread by the data sources we drive.
    public ulong SourceGuid;
    public ulong PlayerGuid;

    // Banner icon / name overrides. -1 means "defer to entry[0]" (client getter 0x1039D30), which is how
    // a zero-entry bundle still shows a prize. Upstream defaults these to 0; -1 is the working default
    // for our preview and wheel packets.
    public int IconId = -1;
    public int NameId = -1;

    public List<RewardBundleEntryBase> Entries { get; } = [];

    // The int that closes the bundle, AFTER the entry list. Upstream models this as a field on
    // RewardBundlePacket instead, which produces identical bytes for the standalone 50/1 packet but is
    // wrong for every EMBEDDED use (quest packets, MiniGameInfo, encounter details) - those would come
    // out 4 bytes short and desync the rest of the packet. Live wheel bundles carry 957.
    public int Trailing;

    public void Serialize(PacketWriter writer)
    {
        writer.Write(CarriesItemGuids);
        writer.Write(Coins);
        writer.Write(Experience);
        writer.Write(Unknown2);
        writer.Write(Unknown3);
        writer.Write(Unknown4);
        writer.Write(Unknown5);
        writer.Write(Multiplier);
        writer.Write(Unknown6);
        writer.Write(Unknown7);
        writer.Write(SourceGuid);
        writer.Write(PlayerGuid);
        writer.Write(IconId);
        writer.Write(NameId);
        writer.Write(Entries.Count);

        foreach (var entry in Entries)
            entry.Serialize(writer, CarriesItemGuids);

        writer.Write(Trailing);
    }
}
