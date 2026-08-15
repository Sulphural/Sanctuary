using System;
using System.Buffers.Binary;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Core.IO;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.UdpLibrary.Tests;

[TestClass]
public class HousingFixtureIdentityPacketTests
{
    [TestMethod]
    public void RuntimeKeyPrefersDatabaseFixtureIdAndUsesPendingGuidLowBits()
    {
        const ulong pendingFixtureGuid = 200_000_000_002;
        const int databaseFixtureId = 702;

        Assert.AreEqual(
            (uint)databaseFixtureId,
            HouseOwnershipService.GetFixtureRuntimeKey(pendingFixtureGuid, databaseFixtureId));
        Assert.AreEqual(
            unchecked((uint)pendingFixtureGuid),
            HouseOwnershipService.GetFixtureRuntimeKey(pendingFixtureGuid, 0));
        Assert.AreNotEqual(
            0u,
            HouseOwnershipService.GetFixtureRuntimeKey(1UL << 32, 0));
    }

    [TestMethod]
    public void InitialFixtureDictionarySerializesDatabaseRuntimeKeyAndActorLink()
    {
        const ulong fixtureGuid = 200_000_000_002;
        const ulong actorGuid = 100_000_000_702;
        const int databaseFixtureId = 702;
        var runtimeKey = HouseOwnershipService.GetFixtureRuntimeKey(fixtureGuid, databaseFixtureId);
        var instance = CreateFixtureInstance(fixtureGuid, actorGuid);

        var data = new HousingPacketInstanceData
        {
            InstanceData = new PlayerHousingInstanceData
            {
                OwnerName = string.Empty,
                Name = string.Empty,
                Unknown22 = string.Empty,
                Unknown23 = string.Empty,
                Fixtures =
                {
                    [runtimeKey] = instance
                }
            }
        }.Serialize();

        var reader = new PacketReader(data);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketInstanceData.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong _));
        Assert.IsTrue(reader.TryRead(out ulong _));
        Assert.IsTrue(reader.TryRead(out string? _));
        Assert.IsTrue(reader.TryRead(out long _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out string? _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out int fixtureCount));
        Assert.AreEqual(1, fixtureCount);
        Assert.IsTrue(reader.TryRead(out uint serializedRuntimeKey));
        Assert.AreEqual((uint)databaseFixtureId, serializedRuntimeKey);

        AssertFixtureIdentity(ref reader, fixtureGuid, actorGuid);
    }

    [TestMethod]
    public void FixtureUpdateSerializesPendingRuntimeKeyAndActorLink()
    {
        const ulong fixtureGuid = 200_000_000_002;
        const ulong actorGuid = 100_000_000_702;
        var runtimeKey = HouseOwnershipService.GetFixtureRuntimeKey(fixtureGuid, 0);
        var data = new HousingPacketFixtureUpdate
        {
            Instance = CreateFixtureInstance(fixtureGuid, actorGuid),
            Info = new FixtureInstanceInfo
            {
                FixtureGuid = fixtureGuid,
                ItemDefinitionId = 36579
            },
            Definition = new FixtureDefinition
            {
                Id = 36579,
                ItemDefinitionId = 36579,
                ModelId = 1234,
                Category = string.Empty,
                LuaCall = string.Empty
            },
            Unknown1 = unchecked((int)runtimeKey)
        }.Serialize();

        var reader = new PacketReader(data);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketFixtureUpdate.OpCode, subOpcode);
        AssertFixtureIdentity(ref reader, fixtureGuid, actorGuid);

        var trailingRuntimeFields = data.AsSpan(data.Length - 12);
        Assert.AreEqual(runtimeKey, BinaryPrimitives.ReadUInt32LittleEndian(trailingRuntimeFields));
        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(trailingRuntimeFields[4..]));
        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(trailingRuntimeFields[8..]));
    }

    private static FixtureInstance CreateFixtureInstance(ulong fixtureGuid, ulong actorGuid)
    {
        return new FixtureInstance
        {
            Guid = fixtureGuid,
            HouseGuid = 45,
            Id = 36579,
            Unknown5 = new Vector4(1f, 2f, 3f, 1f),
            Unknown6 = new Quaternion(0.25f, 0.5f, 0.75f, 0f),
            Unknown7 = Quaternion.Identity,
            Unknown8 = unchecked((long)actorGuid),
            CustomizationDetails = new CustomizationDetail
            {
                TextureAlias = string.Empty,
                TintAlias = string.Empty,
                TextureOverride = string.Empty
            },
            Unknown11 = string.Empty,
            Unknown12 = string.Empty,
            Unknown14 = string.Empty
        };
    }

    private static void AssertFixtureIdentity(
        ref PacketReader reader,
        ulong fixtureGuid,
        ulong actorGuid)
    {
        Assert.IsTrue(reader.TryRead(out ulong serializedFixtureGuid));
        Assert.AreEqual(fixtureGuid, serializedFixtureGuid);
        Assert.IsTrue(reader.TryRead(out ulong _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out Vector4 _));
        Assert.IsTrue(reader.TryRead(out Quaternion _));
        Assert.IsTrue(reader.TryRead(out Quaternion _));
        Assert.IsTrue(reader.TryRead(out long unknown8));
        Assert.AreEqual(unchecked((long)actorGuid), unknown8);
    }
}
