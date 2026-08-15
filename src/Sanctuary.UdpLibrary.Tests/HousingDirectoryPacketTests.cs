using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Core.IO;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;

namespace Sanctuary.UdpLibrary.Tests;

[TestClass]
public class HousingDirectoryPacketTests
{
    [TestMethod]
    public void DataReplyWritesNativeRatingMapLayout()
    {
        var packet = new RatingPacketDataReply
        {
            Correlation = 1234,
            System = "Housing",
            TotalCount = 1
        };
        packet.Entries[7] = CreateEntry();

        var reader = new PacketReader(packet.Serialize());
        Assert.IsTrue(reader.TryRead(out short opCode));
        Assert.AreEqual(BaseRatingPacket.OpCode, opCode);
        Assert.IsTrue(reader.TryRead(out byte subOpCode));
        Assert.AreEqual(RatingPacketDataReply.SubOpCode, subOpCode);
        Assert.IsTrue(reader.TryRead(out ulong correlation));
        Assert.AreEqual((ulong)1234, correlation);
        Assert.IsTrue(reader.TryRead(out string? system));
        Assert.AreEqual("Housing", system);
        Assert.IsTrue(reader.TryRead(out int count));
        Assert.AreEqual(1, count);
        Assert.IsTrue(reader.TryRead(out int key));
        Assert.AreEqual(7, key);
        AssertRatingEntry(ref reader);
        Assert.IsTrue(reader.TryRead(out int totalCount));
        Assert.AreEqual(1, totalCount);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    [TestMethod]
    public void SearchReplyWritesNativeRatingListLayout()
    {
        var packet = new RatingPacketSearchReply
        {
            Correlation = 42,
            Query = "Raising",
            Entries = [CreateEntry()]
        };

        var reader = new PacketReader(packet.Serialize());
        Assert.IsTrue(reader.TryRead(out short opCode));
        Assert.AreEqual(BaseRatingPacket.OpCode, opCode);
        Assert.IsTrue(reader.TryRead(out byte subOpCode));
        Assert.AreEqual(RatingPacketSearchReply.SubOpCode, subOpCode);
        Assert.IsTrue(reader.TryRead(out ulong correlation));
        Assert.AreEqual((ulong)42, correlation);
        Assert.IsTrue(reader.TryRead(out string? query));
        Assert.AreEqual("Raising", query);
        Assert.IsTrue(reader.TryRead(out int count));
        Assert.AreEqual(1, count);
        AssertRatingEntry(ref reader);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    [TestMethod]
    public void CandidateReplyWritesPublishedAndVoteFlagsInClientOrder()
    {
        var packet = new RatingPacketCandidateInfoReply { Correlation = 77 };
        packet.Candidates.Add(new CandidateRatingInfo
        {
            CandidateId = "34",
            OwnerName = "Raising Kaines",
            Name = "Large Wilds House",
            Rating = 4.5f,
            Votes = 8,
            HasRating = true,
            CanVote = false
        });

        var reader = new PacketReader(packet.Serialize());
        Assert.IsTrue(reader.TryRead(out short opCode));
        Assert.AreEqual(BaseRatingPacket.OpCode, opCode);
        Assert.IsTrue(reader.TryRead(out byte subOpCode));
        Assert.AreEqual(RatingPacketCandidateInfoReply.SubOpCode, subOpCode);
        Assert.IsTrue(reader.TryRead(out ulong correlation));
        Assert.AreEqual((ulong)77, correlation);
        Assert.IsTrue(reader.TryRead(out int count));
        Assert.AreEqual(1, count);
        Assert.IsTrue(reader.TryRead(out string? candidate));
        Assert.AreEqual("34", candidate);
        Assert.IsTrue(reader.TryRead(out string? owner));
        Assert.AreEqual("Raising Kaines", owner);
        Assert.IsTrue(reader.TryRead(out string? name));
        Assert.AreEqual("Large Wilds House", name);
        Assert.IsTrue(reader.TryRead(out float rating));
        Assert.AreEqual(4.5f, rating);
        Assert.IsTrue(reader.TryRead(out int votes));
        Assert.AreEqual(8, votes);
        Assert.IsTrue(reader.TryRead(out bool hasRating));
        Assert.IsTrue(hasRating);
        Assert.IsTrue(reader.TryRead(out bool canVote));
        Assert.IsFalse(canVote);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    [TestMethod]
    public void FeaturedReplyWritesOneInlineRatingEntry()
    {
        var packet = new RatingPacketSendFeatured
        {
            Correlation = 99,
            System = "Housing",
            Entry = CreateEntry()
        };

        var reader = new PacketReader(packet.Serialize());
        Assert.IsTrue(reader.TryRead(out short opCode));
        Assert.AreEqual(BaseRatingPacket.OpCode, opCode);
        Assert.IsTrue(reader.TryRead(out byte subOpCode));
        Assert.AreEqual(RatingPacketSendFeatured.SubOpCode, subOpCode);
        Assert.IsTrue(reader.TryRead(out ulong correlation));
        Assert.AreEqual((ulong)99, correlation);
        Assert.IsTrue(reader.TryRead(out string? system));
        Assert.AreEqual("Housing", system);
        AssertRatingEntry(ref reader);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    [TestMethod]
    public void DirectorySnapshotsUseHousingBrowserAssetBasenames()
    {
        Assert.AreEqual("blackspore_lot", HouseOwnershipService.GetDirectorySnapshot(1));
        Assert.AreEqual("yacht_lot", HouseOwnershipService.GetDirectorySnapshot(2));
        Assert.AreEqual("apartment_home", HouseOwnershipService.GetDirectorySnapshot(24));
        Assert.AreEqual("largewilds_home", HouseOwnershipService.GetDirectorySnapshot(26));
        Assert.AreEqual("smallwilds_home", HouseOwnershipService.GetDirectorySnapshot(27));
        Assert.AreEqual("vipclub_lot", HouseOwnershipService.GetDirectorySnapshot(28));
        Assert.AreEqual("vipclub_lot", HouseOwnershipService.GetDirectorySnapshot(49));
        Assert.AreEqual("crystalmines_lot", HouseOwnershipService.GetDirectorySnapshot(30));
        Assert.AreEqual("briarwood_lot", HouseOwnershipService.GetDirectorySnapshot(33));
        Assert.AreEqual("wildrapids_lot", HouseOwnershipService.GetDirectorySnapshot(34));
        Assert.AreEqual("wildrapids_lot", HouseOwnershipService.GetDirectorySnapshot(88));
        Assert.AreEqual("placeholder", HouseOwnershipService.GetDirectorySnapshot(int.MaxValue));
    }

    [TestMethod]
    public void DirectoryCandidateIdsUseTypedHouseGuids()
    {
        Assert.AreEqual("34", HouseOwnershipService.GetDirectoryCandidateId(2));
    }

    private static RatingDataEntry CreateEntry()
    {
        return new RatingDataEntry
        {
            CandidateId = "34",
            OwnerName = "Raising Kaines",
            Name = "Large Wilds House",
            OwnerGuid = 225,
            Snapshot = "snapshot-key",
            Description = "A decorated Wilds home",
            Keywords = "wilds,large",
            Rating = 4.5f,
            Votes = 8
        };
    }

    private static void AssertRatingEntry(ref PacketReader reader)
    {
        Assert.IsTrue(reader.TryRead(out string? candidate));
        Assert.AreEqual("34", candidate);
        Assert.IsTrue(reader.TryRead(out string? owner));
        Assert.AreEqual("Raising Kaines", owner);
        Assert.IsTrue(reader.TryRead(out string? name));
        Assert.AreEqual("Large Wilds House", name);
        Assert.IsTrue(reader.TryRead(out ulong ownerGuid));
        Assert.AreEqual((ulong)225, ownerGuid);
        Assert.IsTrue(reader.TryRead(out string? snapshot));
        Assert.AreEqual("snapshot-key", snapshot);
        Assert.IsTrue(reader.TryRead(out string? keywords));
        Assert.AreEqual("wilds,large", keywords);
        Assert.IsTrue(reader.TryRead(out string? description));
        Assert.AreEqual("A decorated Wilds home", description);
        Assert.IsTrue(reader.TryRead(out float rating));
        Assert.AreEqual(4.5f, rating);
        Assert.IsTrue(reader.TryRead(out float votes));
        Assert.AreEqual(8f, votes);
    }
}
