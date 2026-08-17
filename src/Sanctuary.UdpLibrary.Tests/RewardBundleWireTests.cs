using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Core.IO;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.UdpLibrary.Tests;

// Pins the RewardBundleBase wire layout. The bundle is embedded in the quest offer/turn-in packets,
// MiniGameInfo and the encounter details response, so any size drift here silently desyncs the REST of
// those packets rather than just corrupting the reward box - which is exactly the failure that is hard
// to spot in-game. These sizes are what the client's reader (sub_8E7930) consumes.
[TestClass]
public class RewardBundleWireTests
{
    // bool + 9 int32 + 2 ulong + icon + name + entryCount + trailing int32.
    private const int EmptyBundleSize = 69;

    // int32 type + bool + 6 int32 + empty string (int32 0) + int32 + bool.
    private const int EntrySizeWithoutItemGuid = 38;

    private static byte[] Serialize(RewardBundleBase bundle)
    {
        using var writer = new PacketWriter();
        bundle.Serialize(writer);
        return writer.Buffer;
    }

    [TestMethod]
    public void EmptyBundleIsSixtyNineBytes()
    {
        Assert.AreEqual(EmptyBundleSize, Serialize(new RewardBundleBase()).Length);
    }

    [TestMethod]
    public void PreviewEntryCarriesNoItemGuidTail()
    {
        // A preview describes items the player does not own yet, so the bundle's lead byte is clear and
        // the entry must NOT emit its ItemGuid - this is the case upstream's serializer got wrong.
        var bundle = new RewardBundleBase { CarriesItemGuids = false };
        bundle.Entries.Add(new RewardBundleEntryItem { ItemGuid = 12345 });

        Assert.AreEqual(EmptyBundleSize + EntrySizeWithoutItemGuid, Serialize(bundle).Length);
    }

    [TestMethod]
    public void GrantEntryCarriesItemGuidTail()
    {
        var bundle = new RewardBundleBase { CarriesItemGuids = true };
        bundle.Entries.Add(new RewardBundleEntryItem { ItemGuid = 12345 });

        Assert.AreEqual(EmptyBundleSize + EntrySizeWithoutItemGuid + sizeof(int), Serialize(bundle).Length);
    }

    [TestMethod]
    public void LeadByteGatesEveryEntry()
    {
        var bundle = new RewardBundleBase { CarriesItemGuids = true };
        bundle.Entries.Add(new RewardBundleEntryItem());
        bundle.Entries.Add(new RewardBundleEntryItem());
        bundle.Entries.Add(new RewardBundleEntryItem());

        // The flag is a property of the bundle, not of the entry: all three tails, or none.
        Assert.AreEqual(EmptyBundleSize + 3 * (EntrySizeWithoutItemGuid + sizeof(int)), Serialize(bundle).Length);
    }

    [TestMethod]
    public void TrailingIntClosesTheBundleAfterTheEntryList()
    {
        var bundle = new RewardBundleBase { Trailing = 957 };
        var buffer = Serialize(bundle);

        // Upstream models this int on RewardBundlePacket instead, which comes out 4 bytes short for the
        // embedded uses. It belongs to the bundle and must be the last thing written.
        Assert.AreEqual(957, BitConverter.ToInt32(buffer, buffer.Length - sizeof(int)));
    }

    [TestMethod]
    public void RewardBundlePacketPrefixesTheOpCodePair()
    {
        var buffer = new RewardBundlePacket().Serialize();

        Assert.AreEqual(50, BitConverter.ToInt16(buffer, 0));
        Assert.AreEqual(1, buffer[2]);
        Assert.AreEqual(sizeof(short) + sizeof(byte) + EmptyBundleSize, buffer.Length);
    }
}
