using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Zones;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.UdpLibrary;
using Sanctuary.UdpLibrary.Configuration;
using Sanctuary.UdpLibrary.Enumerations;

namespace Sanctuary.UdpLibrary.Tests;

[TestClass]
[DoNotParallelize]
public class HousingFixtureRehydrationTests
{
    [TestMethod]
    public void ClientReadyReplayRebindsInteractiveFixtureToRecipientOnlyInPacketOrder()
    {
        const int houseId = 45;
        const int databaseFixtureId = 702;
        const int itemDefinitionId = 36579;
        const ulong playerGuid = 0x7fff000000070201;
        const ulong otherPlayerGuid = 0x7fff000000070202;

        var udpParams = new UdpParams(ManagerRole.ExternalServer)
        {
            BindIpAddress = "127.0.0.1",
            Port = 0
        };

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        using var manager = new TestManager(true, udpParams, serviceProvider);

        var resourceManager = new ResourceManager(NullLogger<ResourceManager>.Instance);
        Assert.IsTrue(resourceManager.ClientItemDefinitions.TryAdd(
            itemDefinitionId,
            new ClientItemDefinition
            {
                Id = itemDefinitionId,
                Comment = "Fixture rehydration regression test",
                Type = 1,
                Param1 = 1234,
                ModelName = "fixture_rehydration_test_gumball_machine.adr",
                CategoryId = 57,
                TextureAlias = "fixture",
                TintAlias = "dyetint",
                MaxStackSize = -1
            }));

        var zone = new TestZone();
        var socketAddress = new IPEndPoint(IPAddress.Loopback, 12345).Serialize();
        var connection = new CaptureConnection(manager, socketAddress, connectCode: 1);
        var otherConnection = new CaptureConnection(manager, socketAddress, connectCode: 2);
        var houseGuid = GuidHelper.GetHouseGuid(houseId);
        var player = new Player(null!, connection, resourceManager)
        {
            Guid = playerGuid,
            Zone = zone,
            CurrentHouseGuid = houseGuid,
            Visible = true
        };
        var otherPlayer = new Player(null!, otherConnection, resourceManager)
        {
            Guid = otherPlayerGuid,
            Zone = zone,
            CurrentHouseGuid = houseGuid
        };
        Assert.IsTrue(zone.TryAddPlayer(player));
        Assert.IsTrue(zone.TryAddPlayer(otherPlayer));

        var fixture = new DbHouseFixture
        {
            Id = databaseFixtureId,
            HouseId = houseId,
            ItemDefinitionId = itemDefinitionId,
            PositionX = 12.5f,
            PositionY = 4.25f,
            PositionZ = -8.75f,
            PositionW = 1f,
            RotationX = 0.25f,
            RotationY = -0.5f,
            RotationZ = 0.75f,
            RotationW = 0f,
            Scale = 1.5f
        };
        var house = new DbHouse
        {
            Id = houseId,
            Fixtures = new HashSet<DbHouseFixture> { fixture }
        };
        fixture.House = house;

        var fixtureGuid = HousingFixtureActorService.GetClientFixtureGuid(
            player.Guid,
            house.Id,
            fixture.Id);
        var position = new Vector4(
            fixture.PositionX,
            fixture.PositionY,
            fixture.PositionZ,
            fixture.PositionW);
        var rotation = new Quaternion(
            fixture.RotationX,
            fixture.RotationY,
            fixture.RotationZ,
            fixture.RotationW);

        try
        {
            Assert.IsTrue(HousingFixtureActorService.TryEnsureActor(
                player,
                house.Id,
                fixtureGuid,
                fixture.ItemDefinitionId,
                tintId: 0,
                position,
                rotation,
                fixture.Scale,
                resourceManager,
                out var npcGuid));
            Assert.AreNotEqual(0UL, npcGuid);

            HousingFixtureActorService.Promote(
                player,
                house.Id,
                fixtureGuid,
                fixture.Id,
                position,
                rotation,
                fixture.Scale);

            connection.Clear();
            otherConnection.Clear();

            Assert.AreEqual(1, HousingFixtureActorService.ResendActors(player));
            Assert.AreEqual(
                1,
                HousingFixtureActorService.ReplayPersistedFixtureUpdates(
                    player,
                    house,
                    resourceManager));

            var payloads = connection.GetTunneledPayloads();
            var opcodes = payloads.Select(ReadOpcode).ToList();
            var removeActorIndex = opcodes.FindIndex(opcode =>
                opcode == (BasePlayerUpdatePacket.OpCode, PlayerUpdatePacketRemovePlayer.OpCode));
            var addNpcIndex = opcodes.FindIndex(opcode =>
                opcode == (BasePlayerUpdatePacket.OpCode, PlayerUpdatePacketAddNpc.OpCode));
            var relevanceIndex = opcodes.FindIndex(opcode =>
                opcode == (BasePlayerUpdatePacket.OpCode, PlayerUpdatePacketNpcRelevance.OpCode));
            var removeFixtureIndex = opcodes.FindIndex(opcode =>
                opcode == (BaseHousingPacket.OpCode, HousingPacketRemoveFixture.OpCode));
            var fixtureUpdateIndex = opcodes.FindIndex(opcode =>
                opcode == (BaseHousingPacket.OpCode, HousingPacketFixtureUpdate.OpCode));
            var fixtureAssetIndex = opcodes.FindIndex(opcode =>
                opcode == (BaseHousingPacket.OpCode, HousingPacketFixtureAsset.OpCode));
            var fixturePositionIndex = opcodes.FindIndex(opcode =>
                opcode == (BaseHousingPacket.OpCode, HousingPacketUpdateFixturePosition.OpCode));

            Assert.IsGreaterThanOrEqualTo(0, removeActorIndex);
            Assert.IsGreaterThan(removeActorIndex, addNpcIndex);
            Assert.IsGreaterThan(addNpcIndex, relevanceIndex);
            Assert.IsGreaterThan(addNpcIndex, removeFixtureIndex);
            Assert.IsGreaterThan(removeFixtureIndex, fixtureUpdateIndex);
            Assert.AreEqual(-1, fixtureAssetIndex);
            Assert.IsGreaterThan(fixtureUpdateIndex, fixturePositionIndex);

            AssertRemoveActorIdentity(payloads[removeActorIndex], npcGuid);
            AssertAddNpcIdentity(payloads[addNpcIndex], npcGuid);
            AssertRelevanceIdentity(payloads[relevanceIndex], npcGuid);
            AssertRemoveFixtureIdentity(payloads[removeFixtureIndex], fixtureGuid);
            AssertFixtureUpdateIdentity(
                payloads[fixtureUpdateIndex],
                fixtureGuid,
                npcGuid,
                databaseFixtureId);
            AssertFixturePosition(
                payloads[fixturePositionIndex],
                npcGuid,
                position,
                HousingFixtureActorService.ToHousingRotation(rotation));

            Assert.IsTrue(zone.TryGetNpc(npcGuid, out var npc));
            Assert.IsTrue(npc.CollisionEnabled);
            CollectionAssert.AreEqual(
                HousingFixtureActorService.CreateFixtureAddNpcPacket(npc).Serialize(),
                payloads[addNpcIndex]);

            Assert.IsEmpty(otherConnection.SentPackets);
        }
        finally
        {
            HousingFixtureActorService.RemoveAllForPlayer(player);
            HousingFixtureActorService.RemoveAllForPlayer(otherPlayer);
        }
    }

    [TestMethod]
    public void PersistedBuildingPieceUsesPhysicsBackedActorDuringReplay()
    {
        const int houseId = 45;
        const int databaseFixtureId = 703;
        const int itemDefinitionId = 36456;
        const ulong playerGuid = 0x7fff000000070203;

        var udpParams = new UdpParams(ManagerRole.ExternalServer)
        {
            BindIpAddress = "127.0.0.1",
            Port = 0
        };

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        using var manager = new TestManager(true, udpParams, serviceProvider);

        var resourceManager = new ResourceManager(NullLogger<ResourceManager>.Instance);
        Assert.IsTrue(resourceManager.ClientItemDefinitions.TryAdd(
            itemDefinitionId,
            new ClientItemDefinition
            {
                Id = itemDefinitionId,
                Comment = "Native housing block regression test",
                Type = 1,
                Param1 = 1983,
                ModelName = "hsg_block_03.adr",
                CategoryId = 147,
                TextureAlias = string.Empty,
                TintAlias = string.Empty,
                MaxStackSize = -1
            }));

        var zone = new TestZone();
        var socketAddress = new IPEndPoint(IPAddress.Loopback, 12345).Serialize();
        var connection = new CaptureConnection(manager, socketAddress, connectCode: 3);
        var player = new Player(null!, connection, resourceManager)
        {
            Guid = playerGuid,
            Zone = zone,
            CurrentHouseGuid = GuidHelper.GetHouseGuid(houseId),
            Visible = true
        };
        Assert.IsTrue(zone.TryAddPlayer(player));

        var fixture = new DbHouseFixture
        {
            Id = databaseFixtureId,
            HouseId = houseId,
            ItemDefinitionId = itemDefinitionId,
            PositionW = 1f,
            RotationW = 1f,
            Scale = 1f
        };
        var house = new DbHouse
        {
            Id = houseId,
            Fixtures = new HashSet<DbHouseFixture> { fixture }
        };
        fixture.House = house;

        var fixtureGuid = HousingFixtureActorService.GetClientFixtureGuid(
            player.Guid,
            house.Id,
            fixture.Id);

        try
        {
            Assert.IsEmpty(zone.Npcs);

            connection.Clear();
            Assert.AreEqual(
                1,
                HousingFixtureActorService.ReplayPersistedFixtureUpdates(
                    player,
                    house,
                    resourceManager));
            Assert.HasCount(1, zone.Npcs);

            var payloads = connection.GetTunneledPayloads();
            var opcodes = payloads.Select(ReadOpcode).ToList();
            var removeFixtureIndex = opcodes.FindIndex(opcode =>
                opcode == (BaseHousingPacket.OpCode, HousingPacketRemoveFixture.OpCode));
            var fixtureUpdateIndex = opcodes.FindIndex(opcode =>
                opcode == (BaseHousingPacket.OpCode, HousingPacketFixtureUpdate.OpCode));
            var fixtureAssetIndex = opcodes.FindIndex(opcode =>
                opcode == (BaseHousingPacket.OpCode, HousingPacketFixtureAsset.OpCode));
            var addNpcIndex = opcodes.FindIndex(opcode =>
                opcode == (BasePlayerUpdatePacket.OpCode, PlayerUpdatePacketAddNpc.OpCode));

            Assert.IsGreaterThanOrEqualTo(0, addNpcIndex);
            Assert.IsGreaterThanOrEqualTo(0, removeFixtureIndex);
            Assert.IsGreaterThan(addNpcIndex, removeFixtureIndex);
            Assert.IsGreaterThan(removeFixtureIndex, fixtureUpdateIndex);
            Assert.AreEqual(-1, fixtureAssetIndex);

            var npc = zone.Npcs.Single();
            var addNpc = HousingFixtureActorService.CreateFixtureAddNpcPacket(npc);
            Assert.AreEqual(0, addNpc.MovementType);
            Assert.IsTrue(addNpc.IsInteractable);
            Assert.IsTrue(addNpc.Unknown42);
            AssertAddNpcIdentity(payloads[addNpcIndex], npc.Guid);
            AssertRemoveFixtureIdentity(payloads[removeFixtureIndex], fixtureGuid);
            AssertFixtureUpdateIdentity(
                payloads[fixtureUpdateIndex],
                fixtureGuid,
                npc.Guid,
                databaseFixtureId);
        }
        finally
        {
            HousingFixtureActorService.RemoveAllForPlayer(player);
        }
    }

    [TestMethod]
    [DataRow(
        16872,
        3090,
        "hsg_pool_basic_01.adr",
        3043,
        "hsg_pool_basic_water_01.adr",
        "fun-poolbasicwater-L1")]
    [DataRow(
        10451,
        3334,
        "hsg_vip_party_pool_01.adr",
        3336,
        "hsg_vip_party_water_01.adr",
        "vip-poolwater-L1")]
    public void PoolFixtureCreatesSynchronizedWaterCompanion(
        int itemDefinitionId,
        int poolModelId,
        string poolModelName,
        int waterModelId,
        string waterModelName,
        string waterTextureAlias)
    {
        const int houseId = 46;
        const ulong playerGuid = 0x7fff000000070204;

        var udpParams = new UdpParams(ManagerRole.ExternalServer)
        {
            BindIpAddress = "127.0.0.1",
            Port = 0
        };

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        using var manager = new TestManager(true, udpParams, serviceProvider);

        var resourceManager = new ResourceManager(NullLogger<ResourceManager>.Instance);
        Assert.IsTrue(resourceManager.ClientItemDefinitions.TryAdd(
            itemDefinitionId,
            new ClientItemDefinition
            {
                Id = itemDefinitionId,
                Comment = "Pool water companion regression test",
                Type = 1,
                ModelName = poolModelName.Replace(".adr", ".agr", StringComparison.Ordinal),
                CategoryId = 57,
                TextureAlias = "pool-shell",
                TintAlias = "dyetint",
                MaxStackSize = -1
            }));
        Assert.IsTrue(resourceManager.Models.TryAdd(
            poolModelId,
            new ModelDefinition
            {
                Id = poolModelId,
                ModelFileName = poolModelName
            }));
        Assert.IsTrue(resourceManager.Models.TryAdd(
            waterModelId,
            new ModelDefinition
            {
                Id = waterModelId,
                ModelFileName = waterModelName
            }));

        var zone = new TestZone();
        var socketAddress = new IPEndPoint(IPAddress.Loopback, 12345).Serialize();
        var connection = new CaptureConnection(manager, socketAddress, connectCode: itemDefinitionId);
        var player = new Player(null!, connection, resourceManager)
        {
            Guid = playerGuid,
            Zone = zone,
            CurrentHouseGuid = GuidHelper.GetHouseGuid(houseId)
        };
        Assert.IsTrue(zone.TryAddPlayer(player));

        var fixtureGuid = (ulong)itemDefinitionId;
        var position = new Vector4(14.5f, 2.25f, -9.75f, 1f);
        var rotation = new Quaternion(0.35f, 0f, 0f, 0f);

        try
        {
            Assert.IsTrue(HousingFixtureActorService.TryEnsureActor(
                player,
                houseId,
                fixtureGuid,
                itemDefinitionId,
                tintId: 233,
                position,
                rotation,
                scale: 1.25f,
                resourceManager,
                out var poolNpcGuid));

            Assert.HasCount(2, zone.Npcs);
            var pool = zone.Npcs.Single(candidate => candidate.Guid == poolNpcGuid);
            var water = zone.Npcs.Single(candidate => candidate.Guid != poolNpcGuid);
            Assert.AreEqual(poolModelId, pool.ModelId);
            Assert.AreEqual(waterModelId, water.ModelId);
            Assert.AreEqual(waterTextureAlias, water.TextureAlias);
            Assert.AreEqual("dyetint", water.TintAlias);
            Assert.AreEqual(233, water.TintId);
            Assert.AreEqual(position, water.Position);
            Assert.AreEqual(HousingFixtureActorService.ToActorRotation(rotation), water.Rotation);
            Assert.AreEqual(1.25f, water.Scale);
            Assert.IsFalse(water.CollisionEnabled);
            Assert.IsFalse(water.IsInteractable);
            Assert.AreEqual(0, water.InteractRange);

            var movedPosition = new Vector4(-4f, 7.5f, 22f, 1f);
            var movedRotation = new Quaternion(-0.7f, 0f, 0f, 0f);
            Assert.IsTrue(HousingFixtureActorService.TryEnsureActor(
                player,
                houseId,
                fixtureGuid,
                itemDefinitionId,
                tintId: 234,
                movedPosition,
                movedRotation,
                scale: 0.75f,
                resourceManager,
                out var movedPoolNpcGuid));

            Assert.AreEqual(poolNpcGuid, movedPoolNpcGuid);
            Assert.HasCount(2, zone.Npcs);
            Assert.AreEqual(movedPosition, water.Position);
            Assert.AreEqual(HousingFixtureActorService.ToActorRotation(movedRotation), water.Rotation);
            Assert.AreEqual(0.75f, water.Scale);
            Assert.AreEqual(234, water.TintId);
            Assert.AreEqual(2, HousingFixtureActorService.ResendActors(player));
        }
        finally
        {
            HousingFixtureActorService.RemoveAllForPlayer(player);
            Assert.IsEmpty(zone.Npcs);
        }
    }

    private static (short BaseOpcode, short SubOpcode) ReadOpcode(byte[] payload)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));

        if (baseOpcode is not BasePlayerUpdatePacket.OpCode and not BaseHousingPacket.OpCode)
            return (baseOpcode, short.MinValue);

        Assert.IsTrue(reader.TryRead(out short subOpcode));
        return (baseOpcode, subOpcode);
    }

    private static void AssertAddNpcIdentity(byte[] payload, ulong npcGuid)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BasePlayerUpdatePacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(PlayerUpdatePacketAddNpc.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong serializedNpcGuid));
        Assert.AreEqual(npcGuid, serializedNpcGuid);
    }

    private static void AssertRemoveActorIdentity(byte[] payload, ulong npcGuid)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BasePlayerUpdatePacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(PlayerUpdatePacketRemovePlayer.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out short removeSubOpcode));
        Assert.AreEqual(0, removeSubOpcode);
        Assert.IsTrue(reader.TryRead(out ulong serializedNpcGuid));
        Assert.AreEqual(npcGuid, serializedNpcGuid);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    private static void AssertRelevanceIdentity(byte[] payload, ulong npcGuid)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BasePlayerUpdatePacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(PlayerUpdatePacketNpcRelevance.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out int count));
        Assert.AreEqual(1, count);
        Assert.IsTrue(reader.TryRead(out ulong serializedNpcGuid));
        Assert.AreEqual(npcGuid, serializedNpcGuid);
    }

    private static void AssertFixtureUpdateIdentity(
        byte[] payload,
        ulong fixtureGuid,
        ulong npcGuid,
        int databaseFixtureId)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketFixtureUpdate.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong serializedFixtureGuid));
        Assert.AreEqual(fixtureGuid, serializedFixtureGuid);
        Assert.IsTrue(reader.TryRead(out ulong _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out int _));
        Assert.IsTrue(reader.TryRead(out Vector4 _));
        Assert.IsTrue(reader.TryRead(out Quaternion _));
        Assert.IsTrue(reader.TryRead(out Quaternion _));
        Assert.IsTrue(reader.TryRead(out long actorLink));
        Assert.AreEqual(unchecked((long)npcGuid), actorLink);

        var trailingRuntimeFields = payload.AsSpan(payload.Length - 12);
        Assert.AreEqual(
            (uint)databaseFixtureId,
            BinaryPrimitives.ReadUInt32LittleEndian(trailingRuntimeFields));
    }

    private static void AssertRemoveFixtureIdentity(byte[] payload, ulong fixtureGuid)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketRemoveFixture.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong serializedFixtureGuid));
        Assert.AreEqual(fixtureGuid, serializedFixtureGuid);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    private static void AssertFixturePosition(
        byte[] payload,
        ulong npcGuid,
        Vector4 position,
        Quaternion rotation)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketUpdateFixturePosition.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong serializedNpcGuid));
        Assert.AreEqual(npcGuid, serializedNpcGuid);
        Assert.IsTrue(reader.TryRead(out Vector4 serializedPosition));
        Assert.AreEqual(position, serializedPosition);
        Assert.IsTrue(reader.TryRead(out Quaternion serializedRotation));
        Assert.AreEqual(rotation, serializedRotation);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    private static void AssertFixtureAsset(
        byte[] payload,
        int modelDefinitionId,
        int itemDefinitionId)
    {
        var reader = new PacketReader(payload);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketFixtureAsset.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out int serializedModelDefinitionId));
        Assert.AreEqual(modelDefinitionId, serializedModelDefinitionId);
        Assert.IsTrue(reader.TryRead(out int serializedItemDefinitionId));
        Assert.AreEqual(itemDefinitionId, serializedItemDefinitionId);
    }

    private sealed class CaptureConnection(
        TestManager manager,
        SocketAddress socketAddress,
        int connectCode)
        : UdpConnection(manager, socketAddress, connectCode)
    {
        public List<byte[]> SentPackets { get; } = [];

        public override bool Send(UdpChannel channel, Span<byte> data)
        {
            SentPackets.Add(data.ToArray());
            return true;
        }

        public void Clear()
        {
            SentPackets.Clear();
        }

        public List<byte[]> GetTunneledPayloads()
        {
            return SentPackets.Select(packet =>
            {
                Assert.IsTrue(PacketTunneledClientPacket.TryDeserialize(packet, out var tunneled));
                return tunneled.Payload;
            }).ToList();
        }
    }

    private sealed class TestZone : IZone
    {
        private readonly Dictionary<ulong, Npc> _npcs = [];
        private readonly Dictionary<ulong, Player> _players = [];
        private ulong _nextNpcGuid = 100_000_900_000;

        #region Unused IZone/IScriptZone surface
        // This fork's IZone carries combat, pathfinding and scripting members that the housing
        // rehydration path never touches. Stubbed so the fixture tests keep compiling against it.

        public Vector4 SpawnPosition => default;
        public Quaternion SpawnRotation => default;
        public float TickDeltaSeconds => 0f;

        public Sanctuary.Game.Pathfinding.Pathfinder<Sanctuary.Game.Pathfinding.MapNode>? Pathfinder => null;
        public Sanctuary.Game.Pathfinding.ObstacleMap? NavObstacles => null;
        public Sanctuary.Game.Pathfinding.WaypointGraph? NavGraph => null;

        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;

        public bool IsLineWalkable(Vector4 a, Vector4 b) => true;
        public List<Vector4>? TryFindPath(Vector4 start, Vector4 destination) => null;

        public void OnStart() { }
        public bool ReloadScript() => false;
        public void OnNpcDamaged(Player player, Npc npc) { }
        public void OnNpcKilled(Player player, Npc npc) { }
        public void OnPlayerKnockedOut(Player player) { }
        public void OnPlayerRespawn(Player player) { }
        public void RefreshPlayerCustomizations(Player player) { }
        public void SendNpcHealth(Player player, Npc npc) { }
        public void SendNpcRelevance(Player player, Npc npc) { }
        public void SummonCombatClones(Player summoner, int count, int lifetimeSeconds, Sanctuary.Game.Combat.CombatCloneConfig config) { }

        public bool TryAddPet(Pet pet) => false;
        public bool TryCreateCombatNpc([MaybeNullWhen(false)] out CombatNpc npc) { npc = null; return false; }
        public bool TryCreateEncounterEntryNpc([MaybeNullWhen(false)] out EncounterEntryNpc npc) { npc = null; return false; }
        public bool TryCreatePet(Player player, PetDefinition definition, [MaybeNullWhen(false)] out Pet pet) { pet = null; return false; }
        public bool TryCreateProjectileNpc([MaybeNullWhen(false)] out ProjectileNpc npc) { npc = null; return false; }

        public bool TrySpawnNpc(int npcId, ulong? npcGuid, float x, float y, float z, float heading) => false;
        public bool TrySpawnDungeonEntrance(int dungeonId, float x, float y, float z, float heading) => false;
        public bool TrySpawnGatheringNode(int nodeId, int modelId, string key, float x, float y, float z) => false;
        public bool TrySpawnQuestCollectible(ulong guid, float x, float y, float z) => false;
        public bool TrySpawnSnowballPile(float x, float y, float z, float heading) => false;
        public void AddSpawnPoint(float x, float y, float z) { }
        public void AddSpawnArea(float x, float y, float z, int radius) { }
        #endregion

        public int Id => 1;
        public int DefinitionId => 1;
        public string Name => "Housing fixture rehydration test";
        public IEnumerable<Npc> Npcs => _npcs.Values;
        public IEnumerable<Player> Players => _players.Values;

        public void OnClientIsReady(Player entity)
        {
        }

        public void OnClientFinishedLoading(Player entity)
        {
        }

        public bool TryGetNpc(ulong guid, [MaybeNullWhen(false)] out Npc npc)
        {
            return _npcs.TryGetValue(guid, out npc);
        }

        public bool TryGetPlayer(ulong guid, [MaybeNullWhen(false)] out Player player)
        {
            return _players.TryGetValue(guid, out player);
        }

        public bool TryGetEntity(ulong guid, [MaybeNullWhen(false)] out IEntity entity)
        {
            if (_npcs.TryGetValue(guid, out var npc))
            {
                entity = npc;
                return true;
            }

            if (_players.TryGetValue(guid, out var player))
            {
                entity = player;
                return true;
            }

            entity = null;
            return false;
        }

        public bool TryAddMount(Mount mount)
        {
            return _npcs.TryAdd(mount.Guid, mount);
        }

        public bool TryAddPlayer(Player player)
        {
            return _players.TryAdd(player.Guid, player);
        }

        public bool TryCreateNpc([MaybeNullWhen(false)] out Npc npc)
        {
            return TryCreateNpc(_nextNpcGuid++, out npc);
        }

        public bool TryCreateNpc(ulong guid, [MaybeNullWhen(false)] out Npc npc)
        {
            npc = new Npc(this)
            {
                Guid = guid
            };
            return _npcs.TryAdd(guid, npc);
        }

        public bool TryCreateNpc(
            NpcDefinition definition,
            [MaybeNullWhen(false)] out Npc npc)
        {
            return TryCreateNpc(out npc);
        }

        public bool TryCreateMount(
            Player rider,
            MountDefinition definition,
            [MaybeNullWhen(false)] out Mount mount)
        {
            mount = null;
            return false;
        }

        public bool TryCreatePlayer(
            ulong guid,
            UdpConnection connection,
            [MaybeNullWhen(false)] out Player player)
        {
            player = null;
            return false;
        }

        public bool TryRemoveNpc(ulong guid)
        {
            return _npcs.Remove(guid);
        }

        public bool TryRemovePlayer(ulong guid)
        {
            return _players.Remove(guid);
        }

        public ZoneTile GetTileFromPosition(Vector4 position)
        {
            return new ZoneTile(0, 0);
        }

        public void UpdateEntityZoneTile(IEntity entity, ZoneTile from, ZoneTile to)
        {
        }
    }
}
