using System;
using System.Numerics;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Core.IO;
using Sanctuary.Database.Entities;
using Sanctuary.Game.Entities;
using Sanctuary.Gateway.Admin;
using Sanctuary.Gateway.Handlers;
using Sanctuary.Packet;

namespace Sanctuary.UdpLibrary.Tests;

[TestClass]
public class HousingPlacementPacketTests
{
    [TestMethod]
    public void PlaceFixtureReadsNativeClientLayout()
    {
        var position = new Vector4(10.5f, -20.25f, 30.75f, 1f);
        var rotation = Quaternion.CreateFromYawPitchRoll(0.25f, 0.5f, 0.75f);

        using var writer = new PacketWriter();
        writer.Write(BaseHousingPacket.OpCode);
        writer.Write(ClientHousingPacketPlaceFixture.OpCode);
        writer.Write(36579);
        writer.Write(position);
        writer.Write(rotation);
        writer.Write(1.25f);

        var data = writer.Buffer;
        Assert.AreEqual(44, data.Length);
        Assert.IsTrue(ClientHousingPacketPlaceFixture.TryDeserialize(data, out var packet));
        Assert.AreEqual(36579, packet.ItemDefinitionId);
        Assert.AreEqual(position, packet.Position);
        Assert.AreEqual(rotation, packet.Rotation);
        Assert.AreEqual(1.25f, packet.Scale);
    }

    [TestMethod]
    public void SaveFixtureReadsCapturedNativeClientLayout()
    {
        var data = Convert.FromHexString(
            "7F000500D002000000000000118609449A01C8423D7608440000803F" +
            "00000000000000000000000000000000000000000100000016000000" +
            "66756E2D72656374616E676C656368656573652D4C3107000000647965" +
            "74696E7400000000000000000000803F");

        Assert.AreEqual(101, data.Length);
        Assert.IsTrue(ClientHousingPacketSaveFixture.TryDeserialize(data, out var packet));
        Assert.AreEqual((ulong)720, packet.FixtureGuid);
        Assert.AreEqual(550.0948f, packet.Position.X, 0.0001f);
        Assert.AreEqual(100.0031f, packet.Position.Y, 0.0001f);
        Assert.AreEqual(545.8475f, packet.Position.Z, 0.0001f);
        Assert.AreEqual(1f, packet.Position.W);
        Assert.AreEqual(new Quaternion(0f, 0f, 0f, 0f), packet.Rotation);
        Assert.AreEqual(0f, packet.Unknown);
        Assert.AreEqual(1, packet.Customization.Type);
        Assert.AreEqual("fun-rectanglecheese-L1", packet.Customization.TextureAlias);
        Assert.AreEqual("dyetint", packet.Customization.TintAlias);
        Assert.AreEqual(0, packet.Customization.TintId);
        Assert.AreEqual(string.Empty, packet.Customization.TextureOverride);
        Assert.AreEqual(1f, packet.Scale);
    }

    [TestMethod]
    public void SaveFixtureRetainsLegacyCompactLayout()
    {
        var position = new Vector4(12.5f, 4.25f, -8.75f, 1f);
        var rotation = new Quaternion(0.25f, -0.5f, 0.75f, 0f);

        using var writer = new PacketWriter();
        writer.Write(BaseHousingPacket.OpCode);
        writer.Write(ClientHousingPacketSaveFixture.OpCode);
        writer.Write((ulong)987654321);
        writer.Write(position);
        writer.Write(rotation);
        writer.Write(1.5f);

        Assert.AreEqual(48, writer.Buffer.Length);
        Assert.IsTrue(ClientHousingPacketSaveFixture.TryDeserialize(writer.Buffer, out var packet));
        Assert.AreEqual((ulong)987654321, packet.FixtureGuid);
        Assert.AreEqual(position, packet.Position);
        Assert.AreEqual(rotation, packet.Rotation);
        Assert.AreEqual(1.5f, packet.Scale);
    }

    [TestMethod]
    public void SaveFixtureReadsCapturedLongCustomizationAlias()
    {
        var data = Convert.FromHexString(
            "7F000500D4020000000000000060064411DEC74200E003440000803F" +
            "0000000000000000000000000000000000000000010000001F000000" +
            "66756E2D626C6F636B666C617472656374616E676C656368656573652D" +
            "4C310700000064796574696E7400000000000000000000803F");

        Assert.AreEqual(110, data.Length);
        Assert.IsTrue(ClientHousingPacketSaveFixture.TryDeserialize(data, out var packet));
        Assert.AreEqual((ulong)724, packet.FixtureGuid);
        Assert.AreEqual("fun-blockflatrectanglecheese-L1", packet.Customization.TextureAlias);
        Assert.AreEqual("dyetint", packet.Customization.TintAlias);
        Assert.AreEqual(1f, packet.Scale);
    }

    [TestMethod]
    public void HousingNpcVisibilityRequiresTheSameWorldOrHouseInstance()
    {
        var player = new Player(null!, null!, null!);
        var worldNpc = new Npc(null!) { CurrentHouseGuid = 0 };
        var sameHouseNpc = new Npc(null!) { CurrentHouseGuid = 1234 };
        var otherHouseNpc = new Npc(null!) { CurrentHouseGuid = 5678 };

        Assert.IsTrue(player.CanSeeNpc(worldNpc));
        Assert.IsFalse(player.CanSeeNpc(sameHouseNpc));

        player.CurrentHouseGuid = 1234;

        Assert.IsFalse(player.CanSeeNpc(worldNpc));
        Assert.IsTrue(player.CanSeeNpc(sameHouseNpc));
        Assert.IsFalse(player.CanSeeNpc(otherHouseNpc));
    }

    [TestMethod]
    public void FriendHomeTeleportWaitsForTheTargetToFinishLoading()
    {
        var target = new Player(null!, null!, null!)
        {
            CurrentHouseGuid = 1234,
            Visible = false
        };

        Assert.IsFalse(PacketWorldTeleportRequestHandler.IsHouseTeleportTargetReady(target));

        target.Visible = true;
        Assert.IsTrue(PacketWorldTeleportRequestHandler.IsHouseTeleportTargetReady(target));

        target.CurrentHouseGuid = 0;
        Assert.IsFalse(PacketWorldTeleportRequestHandler.IsHouseTeleportTargetReady(target));
    }

    [TestMethod]
    public void HousingActorsInheritThePlayersActiveHouseInstance()
    {
        var player = new Player(null!, null!, null!)
        {
            CurrentHouseGuid = 1234
        };
        var housingActor = new Npc(null!);

        Assert.IsFalse(player.CanSeeNpc(housingActor));

        HousingFixtureActorService.BindHouseInstance(player, housingActor);

        Assert.AreEqual(player.CurrentHouseGuid, housingActor.CurrentHouseGuid);
        Assert.IsTrue(player.CanSeeNpc(housingActor));
    }

    [TestMethod]
    public void PlaceFixtureCommandWritesRequestedTransform()
    {
        var position = new Vector4(12.5f, 4.25f, -8.75f, 1f);
        var rotation = new Quaternion(MathF.PI, -MathF.PI / 2f, 0.625f, 0f);
        var data = new HousingPacketPlaceFixture
        {
            FixtureGuid = 987654321,
            ItemDefinitionId = 36579,
            Position = position,
            Rotation = rotation,
            Scale = 1.5f
        }.Serialize();

        var reader = new PacketReader(data);
        Assert.AreEqual(52, data.Length);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketPlaceFixture.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong fixtureGuid));
        Assert.AreEqual((ulong)987654321, fixtureGuid);
        Assert.IsTrue(reader.TryRead(out int itemDefinitionId));
        Assert.AreEqual(36579, itemDefinitionId);
        Assert.IsTrue(reader.TryRead(out Vector4 writtenPosition));
        Assert.AreEqual(position, writtenPosition);
        Assert.IsTrue(reader.TryRead(out Quaternion writtenRotation));
        Assert.AreEqual(rotation, writtenRotation);
        Assert.IsTrue(reader.TryRead(out float scale));
        Assert.AreEqual(1.5f, scale);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    [TestMethod]
    public void UpdateFixturePositionWritesNativeClientLayout()
    {
        var position = new Vector4(12.5f, 4.25f, -8.75f, 1f);
        var rotation = new Quaternion(MathF.PI / 2f, 0.25f, -0.5f, 0f);
        var data = new HousingPacketUpdateFixturePosition
        {
            FixtureActorGuid = 100_000_000_123,
            Position = position,
            Rotation = rotation
        }.Serialize();

        var reader = new PacketReader(data);
        Assert.AreEqual(44, data.Length);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketUpdateFixturePosition.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong fixtureActorGuid));
        Assert.AreEqual((ulong)100_000_000_123, fixtureActorGuid);
        Assert.IsTrue(reader.TryRead(out Vector4 writtenPosition));
        Assert.AreEqual(position, writtenPosition);
        Assert.IsTrue(reader.TryRead(out Quaternion writtenRotation));
        Assert.AreEqual(rotation, writtenRotation);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    [TestMethod]
    public void SetCollidableWritesNativeClientLayout()
    {
        const ulong actorGuid = 100_000_000_123;
        var data = new PlayerUpdatePacketSetCollidable
        {
            Guid = actorGuid,
            Collidable = true
        }.Serialize();

        var reader = new PacketReader(data);
        Assert.AreEqual(13, data.Length);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BasePlayerUpdatePacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(PlayerUpdatePacketSetCollidable.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out ulong writtenGuid));
        Assert.AreEqual(actorGuid, writtenGuid);
        Assert.IsTrue(reader.TryRead(out bool collidable));
        Assert.IsTrue(collidable);
        Assert.AreEqual(0, reader.RemainingLength);
    }

    [TestMethod]
    public void HousingZoneDataKeepsTheNormalHeadSize()
    {
        var data = new HousingPacketZoneData
        {
            IsPreview = false,
            HeadSize = 10
        }.Serialize();

        var reader = new PacketReader(data);
        Assert.IsTrue(reader.TryRead(out short baseOpcode));
        Assert.AreEqual(BaseHousingPacket.OpCode, baseOpcode);
        Assert.IsTrue(reader.TryRead(out short subOpcode));
        Assert.AreEqual(HousingPacketZoneData.OpCode, subOpcode);
        Assert.IsTrue(reader.TryRead(out bool isPreview));
        Assert.IsFalse(isPreview);
        Assert.IsTrue(reader.TryRead(out bool unused));
        Assert.IsFalse(unused);
        Assert.IsTrue(reader.TryRead(out int headSize));
        Assert.AreEqual(10, headSize);
    }

    [TestMethod]
    public void StartingAnotherPreviewClearsStalePlacementForHouse()
    {
        const ulong playerGuid = 0x7fff000000000001;
        const int houseId = 700001;

        var stale = HousingPlacementSession.Start(playerGuid, houseId, 27943, 900001, 0);
        var current = HousingPlacementSession.Start(playerGuid, houseId, 36579, 900002, 7);

        Assert.AreNotEqual((ulong)900001, stale.FixtureGuid);
        Assert.AreNotEqual((ulong)900002, current.FixtureGuid);
        Assert.AreNotEqual(stale.FixtureGuid, current.FixtureGuid);
        Assert.IsFalse(HousingPlacementSession.TryGet(playerGuid, houseId, 27943, out _));
        Assert.IsTrue(HousingPlacementSession.TryGet(playerGuid, houseId, 36579, out var found));
        Assert.AreEqual(current, found);
        Assert.IsTrue(HousingPlacementSession.TryActivateHover(
            playerGuid,
            houseId,
            current.FixtureGuid,
            out var hoverActive));
        Assert.IsTrue(hoverActive.HoverActive);
        Assert.IsFalse(HousingPlacementSession.TryActivateHover(
            playerGuid,
            houseId,
            current.FixtureGuid,
            out var alreadyActive));
        Assert.AreEqual(hoverActive, alreadyActive);
        Assert.IsTrue(HousingPlacementSession.TrySetNpcGuid(
            playerGuid,
            houseId,
            current.FixtureGuid,
            100_000_000_123,
            out var actorLinked));
        Assert.AreEqual((ulong)100_000_000_123, actorLinked.NpcGuid);

        var removed = HousingPlacementSession.TakeAll(playerGuid, houseId);
        Assert.HasCount(1, removed);
        Assert.AreEqual(actorLinked, removed[0]);
        Assert.IsFalse(HousingPlacementSession.TryGet(playerGuid, houseId, 36579, out _));
    }

    [TestMethod]
    public void PlacementCanOnlyBeConsumedOnce()
    {
        const ulong playerGuid = 0x7fff000000000002;
        const int houseId = 700002;

        var placement = HousingPlacementSession.Start(playerGuid, houseId, 36676, 900003, 237);
        Assert.IsTrue(HousingPlacementSession.TryTake(
            playerGuid,
            houseId,
            placement.FixtureGuid,
            out var consumed));
        Assert.AreEqual(placement, consumed);
        Assert.IsFalse(HousingPlacementSession.TryTake(
            playerGuid,
            houseId,
            placement.FixtureGuid,
            out _));
        Assert.IsFalse(HousingPlacementSession.TryGet(playerGuid, houseId, 36676, out _));
    }

    [TestMethod]
    public void HousingRotationConvertsPlanarFacingToEulerAngles()
    {
        var housingRotation = HousingFixtureActorService.ToHousingRotation(
            new Quaternion(-1f, 0f, 0f, 0f));

        Assert.AreEqual(-MathF.PI / 2f, housingRotation.X, 0.00001f);
        Assert.AreEqual(0f, housingRotation.Y);
        Assert.AreEqual(0f, housingRotation.Z);
        Assert.AreEqual(0f, housingRotation.W);

        var nativeEuler = new Quaternion(0.2617994f, -0.5f, MathF.PI / 2f, 0f);
        Assert.AreEqual(
            nativeEuler,
            HousingFixtureActorService.ToHousingRotation(nativeEuler));
        Assert.AreEqual(
            new Quaternion(0f, 0f, 0f, 0f),
            HousingFixtureActorService.ToHousingRotation(Quaternion.Identity));
    }

    [TestMethod]
    public void HousingYawConvertsToSafePlanarActorRotation()
    {
        var actorRotation = HousingFixtureActorService.ToActorRotation(
            new Quaternion(MathF.PI / 2f, -1.25f, -MathF.PI / 2f, 0f));

        Assert.AreEqual(0f, actorRotation.Y);
        Assert.AreEqual(0f, actorRotation.W);
        Assert.AreEqual(1f, MathF.Sqrt(
            actorRotation.X * actorRotation.X +
            actorRotation.Z * actorRotation.Z), 0.00001f);
        Assert.AreEqual(1f, actorRotation.X, 0.00001f);
        Assert.AreEqual(0f, actorRotation.Z, 0.00001f);
        Assert.AreEqual(
            new Quaternion(0f, 0f, 1f, 0f),
            HousingFixtureActorService.ToActorRotation(Quaternion.Identity));
    }

    [TestMethod]
    public void PreviewActorsDisableCollisionUntilPromotion()
    {
        Assert.IsFalse(HousingFixtureActorService.ResolveFixtureCollisionEnabled(
            isPreview: true,
            isTeleporter: false));
        Assert.IsTrue(HousingFixtureActorService.ResolveFixtureCollisionEnabled(
            isPreview: false,
            isTeleporter: false));
        Assert.IsFalse(HousingFixtureActorService.ResolveFixtureCollisionEnabled(
            isPreview: false,
            isTeleporter: true));

        Assert.AreEqual(0, HousingFixtureActorService.ResolveFixtureInteractionRange(
            isPreview: true,
            inEditMode: true,
            supportsClickInteraction: true));
        Assert.AreEqual(100, HousingFixtureActorService.ResolveFixtureInteractionRange(
            isPreview: false,
            inEditMode: true,
            supportsClickInteraction: false));
        Assert.AreEqual(5, HousingFixtureActorService.ResolveFixtureInteractionRange(
            isPreview: false,
            inEditMode: false,
            supportsClickInteraction: true));
        Assert.AreEqual(0, HousingFixtureActorService.ResolveFixtureInteractionRange(
            isPreview: false,
            inEditMode: false,
            supportsClickInteraction: false));
    }

    [TestMethod]
    public void EditModeMakesPersistedFixturesSelectableWithoutWorldActions()
    {
        var preview = HousingFixtureActorService.ResolveInteractionState(
            isPreview: true,
            inEditMode: true,
            supportsClickInteraction: true);
        Assert.IsFalse(preview.IsInteractable);
        Assert.IsFalse(preview.SupportsWorldInteraction);

        var persisted = HousingFixtureActorService.ResolveInteractionState(
            isPreview: false,
            inEditMode: true,
            supportsClickInteraction: true);
        Assert.IsTrue(persisted.IsInteractable);
        Assert.IsFalse(persisted.SupportsWorldInteraction);

        var ordinaryPersisted = HousingFixtureActorService.ResolveInteractionState(
            isPreview: false,
            inEditMode: true,
            supportsClickInteraction: false);
        Assert.IsTrue(ordinaryPersisted.IsInteractable);
        Assert.IsFalse(ordinaryPersisted.SupportsWorldInteraction);

        var normalWorld = HousingFixtureActorService.ResolveInteractionState(
            isPreview: false,
            inEditMode: false,
            supportsClickInteraction: true);
        Assert.IsTrue(normalWorld.IsInteractable);
        Assert.IsTrue(normalWorld.SupportsWorldInteraction);

        var ordinaryNormalWorld = HousingFixtureActorService.ResolveInteractionState(
            isPreview: false,
            inEditMode: false,
            supportsClickInteraction: false);
        Assert.IsTrue(ordinaryNormalWorld.IsInteractable);
        Assert.IsFalse(ordinaryNormalWorld.SupportsWorldInteraction);
    }

    [TestMethod]
    public void FixtureActorPacketsKeepCollisionAndRelevanceSeparateFromGameplayInteraction()
    {
        var npc = new Npc(null!)
        {
            Guid = 100_000_000_321,
            InteractRange = HousingFixtureActorService.ResolveFixtureInteractionRange(
                isPreview: false,
                inEditMode: false,
                supportsClickInteraction: true),
            CollisionEnabled = true,
            IsInteractable = false,
            CursorId = 0
        };

        var genericAddNpc = npc.GetAddNpcPacket();
        Assert.IsTrue(genericAddNpc.Unknown42);

        var addNpc = HousingFixtureActorService.CreateFixtureAddNpcPacket(npc);
        Assert.AreEqual(5, addNpc.InteractRange);
        Assert.IsTrue(addNpc.Unknown42);
        Assert.IsFalse(addNpc.IsInteractable);

        npc.CollisionEnabled = false;
        var collisionDisabledAddNpc = HousingFixtureActorService.CreateFixtureAddNpcPacket(npc);
        Assert.IsFalse(collisionDisabledAddNpc.Unknown42);

        var relevance = HousingFixtureActorService.CreateInteractionRelevanceEntry(npc);
        Assert.AreEqual(npc.Guid, relevance.Guid);
        Assert.IsFalse(relevance.Unknown);
        Assert.AreEqual((byte)0, relevance.CursorId);
        Assert.IsFalse(relevance.HasCursor);

        npc.IsInteractable = true;
        var editorRelevance = HousingFixtureActorService.CreateInteractionRelevanceEntry(npc);
        Assert.IsFalse(editorRelevance.Unknown);
        Assert.AreEqual((byte)0, editorRelevance.CursorId);
        Assert.IsFalse(editorRelevance.HasCursor);

        npc.CursorId = 17;
        var worldActionRelevance = HousingFixtureActorService.CreateInteractionRelevanceEntry(npc);
        Assert.IsTrue(worldActionRelevance.Unknown);
        Assert.AreEqual((byte)17, worldActionRelevance.CursorId);
        Assert.IsFalse(worldActionRelevance.HasCursor);
    }

    [TestMethod]
    public void PickupAllAcceptsOnlyTheNativeHeaderOnlyPacket()
    {
        using var writer = new PacketWriter();
        writer.Write(BaseHousingPacket.OpCode);
        writer.Write(ClientHousingPacketPickupAllFixturesHandler.OpCode);

        Assert.IsTrue(ClientHousingPacketPickupAllFixturesHandler.TryDeserialize(writer.Buffer));

        writer.Write((byte)0);
        Assert.IsFalse(ClientHousingPacketPickupAllFixturesHandler.TryDeserialize(writer.Buffer));
    }

    [TestMethod]
    public void FixtureTintRoundTripsThroughPersistedAppearanceData()
    {
        var fixture = new DbHouseFixture
        {
            CustomizationData = "<legacy-customization />"
        };

        Assert.AreEqual(0, HouseOwnershipService.GetFixtureTintId(fixture));
        Assert.AreEqual("<legacy-customization />", fixture.CustomizationData);

        HouseOwnershipService.SetFixtureTintId(fixture, 237);
        Assert.AreEqual(237, HouseOwnershipService.GetFixtureTintId(fixture));
        StringAssert.Contains(fixture.CustomizationData, "237");

        HouseOwnershipService.SetFixtureTintId(fixture, 0);
        Assert.AreEqual(0, HouseOwnershipService.GetFixtureTintId(fixture));
        Assert.IsNull(fixture.CustomizationData);
    }

    [TestMethod]
    public void ApplyCustomizationReadsNativeClientLayout()
    {
        using var writer = new PacketWriter();
        writer.Write(BaseHousingPacket.OpCode);
        writer.Write(ClientHousingPacketApplyCustomizationToFixtureGroupAndType.OpCode);
        writer.Write(12345);
        writer.Write("walls");
        writer.Write("wallpaper");

        var data = writer.Buffer;
        Assert.IsTrue(ClientHousingPacketApplyCustomizationToFixtureGroupAndType.TryDeserialize(data, out var packet));
        Assert.AreEqual(12345, packet.ItemDefinitionId);
        Assert.AreEqual("walls", packet.FixtureGroup);
        Assert.AreEqual("wallpaper", packet.FixtureType);
    }

    [TestMethod]
    public void RemoveCustomizationReadsNativeClientLayout()
    {
        using var writer = new PacketWriter();
        writer.Write(BaseHousingPacket.OpCode);
        writer.Write(ClientHousingPacketRemoveCustomizationFromFixtureGroupAndType.OpCode);
        writer.Write("floors");
        writer.Write("floor");

        var data = writer.Buffer;
        Assert.IsTrue(ClientHousingPacketRemoveCustomizationFromFixtureGroupAndType.TryDeserialize(data, out var packet));
        Assert.AreEqual("floors", packet.FixtureGroup);
        Assert.AreEqual("floor", packet.FixtureType);
    }

    [TestMethod]
    public void HousingSurfaceCatalogResolvesCustomizationTexture()
    {
        Assert.AreEqual(
            "hsg_cust_wall_underwater_01.dds",
            HousingSurfaceCatalog.GetTextureOverride(27994));
        Assert.AreEqual(string.Empty, HousingSurfaceCatalog.GetTextureOverride(-1));
    }

    [TestMethod]
    public void HousingSurfaceCatalogTargetsSnowhillSurfacesByType()
    {
        CollectionAssert.AreEqual(
            new[] { 5038 },
            HousingSurfaceCatalog.GetTargetModelIds("hsg_hum_snowhill_01", "Roof").ToArray());
        CollectionAssert.AreEqual(
            new[] { 5039, 5046, 5047, 5048, 5049 },
            HousingSurfaceCatalog.GetTargetModelIds("hsg_hum_snowhill_01", "Wall").ToArray());
    }
}
