using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using NLog;

using Sanctuary.Core.Helpers;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;

namespace Sanctuary.Gateway.Admin;

public static class HousingFixtureActorService
{
    private readonly record struct FixtureActorKey(ulong PlayerGuid, int HouseId, ulong FixtureGuid);
    private readonly record struct FixtureActorMetadata(int ItemDefinitionId, FixtureInteractionKind Kind);
    private readonly record struct FixtureCompanionDefinition(
        string ModelName,
        string TextureAlias,
        string TintAlias);
    private readonly record struct FixtureRuntimeKey(int HouseId, int DatabaseFixtureId);

    private sealed class MovingPlatformState(
        Player recipient,
        int itemDefinitionId,
        Vector4 basePosition,
        Quaternion housingRotation,
        float scale)
    {
        public Player Recipient { get; } = recipient;
        public int ItemDefinitionId { get; } = itemDefinitionId;
        public Vector4 BasePosition { get; } = basePosition;
        public Quaternion HousingRotation { get; } = housingRotation;
        public float Scale { get; } = scale;
        public Vector4 LastPosition { get; set; } = basePosition;
    }

    private enum FixtureInteractionKind
    {
        None,
        Teleporter,
        TrainSet,
        GumballMachine,
        Fireworks,
        PartyPool,
        ElevatorPlatform,
        LaunchPad
    }

    private static readonly ConcurrentDictionary<FixtureActorKey, ulong> ActorGuids = new();
    private static readonly ConcurrentDictionary<FixtureActorKey, ulong> CompanionActorGuids = new();
    private static readonly ConcurrentDictionary<FixtureActorKey, int> DatabaseFixtureIds = new();
    private static readonly ConcurrentDictionary<FixtureActorKey, FixtureActorMetadata> ActorMetadata = new();
    private static readonly ConcurrentDictionary<FixtureActorKey, byte> PreviewActorKeys = new();
    private static readonly ConcurrentDictionary<ulong, DateTimeOffset> LastInteractionTimes = new();
    private static readonly ConcurrentDictionary<ulong, ulong> TeleporterLandingLocks = new();
    private static readonly ConcurrentDictionary<ulong, byte> ActiveTeleports = new();
    private static readonly ConcurrentDictionary<FixtureRuntimeKey, byte> ActiveAnimatedFixtures = new();
    private static readonly ConcurrentDictionary<ulong, byte> PlayersInEditMode = new();
    private static readonly ConcurrentDictionary<FixtureActorKey, MovingPlatformState> MovingPlatforms = new();
    private static readonly ConcurrentDictionary<ulong, byte> MovingPlatformRuntimePlayers = new();
    private static readonly IReadOnlyDictionary<int, FixtureCompanionDefinition> FixtureCompanionDefinitions =
        new Dictionary<int, FixtureCompanionDefinition>
        {
            [10451] = new("hsg_vip_party_water_01.adr", "vip-poolwater-L1", "dyetint"),
            [16872] = new("hsg_pool_basic_water_01.adr", "fun-poolbasicwater-L1", "dyetint"),
            [18135] = new("hsg_pool_basic_water_01.adr", "fun-poolbasicwater-L1", "dyetint")
        };
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly TimeSpan InteractionCooldown = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan TeleporterEffectDelay = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan TeleporterCloseAnimationDelay = TimeSpan.FromMilliseconds(850);
    // Housing fixture transforms are applied as direct kinematic positions by
    // the client, so a low cadence is visibly stepped. Sixty updates per second
    // keeps the linked model and collision body fluid without changing the
    // route timing or touching the general player-position broadcast path.
    private static readonly TimeSpan MovingPlatformUpdateInterval =
        TimeSpan.FromMilliseconds(1000d / 60d);
    private const float TeleporterActivationRadius = 3.5f;
    private const float TeleporterReleaseRadius = 5.0f;
    private const float LaunchPadActivationRadius = 2.4f;
    private const float LaunchPadVerticalTolerance = 2.25f;
    private const float LargeLaunchPadVelocity = 22.0f;
    private const int FixtureWorldInteractionRange = 5;
    private const int FixtureEditorSelectionRange = 100;
    private const int TeleporterOpenAnimationId = 2000;
    private const int TeleporterCloseAnimationId = 2001;
    private const int FixtureLoopAnimationId = 2100;
    private const int FixtureIdleAnimationId = 1;
    private const byte FixtureInteractionCursorId = 17;
    private const float MovingPlatformSpeed = 2.5f;
    private const float MovingPlatformEndpointHoldSeconds = 0.75f;

    static HousingFixtureActorService()
    {
        _ = Task.Run(MonitorMovingPlatformsAsync);
    }

    public static bool TryEnsureActor(
        GatewayConnection connection,
        int houseId,
        ulong fixtureGuid,
        int itemDefinitionId,
        int tintId,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager,
        out ulong npcGuid)
    {
        return TryEnsureActor(
            connection.Player,
            houseId,
            fixtureGuid,
            itemDefinitionId,
            tintId,
            position,
            rotation,
            scale,
            resourceManager,
            isPreview: false,
            out npcGuid);
    }

    public static bool TryEnsurePreviewActor(
        GatewayConnection connection,
        int houseId,
        ulong fixtureGuid,
        int itemDefinitionId,
        int tintId,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager,
        out ulong npcGuid)
    {
        return TryEnsureActor(
            connection.Player,
            houseId,
            fixtureGuid,
            itemDefinitionId,
            tintId,
            position,
            rotation,
            scale,
            resourceManager,
            isPreview: true,
            out npcGuid);
    }

    public static bool TryEnsureActor(
        Player player,
        int houseId,
        ulong fixtureGuid,
        int itemDefinitionId,
        int tintId,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager,
        out ulong npcGuid)
    {
        return TryEnsureActor(
            player,
            houseId,
            fixtureGuid,
            itemDefinitionId,
            tintId,
            position,
            rotation,
            scale,
            resourceManager,
            isPreview: false,
            out npcGuid);
    }

    private static bool TryEnsureActor(
        Player player,
        int houseId,
        ulong fixtureGuid,
        int itemDefinitionId,
        int tintId,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager,
        bool isPreview,
        out ulong npcGuid)
    {
        var key = new FixtureActorKey(player.Guid, houseId, fixtureGuid);
        var interactionKind = ResolveInteractionKind(resourceManager, itemDefinitionId);
        ActorMetadata[key] = new FixtureActorMetadata(itemDefinitionId, interactionKind);
        if (isPreview)
            PreviewActorKeys[key] = 0;
        else
            PreviewActorKeys.TryRemove(key, out _);

        var actorRotation = ToActorRotation(rotation);

        if (ActorGuids.TryGetValue(key, out npcGuid) &&
            player.Zone.TryGetNpc(npcGuid, out var existingNpc))
        {
            BindHouseInstance(player, existingNpc);
            var normalizedScale = NormalizeScale(scale);
            var collisionEnabled = ResolveFixtureCollisionEnabled(
                isPreview,
                IsTeleporter(interactionKind));
            var previousScale = existingNpc.Scale;
            var previousTintId = existingNpc.TintId;
            var previousCollisionEnabled = existingNpc.CollisionEnabled;
            var previousInteractRange = existingNpc.InteractRange;
            var previousIsInteractable = existingNpc.IsInteractable;
            var previousCursorId = existingNpc.CursorId;

            var existingDefaultAnimationId = ResolveDefaultAnimationId(interactionKind, itemDefinitionId);

            existingNpc.Scale = normalizedScale;
            existingNpc.TintId = tintId;
            existingNpc.CollisionEnabled = collisionEnabled;
            existingNpc.Animation = existingDefaultAnimationId;
            existingNpc.StandAnimId = ResolveDefaultStandAnimationId(existingDefaultAnimationId);
            if (interactionKind == FixtureInteractionKind.ElevatorPlatform)
            {
                existingNpc.MovementType = 2;
                existingNpc.Speed = MovingPlatformSpeed;
            }
            existingNpc.UpdatePosition(position, actorRotation);
            BindInteraction(existingNpc, key, interactionKind, isPreview);

            var requiresFullRefresh = Math.Abs(previousScale - normalizedScale) > 0.0001f ||
                previousTintId != tintId ||
                previousCollisionEnabled != existingNpc.CollisionEnabled ||
                previousInteractRange != existingNpc.InteractRange ||
                previousIsInteractable != existingNpc.IsInteractable ||
                previousCursorId != existingNpc.CursorId;

            // House entry prepares every fixture while the player is hidden and
            // still zoning. Sending the actors during that phase duplicates the
            // post-ready replay and can overwhelm the client's reliable UDP
            // stream before it has finished constructing the house. Keep the
            // actors server-side until visibility is restored; ResendActors then
            // introduces them on the normal post-ready path.
            if (!player.Visible)
            {
                if (isPreview)
                    RemoveFixtureCompanionActor(player, key);
                else
                {
                    TrackMovingPlatform(
                        player,
                        key,
                        interactionKind,
                        itemDefinitionId,
                        position,
                        rotation,
                        normalizedScale);
                    EnsureFixtureCompanionActor(
                        player,
                        key,
                        itemDefinitionId,
                        tintId,
                        position,
                        actorRotation,
                        normalizedScale,
                        resourceManager);
                }

                return true;
            }

            if (!player.VisibleNpcs.ContainsKey(existingNpc.Guid) || requiresFullRefresh)
            {
                EnsureVisibleToPlayer(player, existingNpc);
            }
            else
            {
                player.SendTunneled(new PlayerUpdatePacketUpdatePosition
                {
                    Guid = existingNpc.Guid,
                    Position = position,
                    Rotation = actorRotation
                });
                existingNpc.OnAddVisiblePlayers([player]);
            }

            if (isPreview)
                RemoveFixtureCompanionActor(player, key);
            else
            {
                TrackMovingPlatform(
                    player,
                    key,
                    interactionKind,
                    itemDefinitionId,
                    position,
                    rotation,
                    normalizedScale);
                EnsureFixtureCompanionActor(
                    player,
                    key,
                    itemDefinitionId,
                    tintId,
                    position,
                    actorRotation,
                    normalizedScale,
                    resourceManager);
            }
            return true;
        }

        ActorGuids.TryRemove(key, out _);

        var modelId = HouseOwnershipService.ResolveFixtureActorModelId(resourceManager, itemDefinitionId);
        if (modelId == 0 ||
            !resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition) ||
            !player.Zone.TryCreateNpc(out var npc))
        {
            ActorMetadata.TryRemove(key, out _);
            PreviewActorKeys.TryRemove(key, out _);
            npcGuid = 0;
            return false;
        }

        npc.NameId = 0;
        npc.Name = string.Empty;
        npc.ModelId = modelId;
        npc.TextureAlias = itemDefinition.TextureAlias ?? string.Empty;
        npc.TintAlias = itemDefinition.TintAlias ?? string.Empty;
        npc.TintId = tintId;
        npc.Scale = NormalizeScale(scale);
        var defaultAnimationId = ResolveDefaultAnimationId(interactionKind, itemDefinitionId);
        npc.Animation = defaultAnimationId;
        npc.StandAnimId = ResolveDefaultStandAnimationId(defaultAnimationId);
        if (interactionKind == FixtureInteractionKind.ElevatorPlatform)
        {
            npc.MovementType = 2;
            npc.Speed = MovingPlatformSpeed;
        }
        npc.CompositeEffectId = 0;
        npc.HideNamePlate = true;
        BindHouseInstance(player, npc);
        BindInteraction(npc, key, interactionKind, isPreview);
        // The pad model has a radius of roughly 1.9 units. Keeping NPC collision
        // enabled prevents a player-sized actor from reaching the old trigger.
        npc.CollisionEnabled = ResolveFixtureCollisionEnabled(
            isPreview,
            IsTeleporter(interactionKind));
        npc.UpdatePosition(position, actorRotation);

        if (player.Visible)
        {
            EnsureVisibleToPlayer(player, npc);
            if (!isPreview && npc.CursorId == 0)
                SendInteractionRelevance(player, npc);
        }

        if (isPreview)
            RemoveFixtureCompanionActor(player, key);
        else
        {
            TrackMovingPlatform(
                player,
                key,
                interactionKind,
                itemDefinitionId,
                position,
                rotation,
                npc.Scale);
            EnsureFixtureCompanionActor(
                player,
                key,
                itemDefinitionId,
                tintId,
                position,
                actorRotation,
                npc.Scale,
                resourceManager);
        }

        npcGuid = npc.Guid;
        ActorGuids[key] = npcGuid;
        return true;
    }

    public static IReadOnlyList<Player> GetHouseOccupants(Player player)
    {
        if (player.CurrentHouseGuid == 0)
            return [player];

        return player.Zone.Players
            .Where(candidate => candidate.CurrentHouseGuid == player.CurrentHouseGuid)
            .GroupBy(candidate => candidate.Guid)
            .Select(group => group.First())
            .ToList();
    }

    public static void PublishFixture(
        Player sourcePlayer,
        DbHouse house,
        ulong sourceFixtureGuid,
        DbHouseFixture fixture,
        int tintId,
        IResourceManager resourceManager,
        bool includeAsset = true)
    {
        var position = new Vector4(
            fixture.PositionX,
            fixture.PositionY,
            fixture.PositionZ,
            fixture.PositionW);
        var rotation = ToHousingRotation(new Quaternion(
            fixture.RotationX,
            fixture.RotationY,
            fixture.RotationZ,
            fixture.RotationW));

        foreach (var recipient in GetHouseOccupants(sourcePlayer))
        {
            var fixtureGuid = recipient.Guid == sourcePlayer.Guid
                ? sourceFixtureGuid
                : GetClientFixtureGuid(recipient.Guid, house.Id, fixture.Id);

            TryEnsureActor(
                recipient,
                house.Id,
                fixtureGuid,
                fixture.ItemDefinitionId,
                tintId,
                position,
                rotation,
                fixture.Scale,
                resourceManager,
                out var npcGuid);

            Promote(
                recipient,
                house.Id,
                fixtureGuid,
                fixture.Id,
                position,
                rotation,
                fixture.Scale);

            var sentFixture = HouseOwnershipService.SendFixtureUpdate(
                recipient,
                house,
                fixtureGuid,
                npcGuid,
                fixture.ItemDefinitionId,
                itemRecordId: 0,
                tintId,
                position,
                rotation,
                fixture.Scale,
                resourceManager,
                isPreview: false,
                includeAsset);

            if (sentFixture && npcGuid != 0)
            {
                recipient.SendTunneled(new HousingPacketUpdateFixturePosition
                {
                    FixtureActorGuid = npcGuid,
                    Position = position,
                    Rotation = rotation
                });
            }

            ReplayDefaultFixtureAnimation(recipient, house.Id, fixture.Id);
        }
    }

    public static int ReplayPersistedFixtureUpdates(
        Player player,
        DbHouse house,
        IResourceManager resourceManager)
    {
        if (!TryGetCurrentHouseId(player, out var activeHouseId) ||
            activeHouseId != house.Id)
        {
            return 0;
        }

        var count = 0;

        foreach (var fixture in house.Fixtures.OrderBy(fixture => fixture.Id))
        {
            // Client-ready sends are delayed. Stop immediately if the player
            // changed houses while the database snapshot was being loaded.
            if (!TryGetCurrentHouseId(player, out activeHouseId) ||
                activeHouseId != house.Id)
            {
                break;
            }

            var fixtureGuid = GetClientFixtureGuid(
                player.Guid,
                house.Id,
                fixture.Id);
            var tintId = HouseOwnershipService.GetFixtureTintId(fixture, resourceManager);
            var position = new Vector4(
                fixture.PositionX,
                fixture.PositionY,
                fixture.PositionZ,
                fixture.PositionW);
            var rotation = ToHousingRotation(new Quaternion(
                fixture.RotationX,
                fixture.RotationY,
                fixture.RotationZ,
                fixture.RotationW));

            if (!TryEnsureActor(
                    player,
                    house.Id,
                    fixtureGuid,
                    fixture.ItemDefinitionId,
                    tintId,
                    position,
                    rotation,
                    fixture.Scale,
                    resourceManager,
                    out var npcGuid) ||
                npcGuid == 0)
            {
                continue;
            }

            // InstanceData creates the logical fixture while the house is still
            // zoning, but that resident record can retain a missing/stale native
            // collision body. FixtureUpdate only mutates an existing record, so
            // replaying it over the same GUID does not force the body to be
            // rebuilt. Tear down the stable fixture record first; the full
            // update + asset sequence below then follows the client's native
            // remove/recreate path while retaining the database-backed identity.
            player.SendTunneled(new HousingPacketRemoveFixture
            {
                FixtureGuid = fixtureGuid
            });

            Promote(
                player,
                house.Id,
                fixtureGuid,
                fixture.Id,
                position,
                rotation,
                fixture.Scale);

            // Rebind the persisted instance after AddNpc, but do not replay its
            // FixtureAsset here. Once decorate mode is active the client treats
            // that asset as the current placement cursor, leaving the final saved
            // item hovering even though the player selected nothing. Definitions
            // were already primed by the house grant; FixtureUpdate restores the
            // stable saved-fixture actor link without creating a cursor.
            if (!HouseOwnershipService.SendFixtureUpdate(
                    player,
                    house,
                    fixtureGuid,
                    npcGuid,
                    fixture.ItemDefinitionId,
                    itemRecordId: 0,
                    tintId,
                    position,
                    rotation,
                    fixture.Scale,
                    resourceManager,
                    isPreview: false,
                    includeAsset: false))
            {
                continue;
            }

            player.SendTunneled(new HousingPacketUpdateFixturePosition
            {
                FixtureActorGuid = npcGuid,
                Position = position,
                Rotation = rotation
            });

            ReplayDefaultFixtureAnimation(player, house.Id, fixture.Id);
            count++;
        }

        return count;
    }

    public static void PrepareHouse(
        GatewayConnection connection,
        DbHouse house,
        IResourceManager resourceManager)
    {
        RemoveAllForPlayer(connection.Player);

        foreach (var fixture in house.Fixtures.OrderBy(fixture => fixture.Id))
        {
            var fixtureGuid = GetClientFixtureGuid(
                connection.Player.Guid,
                house.Id,
                fixture.Id);
            if (!TryEnsureActor(
                connection,
                house.Id,
                fixtureGuid,
                fixture.ItemDefinitionId,
                HouseOwnershipService.GetFixtureTintId(fixture, resourceManager),
                new Vector4(fixture.PositionX, fixture.PositionY, fixture.PositionZ, fixture.PositionW),
                new Quaternion(fixture.RotationX, fixture.RotationY, fixture.RotationZ, fixture.RotationW),
                fixture.Scale,
                resourceManager,
                out _))
            {
                continue;
            }

            DatabaseFixtureIds[new FixtureActorKey(connection.Player.Guid, house.Id, fixtureGuid)] = fixture.Id;
        }

    }

    public static void ReplayHouseRuntime(Player player, int houseId)
    {
        ReplayDefaultFixtureAnimations(player, houseId);
        ReplayAnimatedFixtures(player, houseId);
        MovingPlatformRuntimePlayers[player.Guid] = 0;
    }

    public static void EnsurePersistedActors(
        GatewayConnection connection,
        DbHouse house,
        IResourceManager resourceManager)
    {
        foreach (var fixture in house.Fixtures.OrderBy(fixture => fixture.Id))
        {
            var fixtureGuid = GetClientFixtureGuid(
                connection.Player.Guid,
                house.Id,
                fixture.Id);
            if (!TryEnsureActor(
                connection,
                house.Id,
                fixtureGuid,
                fixture.ItemDefinitionId,
                HouseOwnershipService.GetFixtureTintId(fixture, resourceManager),
                new Vector4(fixture.PositionX, fixture.PositionY, fixture.PositionZ, fixture.PositionW),
                new Quaternion(fixture.RotationX, fixture.RotationY, fixture.RotationZ, fixture.RotationW),
                fixture.Scale,
                resourceManager,
                out _))
            {
                continue;
            }

            DatabaseFixtureIds[new FixtureActorKey(connection.Player.Guid, house.Id, fixtureGuid)] = fixture.Id;
        }
    }

    public static void Promote(
        Player player,
        int houseId,
        ulong fixtureGuid,
        int databaseFixtureId,
        Vector4 position,
        Quaternion rotation,
        float scale)
    {
        var key = new FixtureActorKey(player.Guid, houseId, fixtureGuid);
        DatabaseFixtureIds[key] = databaseFixtureId;
        PreviewActorKeys.TryRemove(key, out _);

        if (ActorGuids.TryGetValue(key, out var npcGuid) && player.Zone.TryGetNpc(npcGuid, out var npc))
        {
            npc.Scale = NormalizeScale(scale);
            npc.UpdatePosition(position, ToActorRotation(rotation));
        }

        UpdateMovingPlatformOrigin(player, key, position, rotation, scale);

        UpdateFixtureCompanionTransform(
            player,
            key,
            position,
            ToActorRotation(rotation),
            scale,
            sendPosition: false);
    }

    public static void PublishSavedTransform(
        Player sourcePlayer,
        DbHouse house,
        ulong sourceFixtureGuid,
        DbHouseFixture fixture,
        int tintId,
        IResourceManager resourceManager)
    {
        var position = new Vector4(
            fixture.PositionX,
            fixture.PositionY,
            fixture.PositionZ,
            fixture.PositionW);
        var rotation = ToHousingRotation(new Quaternion(
            fixture.RotationX,
            fixture.RotationY,
            fixture.RotationZ,
            fixture.RotationW));

        foreach (var recipient in GetHouseOccupants(sourcePlayer))
        {
            var fixtureGuid = recipient.Guid == sourcePlayer.Guid
                ? sourceFixtureGuid
                : GetClientFixtureGuid(recipient.Guid, house.Id, fixture.Id);
            var key = new FixtureActorKey(recipient.Guid, house.Id, fixtureGuid);
            var createdActor = false;

            if (!ActorGuids.TryGetValue(key, out var npcGuid) ||
                !recipient.Zone.TryGetNpc(npcGuid, out var npc))
            {
                if (!TryEnsureActor(
                        recipient,
                        house.Id,
                        fixtureGuid,
                        fixture.ItemDefinitionId,
                        tintId,
                        position,
                        rotation,
                        fixture.Scale,
                        resourceManager,
                        out npcGuid))
                {
                    continue;
                }
                createdActor = true;

                Promote(
                    recipient,
                    house.Id,
                    fixtureGuid,
                    fixture.Id,
                    position,
                    rotation,
                    fixture.Scale);

                HouseOwnershipService.SendFixtureUpdate(
                    recipient,
                    house,
                    fixtureGuid,
                    npcGuid,
                    fixture.ItemDefinitionId,
                    itemRecordId: 0,
                    tintId,
                    position,
                    rotation,
                    fixture.Scale,
                    resourceManager,
                    isPreview: false,
                    includeAsset: true);
            }
            else
            {
                npc.Scale = NormalizeScale(fixture.Scale);
                npc.UpdatePosition(position, ToActorRotation(rotation));
            }

            UpdateMovingPlatformOrigin(recipient, key, position, rotation, fixture.Scale);

            UpdateFixtureCompanionTransform(
                recipient,
                key,
                position,
                ToActorRotation(rotation),
                fixture.Scale,
                sendPosition: true);

            // The editing client already applied its local transform. Echoing a
            // full fixture update while its rotate tool owns the model can
            // invalidate that model and crash the client. Other occupants need
            // only the native housing transform delta.
            if ((recipient.Guid == sourcePlayer.Guid && !createdActor) || npcGuid == 0)
                continue;

            recipient.SendTunneled(new HousingPacketUpdateFixturePosition
            {
                FixtureActorGuid = npcGuid,
                Position = position,
                Rotation = rotation
            });
        }
    }

    public static void SendPersistedFixtureTransforms(Player player, DbHouse house)
    {
        foreach (var fixture in house.Fixtures.OrderBy(fixture => fixture.Id))
        {
            var fixtureGuid = GetClientFixtureGuid(player.Guid, house.Id, fixture.Id);
            var key = new FixtureActorKey(player.Guid, house.Id, fixtureGuid);
            if (!ActorGuids.TryGetValue(key, out var npcGuid) ||
                npcGuid == 0 ||
                !player.Zone.TryGetNpc(npcGuid, out var npc))
            {
                continue;
            }

            var position = new Vector4(
                fixture.PositionX,
                fixture.PositionY,
                fixture.PositionZ,
                fixture.PositionW);
            var rotation = ToHousingRotation(new Quaternion(
                fixture.RotationX,
                fixture.RotationY,
                fixture.RotationZ,
                fixture.RotationW));

            npc.Scale = NormalizeScale(fixture.Scale);
            npc.UpdatePosition(position, ToActorRotation(rotation));
            UpdateMovingPlatformOrigin(player, key, position, rotation, fixture.Scale);
            UpdateFixtureCompanionTransform(
                player,
                key,
                position,
                ToActorRotation(rotation),
                fixture.Scale,
                sendPosition: true);
            player.SendTunneled(new HousingPacketUpdateFixturePosition
            {
                FixtureActorGuid = npcGuid,
                Position = position,
                Rotation = rotation
            });
        }
    }

    public static Quaternion ToHousingRotation(Quaternion rotation)
    {
        if (!float.IsFinite(rotation.X) ||
            !float.IsFinite(rotation.Y) ||
            !float.IsFinite(rotation.Z) ||
            !float.IsFinite(rotation.W))
        {
            return new Quaternion(0f, 0f, 0f, 0f);
        }

        if (MathF.Abs(rotation.X) <= 0.0001f &&
            MathF.Abs(rotation.Y) <= 0.0001f &&
            MathF.Abs(rotation.Z) <= 0.0001f)
        {
            return new Quaternion(0f, 0f, 0f, 0f);
        }

        var planarLengthSquared = rotation.X * rotation.X + rotation.Z * rotation.Z;
        var isPlanarFacing = MathF.Abs(rotation.Y) <= 0.0001f &&
            MathF.Abs(rotation.W) <= 0.0001f &&
            MathF.Abs(planarLengthSquared - 1f) <= 0.01f;

        if (isPlanarFacing)
        {
            return new Quaternion(
                MathF.Atan2(rotation.X, rotation.Z),
                0f,
                0f,
                0f);
        }

        // The housing protocol names this field Quaternion, but the native
        // client consumes it as yaw (Y axis), pitch (X axis), and roll (Z axis).
        return new Quaternion(rotation.X, rotation.Y, rotation.Z, 0f);
    }

    public static Quaternion ToActorRotation(Quaternion fixtureRotation)
    {
        var housingRotation = ToHousingRotation(fixtureRotation);
        var yaw = housingRotation.X;

        return new Quaternion(
            MathF.Sin(yaw),
            0f,
            MathF.Cos(yaw),
            0f);
    }

    public static int ResolveDatabaseFixtureId(ulong playerGuid, int houseId, ulong fixtureGuid)
    {
        fixtureGuid = ResolveClientFixtureGuid(playerGuid, houseId, fixtureGuid);
        var key = new FixtureActorKey(playerGuid, houseId, fixtureGuid);
        if (DatabaseFixtureIds.TryGetValue(key, out var fixtureId))
            return fixtureId;

        if (fixtureGuid <= int.MaxValue)
            return (int)fixtureGuid;

        var unwrapped = fixtureGuid >> 4;
        return unwrapped <= int.MaxValue ? (int)unwrapped : 0;
    }

    public static ulong ResolveClientFixtureGuid(ulong playerGuid, int houseId, ulong guid)
    {
        var directKey = new FixtureActorKey(playerGuid, houseId, guid);
        if (ActorGuids.ContainsKey(directKey) || DatabaseFixtureIds.ContainsKey(directKey))
            return guid;

        foreach (var entry in ActorGuids)
        {
            if (entry.Key.PlayerGuid == playerGuid &&
                entry.Key.HouseId == houseId &&
                entry.Value == guid)
            {
                return entry.Key.FixtureGuid;
            }
        }

        if (guid <= int.MaxValue)
        {
            foreach (var entry in DatabaseFixtureIds)
            {
                if (entry.Key.PlayerGuid == playerGuid &&
                    entry.Key.HouseId == houseId &&
                    entry.Value == (int)guid)
                {
                    return entry.Key.FixtureGuid;
                }
            }
        }

        return guid;
    }

    public static ulong GetNpcGuid(ulong playerGuid, int houseId, ulong fixtureGuid)
    {
        fixtureGuid = ResolveClientFixtureGuid(playerGuid, houseId, fixtureGuid);
        return ActorGuids.TryGetValue(new FixtureActorKey(playerGuid, houseId, fixtureGuid), out var npcGuid)
            ? npcGuid
            : 0;
    }

    public static bool TryHandleInteraction(Player player, ulong targetGuid)
    {
        if (!TryGetCurrentHouseId(player, out var houseId))
            return false;

        var fixtureGuid = ResolveClientFixtureGuid(player.Guid, houseId, targetGuid);
        var key = new FixtureActorKey(player.Guid, houseId, fixtureGuid);
        if (!ActorMetadata.TryGetValue(key, out var metadata) ||
            !SupportsClickInteraction(metadata.Kind))
        {
            return false;
        }

        HandleInteraction(player, key);
        return true;
    }

    public static int GetTintId(Player player, int houseId, ulong fixtureGuid)
    {
        fixtureGuid = ResolveClientFixtureGuid(player.Guid, houseId, fixtureGuid);
        if (!ActorGuids.TryGetValue(new FixtureActorKey(player.Guid, houseId, fixtureGuid), out var npcGuid) ||
            !player.Zone.TryGetNpc(npcGuid, out var npc))
        {
            return 0;
        }

        return npc.TintId;
    }

    public static ulong GetClientFixtureGuid(ulong playerGuid, int houseId, int databaseFixtureId)
    {
        foreach (var entry in DatabaseFixtureIds)
        {
            if (entry.Key.PlayerGuid == playerGuid &&
                entry.Key.HouseId == houseId &&
                entry.Value == databaseFixtureId)
            {
                return entry.Key.FixtureGuid;
            }
        }

        return (ulong)databaseFixtureId;
    }

    public static void Remove(Player player, int houseId, ulong fixtureGuid)
    {
        var requestedGuid = fixtureGuid;
        fixtureGuid = ResolveClientFixtureGuid(player.Guid, houseId, fixtureGuid);
        var databaseFixtureId = ResolveDatabaseFixtureId(player.Guid, houseId, fixtureGuid);
        var keys = ActorGuids.Keys
            .Concat(CompanionActorGuids.Keys)
            .Concat(DatabaseFixtureIds.Keys)
            .Concat(ActorMetadata.Keys)
            .Concat(PreviewActorKeys.Keys)
            .Where(key =>
                key.PlayerGuid == player.Guid &&
                key.HouseId == houseId &&
                (key.FixtureGuid == requestedGuid ||
                    key.FixtureGuid == fixtureGuid ||
                    (databaseFixtureId > 0 &&
                        (key.FixtureGuid == (ulong)databaseFixtureId ||
                            (DatabaseFixtureIds.TryGetValue(key, out var mappedId) &&
                                mappedId == databaseFixtureId)))))
            .Distinct()
            .ToList();

        if (keys.Count == 0)
            keys.Add(new FixtureActorKey(player.Guid, houseId, fixtureGuid));

        var npcGuids = new HashSet<ulong>();
        var companionNpcGuids = new HashSet<ulong>();
        foreach (var key in keys)
        {
            DatabaseFixtureIds.TryRemove(key, out _);
            ActorMetadata.TryRemove(key, out _);
            PreviewActorKeys.TryRemove(key, out _);
            MovingPlatforms.TryRemove(key, out _);
            if (ActorGuids.TryRemove(key, out var npcGuid))
                npcGuids.Add(npcGuid);
            if (CompanionActorGuids.TryRemove(key, out var companionNpcGuid))
                companionNpcGuids.Add(companionNpcGuid);
        }

        foreach (var npcGuid in npcGuids)
        {
            if (player.Zone.TryGetNpc(npcGuid, out var npc))
                npc.Dispose();
        }

        foreach (var npcGuid in companionNpcGuids)
        {
            player.SendTunneled(new PlayerUpdatePacketRemovePlayer
            {
                Guid = npcGuid
            });
            if (player.Zone.TryGetNpc(npcGuid, out var npc))
                npc.Dispose();
        }
    }

    public static void RemoveAllForPlayer(Player player)
    {
        var keys = ActorGuids.Keys
            .Concat(CompanionActorGuids.Keys)
            .Concat(DatabaseFixtureIds.Keys)
            .Concat(ActorMetadata.Keys)
            .Concat(PreviewActorKeys.Keys)
            .Where(key => key.PlayerGuid == player.Guid)
            .Distinct()
            .ToList();

        foreach (var key in keys)
            Remove(player, key.HouseId, key.FixtureGuid);

        LastInteractionTimes.TryRemove(player.Guid, out _);
        TeleporterLandingLocks.TryRemove(player.Guid, out _);
        ActiveTeleports.TryRemove(player.Guid, out _);
        PlayersInEditMode.TryRemove(player.Guid, out _);
        MovingPlatformRuntimePlayers.TryRemove(player.Guid, out _);

        foreach (var key in MovingPlatforms.Keys.Where(key => key.PlayerGuid == player.Guid))
            MovingPlatforms.TryRemove(key, out _);

    }

    public static void SetEditMode(Player player, bool inEditMode)
    {
        if (inEditMode)
        {
            PlayersInEditMode[player.Guid] = 0;
            ResetMovingPlatforms(player);
        }
        else
        {
            PlayersInEditMode.TryRemove(player.Guid, out _);
        }

        foreach (var entry in ActorMetadata.Where(entry => entry.Key.PlayerGuid == player.Guid))
        {
            if (!ActorGuids.TryGetValue(entry.Key, out var npcGuid) ||
                !player.Zone.TryGetNpc(npcGuid, out var npc))
            {
                continue;
            }

            var previousInteractRange = npc.InteractRange;
            var previousIsInteractable = npc.IsInteractable;
            var previousCursorId = npc.CursorId;
            BindInteraction(npc, entry.Key, entry.Value.Kind, PreviewActorKeys.ContainsKey(entry.Key));

            // FixtureInstance.Unknown8 keeps the editor bound to this exact GUID.
            // A same-GUID AddNpc refresh updates selection and interaction fields
            // without disposing or replacing that actor, so ordinary fixtures are
            // editor-selectable while real fixture actions stay locally ranged.
            if (!player.VisibleNpcs.ContainsKey(npcGuid))
                EnsureVisibleToPlayer(player, npc);
            else if (previousInteractRange != npc.InteractRange ||
                previousIsInteractable != npc.IsInteractable ||
                previousCursorId != npc.CursorId)
                player.SendTunneled(CreateFixtureAddNpcPacket(npc));

            SendInteractionRelevance(player, npc);
            npc.OnAddVisiblePlayers([player]);
        }
    }

    public static bool IsInEditMode(Player player)
    {
        return PlayersInEditMode.ContainsKey(player.Guid);
    }

    public static int ResendActors(Player player)
    {
        var count = 0;

        foreach (var entry in ActorGuids.Where(entry => entry.Key.PlayerGuid == player.Guid))
        {
            if (player.Zone.TryGetNpc(entry.Value, out var npc))
            {
                // Housing actors are first introduced while the client is still
                // hydrating the house. A same-GUID AddNpc after ClientIsReady is
                // only treated as an actor update, so a zoning-era actor that
                // missed its model collision keeps that collisionless state.
                // Remove the client copy first so the corrected AddNpc below
                // follows the same native construction path as a freshly placed
                // fixture. The server-side NPC and stable fixture link remain
                // intact.
                player.SendTunneled(new PlayerUpdatePacketRemovePlayer
                {
                    Guid = npc.Guid
                });
                EnsureVisibleToPlayer(player, npc);
                RefreshFixtureCollision(player, npc);
                count++;
            }
        }

        foreach (var entry in CompanionActorGuids.Where(entry => entry.Key.PlayerGuid == player.Guid))
        {
            if (!player.Zone.TryGetNpc(entry.Value, out var npc))
                continue;

            player.SendTunneled(new PlayerUpdatePacketRemovePlayer
            {
                Guid = npc.Guid
            });
            EnsureFixtureCompanionVisibleToPlayer(player, npc);
            count++;
        }

        return count;
    }

    public static bool HandlePlayerPosition(Player player)
    {
        if (!TryGetCurrentHouseId(player, out var houseId))
            return false;

        if (PlayersInEditMode.ContainsKey(player.Guid))
            return false;

        if (ActiveTeleports.ContainsKey(player.Guid))
            return true;

        if (TeleporterLandingLocks.TryGetValue(player.Guid, out var landingNpcGuid))
        {
            if (player.Zone.TryGetNpc(landingNpcGuid, out var landingNpc) &&
                GetHorizontalDistance(player.Position, landingNpc.Position) <= TeleporterReleaseRadius)
            {
                return false;
            }

            TeleporterLandingLocks.TryRemove(player.Guid, out _);
        }

        var persistedActors = GetPersistedActors(player, houseId);
        var teleporter = persistedActors
            .Where(entry => IsTeleporter(entry.Metadata.Kind))
            .Select(entry => new { Entry = entry, Distance = GetHorizontalDistance(player.Position, entry.Npc.Position) })
            .Where(entry => entry.Distance <= TeleporterActivationRadius)
            .OrderBy(entry => entry.Distance)
            .Select(entry => entry.Entry)
            .FirstOrDefault();

        if (teleporter.Npc is not null && ActivateTeleporter(player, teleporter.Key))
            return true;

        var launchPad = persistedActors
            .Where(entry => entry.Metadata.Kind == FixtureInteractionKind.LaunchPad)
            .Select(entry => new
            {
                Entry = entry,
                HorizontalDistance = GetHorizontalDistance(player.Position, entry.Npc.Position),
                VerticalDistance = MathF.Abs(player.Position.Y - entry.Npc.Position.Y)
            })
            .Where(entry =>
                entry.HorizontalDistance <= LaunchPadActivationRadius &&
                entry.VerticalDistance <= LaunchPadVerticalTolerance)
            .OrderBy(entry => entry.HorizontalDistance)
            .Select(entry => entry.Entry)
            .FirstOrDefault();

        if (launchPad.Npc is not null && TryBeginInteraction(player.Guid))
        {
            ActivateLaunchPad(player);
            return true;
        }

        return false;
    }

    public static void OnFixtureRemoved(Player sourcePlayer, int houseId, int databaseFixtureId)
    {
        ActiveAnimatedFixtures.TryRemove(new FixtureRuntimeKey(houseId, databaseFixtureId), out _);
    }

    private static void EnsureFixtureCompanionActor(
        Player player,
        FixtureActorKey key,
        int itemDefinitionId,
        int tintId,
        Vector4 position,
        Quaternion rotation,
        float scale,
        IResourceManager resourceManager)
    {
        if (!FixtureCompanionDefinitions.TryGetValue(itemDefinitionId, out var definition))
        {
            RemoveFixtureCompanionActor(player, key);
            return;
        }

        var modelId = resourceManager.Models.Values
            .FirstOrDefault(candidate => string.Equals(
                candidate.ModelFileName,
                definition.ModelName,
                StringComparison.OrdinalIgnoreCase))
            ?.Id ?? 0;
        if (modelId == 0)
        {
            RemoveFixtureCompanionActor(player, key);
            Logger.Warn(
                "Could not resolve housing fixture companion model. ( ItemDefinitionId: {ItemDefinitionId}, Model: {ModelName} )",
                itemDefinitionId,
                definition.ModelName);
            return;
        }

        var normalizedScale = NormalizeScale(scale);
        if (CompanionActorGuids.TryGetValue(key, out var companionGuid) &&
            player.Zone.TryGetNpc(companionGuid, out var existingCompanion))
        {
            var requiresFullRefresh =
                existingCompanion.ModelId != modelId ||
                existingCompanion.TextureAlias != definition.TextureAlias ||
                existingCompanion.TintAlias != definition.TintAlias ||
                existingCompanion.TintId != tintId ||
                Math.Abs(existingCompanion.Scale - normalizedScale) > 0.0001f ||
                !player.VisibleNpcs.ContainsKey(companionGuid);

            existingCompanion.ModelId = modelId;
            existingCompanion.TextureAlias = definition.TextureAlias;
            existingCompanion.TintAlias = definition.TintAlias;
            existingCompanion.TintId = tintId;
            existingCompanion.Scale = normalizedScale;
            existingCompanion.CollisionEnabled = false;
            existingCompanion.IsInteractable = false;
            existingCompanion.InteractRange = 0;
            existingCompanion.CursorId = 0;
            existingCompanion.InteractAction = null;
            BindHouseInstance(player, existingCompanion);
            existingCompanion.UpdatePosition(position, rotation);

            if (requiresFullRefresh)
            {
                EnsureFixtureCompanionVisibleToPlayer(player, existingCompanion);
            }
            else
            {
                player.SendTunneled(new PlayerUpdatePacketUpdatePosition
                {
                    Guid = existingCompanion.Guid,
                    Position = position,
                    Rotation = rotation
                });
                existingCompanion.OnAddVisiblePlayers([player]);
            }

            return;
        }

        if (CompanionActorGuids.TryRemove(key, out var staleCompanionGuid))
        {
            player.SendTunneled(new PlayerUpdatePacketRemovePlayer
            {
                Guid = staleCompanionGuid
            });
        }

        if (!player.Zone.TryCreateNpc(out var companion))
            return;

        companion.NameId = 0;
        companion.Name = string.Empty;
        companion.ModelId = modelId;
        companion.TextureAlias = definition.TextureAlias;
        companion.TintAlias = definition.TintAlias;
        companion.TintId = tintId;
        companion.Scale = normalizedScale;
        companion.Animation = FixtureIdleAnimationId;
        companion.HideNamePlate = true;
        companion.CollisionEnabled = false;
        companion.IsInteractable = false;
        companion.InteractRange = 0;
        companion.CursorId = 0;
        companion.InteractAction = null;
        BindHouseInstance(player, companion);
        companion.UpdatePosition(position, rotation);

        CompanionActorGuids[key] = companion.Guid;
        EnsureFixtureCompanionVisibleToPlayer(player, companion);
    }

    private static void UpdateFixtureCompanionTransform(
        Player player,
        FixtureActorKey key,
        Vector4 position,
        Quaternion rotation,
        float scale,
        bool sendPosition)
    {
        if (!CompanionActorGuids.TryGetValue(key, out var companionGuid) ||
            !player.Zone.TryGetNpc(companionGuid, out var companion))
        {
            return;
        }

        var normalizedScale = NormalizeScale(scale);
        var scaleChanged = Math.Abs(companion.Scale - normalizedScale) > 0.0001f;
        companion.Scale = normalizedScale;
        companion.UpdatePosition(position, rotation);

        if (scaleChanged)
        {
            EnsureFixtureCompanionVisibleToPlayer(player, companion);
        }
        else if (sendPosition)
        {
            player.SendTunneled(new PlayerUpdatePacketUpdatePosition
            {
                Guid = companion.Guid,
                Position = position,
                Rotation = rotation
            });
            companion.OnAddVisiblePlayers([player]);
        }
    }

    private static void RemoveFixtureCompanionActor(Player player, FixtureActorKey key)
    {
        if (!CompanionActorGuids.TryRemove(key, out var companionGuid))
            return;

        player.SendTunneled(new PlayerUpdatePacketRemovePlayer
        {
            Guid = companionGuid
        });
        if (player.Zone.TryGetNpc(companionGuid, out var companion))
            companion.Dispose();
    }

    private static void EnsureFixtureCompanionVisibleToPlayer(Player player, Npc companion)
    {
        if (!player.Visible)
            return;

        player.SendTunneled(CreateFixtureAddNpcPacket(companion));
        player.VisibleNpcs.TryAdd(companion.Guid, companion);
        companion.OnAddVisiblePlayers([player]);
    }

    private static void EnsureVisibleToPlayer(Player player, Npc npc)
    {
        if (!player.Visible)
            return;

        if (!player.VisibleNpcs.ContainsKey(npc.Guid))
        {
            player.SendTunneled(CreateFixtureAddNpcPacket(npc));
            if (npc.CursorId != 0)
                SendInteractionRelevance(player, npc);

            player.VisibleNpcs.TryAdd(npc.Guid, npc);
        }
        else
        {
            player.SendTunneled(CreateFixtureAddNpcPacket(npc));
            SendInteractionRelevance(player, npc);
        }

        npc.OnAddVisiblePlayers([player]);
    }

    private static void RefreshFixtureCollision(Player player, Npc npc)
    {
        if (!npc.CollisionEnabled)
            return;

        // A persisted AddNpc can retain a stale/missing physics resource after
        // zoning. Disabling collision forces the client to rebuild that model
        // resource; immediately restoring it reloads the actor's original solid
        // flags and leaves collision enabled.
        player.SendTunneled(new PlayerUpdatePacketSetCollidable
        {
            Guid = npc.Guid,
            Collidable = false
        });
        player.SendTunneled(new PlayerUpdatePacketSetCollidable
        {
            Guid = npc.Guid,
            Collidable = true
        });
    }

    internal static PlayerUpdatePacketAddNpc CreateFixtureAddNpcPacket(Npc npc)
    {
        var packet = npc.GetAddNpcPacket();

        // Preserve the native AddNpc collision wire value. The client negates
        // this field while applying its internal "disable model extents" flag,
        // so sending the inverse here leaves ordinary fixture models without
        // the extents used by both movement collision and housing raycasts.
        packet.Unknown42 = npc.CollisionEnabled;

        return packet;
    }

    private static void SendInteractionRelevance(Player player, Npc npc)
    {
        var packet = new PlayerUpdatePacketNpcRelevance();
        packet.Entries.Add(CreateInteractionRelevanceEntry(npc));
        player.SendTunneled(packet);
    }

    internal static PlayerUpdatePacketNpcRelevance.Entry CreateInteractionRelevanceEntry(Npc npc)
    {
        return new PlayerUpdatePacketNpcRelevance.Entry
        {
            Guid = npc.Guid,
            // Editor selection is carried by AddNpc.IsInteractable. NpcRelevance
            // is reserved for genuine world actions; advertising editor-only
            // actors here leaves the client's highlight and Press-X target cached
            // after decorate mode closes.
            Unknown = npc.CursorId != 0,
            CursorId = npc.CursorId,
            HasCursor = false
        };
    }

    private static float NormalizeScale(float scale)
    {
        return scale <= 0 ? 1.0f : scale;
    }

    internal static void BindHouseInstance(Player player, Npc npc)
    {
        npc.CurrentHouseGuid = player.CurrentHouseGuid;
    }

    internal static bool ResolveFixtureCollisionEnabled(bool isPreview, bool isTeleporter)
    {
        return !isPreview && !isTeleporter;
    }

    internal static int ResolveFixtureInteractionRange(
        bool isPreview,
        bool inEditMode,
        bool supportsClickInteraction)
    {
        // Generic NPCs default to 100, which lets an actionable
        // fixture own the Press-X prompt from far across a house. Housing actors
        // should only advertise world actions at normal close-interaction distance.
        // Keep the known-good editor range so distant fixture selection is not
        // coupled to gameplay prompt behavior.
        if (isPreview)
            return 0;

        if (inEditMode)
            return FixtureEditorSelectionRange;

        return supportsClickInteraction ? FixtureWorldInteractionRange : 0;
    }

    internal static (bool IsInteractable, bool SupportsWorldInteraction) ResolveInteractionState(
        bool isPreview,
        bool inEditMode,
        bool supportsClickInteraction)
    {
        var supportsWorldInteraction = !isPreview &&
            !inEditMode &&
            supportsClickInteraction;
        // Persisted fixtures must be born as interactable actors even outside
        // decorate mode. The client uses that construction bit to retain model
        // extents for physical collision and to register the actor for later
        // housing-editor raycasts. CursorId, relevance, interaction range, and
        // InteractAction still remain disabled for ordinary world fixtures, so
        // this does not expose a Press-X action outside decorate mode.
        var isInteractable = !isPreview;
        return (isInteractable, supportsWorldInteraction);
    }

    private static void BindInteraction(
        Npc npc,
        FixtureActorKey key,
        FixtureInteractionKind kind,
        bool isPreview)
    {
        var inEditMode = PlayersInEditMode.ContainsKey(key.PlayerGuid);
        var supportsClickInteraction = SupportsClickInteraction(kind);
        var interactionState = ResolveInteractionState(
            isPreview,
            inEditMode,
            supportsClickInteraction);

        // Persisted actors remain raycast targets for the housing editor. Cursor
        // zero and a null action keep them from becoming gameplay interactions.
        npc.InteractRange = ResolveFixtureInteractionRange(
            isPreview,
            inEditMode,
            supportsClickInteraction);
        npc.IsInteractable = interactionState.IsInteractable;
        npc.CursorId = interactionState.SupportsWorldInteraction
            ? FixtureInteractionCursorId
            : (byte)0;
        npc.InteractAction = interactionState.SupportsWorldInteraction
            ? interactingPlayer => HandleInteraction(interactingPlayer, key)
            : null;
    }

    private static void HandleInteraction(Player player, FixtureActorKey key)
    {
        if (player.Guid != key.PlayerGuid ||
            !TryGetCurrentHouseId(player, out var houseId) ||
            houseId != key.HouseId ||
            !ActorMetadata.TryGetValue(key, out var metadata))
        {
            return;
        }

        if (PlayersInEditMode.ContainsKey(player.Guid))
            return;

        if (IsTeleporter(metadata.Kind))
        {
            ActivateTeleporter(player, key);
            return;
        }

        if (!TryBeginInteraction(player.Guid))
        {
            return;
        }

        ActivateAnimatedFixture(player, key, metadata);
    }

    private static bool ActivateTeleporter(Player player, FixtureActorKey sourceKey)
    {
        if (!ActorMetadata.TryGetValue(sourceKey, out var sourceMetadata) ||
            !IsTeleporter(sourceMetadata.Kind))
        {
            return false;
        }

        var teleporters = GetPersistedActors(player, sourceKey.HouseId)
            .Where(entry =>
                IsTeleporter(entry.Metadata.Kind) &&
                entry.Metadata.ItemDefinitionId == sourceMetadata.ItemDefinitionId)
            .OrderBy(entry => entry.DatabaseFixtureId)
            .ToList();
        if (teleporters.Count < 2)
            return false;

        var sourceIndex = teleporters.FindIndex(entry => entry.Key == sourceKey);
        if (sourceIndex < 0)
            sourceIndex = teleporters.FindIndex(entry =>
                GetHorizontalDistance(player.Position, entry.Npc.Position) <= TeleporterActivationRadius);
        if (sourceIndex < 0)
            return false;

        if (!TryBeginInteraction(player.Guid))
            return false;

        var destination = teleporters[(sourceIndex + 1) % teleporters.Count];
        if (!ActiveTeleports.TryAdd(player.Guid, 0))
            return false;

        var sourceDatabaseFixtureId = teleporters[sourceIndex].DatabaseFixtureId;
        var destinationDatabaseFixtureId = destination.DatabaseFixtureId;

        SetTeleporterPairAnimation(
            player,
            sourceKey.HouseId,
            sourceDatabaseFixtureId,
            destinationDatabaseFixtureId,
            TeleporterOpenAnimationId);

        var (buildupEffectId, beamEffectId) = ResolveTeleporterEffectIds(sourceMetadata.ItemDefinitionId);
        PlayTeleporterEffects(
            player,
            sourceKey.HouseId,
            sourceDatabaseFixtureId,
            destinationDatabaseFixtureId,
            buildupEffectId,
            beamEffectId);

        _ = CompleteTeleporterSequenceAsync(
            player,
            sourceKey.HouseId,
            sourceDatabaseFixtureId,
            destinationDatabaseFixtureId,
            destination.Npc.Guid,
            destination.Npc.Position,
            destination.Npc.Rotation);
        return true;
    }

    private static async System.Threading.Tasks.Task CompleteTeleporterSequenceAsync(
        Player player,
        int houseId,
        int sourceDatabaseFixtureId,
        int destinationDatabaseFixtureId,
        ulong destinationNpcGuid,
        Vector4 destinationPosition,
        Quaternion destinationRotation)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(TeleporterEffectDelay);
            if (!TryGetCurrentHouseId(player, out var currentHouseId) || currentHouseId != houseId)
                return;

            destinationPosition.Y += 0.5f;
            player.UpdatePosition(destinationPosition, destinationRotation);
            player.SendTunneled(new ClientUpdatePacketUpdateLocation
            {
                Position = destinationPosition,
                Rotation = destinationRotation,
                Teleport = true
            });
            player.SendTunneledToVisible(new PlayerUpdatePacketUpdatePosition
            {
                Guid = player.Guid,
                Position = destinationPosition,
                Rotation = destinationRotation
            });

            TeleporterLandingLocks[player.Guid] = destinationNpcGuid;

            SetTeleporterPairAnimation(
                player,
                houseId,
                sourceDatabaseFixtureId,
                destinationDatabaseFixtureId,
                TeleporterCloseAnimationId);
            await System.Threading.Tasks.Task.Delay(TeleporterCloseAnimationDelay);
        }
        finally
        {
            SetTeleporterPairAnimation(
                player,
                houseId,
                sourceDatabaseFixtureId,
                destinationDatabaseFixtureId,
                FixtureLoopAnimationId);
            ActiveTeleports.TryRemove(player.Guid, out _);
        }
    }

    private static void SetTeleporterPairAnimation(
        Player sourcePlayer,
        int houseId,
        int sourceDatabaseFixtureId,
        int destinationDatabaseFixtureId,
        int animationId)
    {
        foreach (var recipient in GetHouseOccupants(sourcePlayer))
        {
            SendFixtureAnimation(recipient, houseId, sourceDatabaseFixtureId, animationId);
            SendFixtureAnimation(recipient, houseId, destinationDatabaseFixtureId, animationId);
        }
    }

    private static void ActivateLaunchPad(Player player)
    {
        var packet = new PlayerUpdatePacketJump
        {
            Guid = player.Guid,
            Position = player.Position,
            Rotation = player.Rotation,
            State = 1,
            Unknown = 0,
            VerticalVelocity = LargeLaunchPadVelocity
        };

        player.SendTunneled(packet);
        player.SendTunneledToVisible(packet);
    }

    private static void PlayTeleporterEffects(
        Player sourcePlayer,
        int houseId,
        int sourceDatabaseFixtureId,
        int destinationDatabaseFixtureId,
        int buildupEffectId,
        int beamEffectId)
    {
        foreach (var recipient in GetHouseOccupants(sourcePlayer))
        {
            var sourceFixtureGuid = GetClientFixtureGuid(recipient.Guid, houseId, sourceDatabaseFixtureId);
            var destinationFixtureGuid = GetClientFixtureGuid(recipient.Guid, houseId, destinationDatabaseFixtureId);
            var sourceNpcGuid = GetNpcGuid(recipient.Guid, houseId, sourceFixtureGuid);
            var destinationNpcGuid = GetNpcGuid(recipient.Guid, houseId, destinationFixtureGuid);
            if (sourceNpcGuid == 0 || destinationNpcGuid == 0 ||
                !recipient.Zone.TryGetNpc(sourceNpcGuid, out var sourceNpc) ||
                !recipient.Zone.TryGetNpc(destinationNpcGuid, out var destinationNpc))
            {
                continue;
            }

            recipient.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = sourceNpcGuid,
                CompositeEffectId = buildupEffectId,
                Position = sourceNpc.Position,
                Clear = true
            });
            recipient.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = destinationNpcGuid,
                CompositeEffectId = buildupEffectId,
                Position = destinationNpc.Position,
                Clear = true
            });
            recipient.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = sourceNpcGuid,
                Unknown2 = destinationNpcGuid,
                CompositeEffectId = beamEffectId,
                Position = destinationNpc.Position,
                Clear = true
            });
        }
    }

    private static (int BuildupEffectId, int BeamEffectId) ResolveTeleporterEffectIds(int itemDefinitionId)
    {
        return itemDefinitionId switch
        {
            10358 => (16578, 16577),
            10362 => (16579, 16581),
            _ => (16580, 16582)
        };
    }

    private static void TrackMovingPlatform(
        Player recipient,
        FixtureActorKey key,
        FixtureInteractionKind kind,
        int itemDefinitionId,
        Vector4 basePosition,
        Quaternion housingRotation,
        float scale)
    {
        if (kind != FixtureInteractionKind.ElevatorPlatform || PreviewActorKeys.ContainsKey(key))
        {
            MovingPlatforms.TryRemove(key, out _);
            return;
        }

        MovingPlatforms[key] = new MovingPlatformState(
            recipient,
            itemDefinitionId,
            basePosition,
            ToHousingRotation(housingRotation),
            NormalizeScale(scale));
    }

    private static void UpdateMovingPlatformOrigin(
        Player recipient,
        FixtureActorKey key,
        Vector4 basePosition,
        Quaternion housingRotation,
        float scale)
    {
        if (!ActorMetadata.TryGetValue(key, out var metadata) ||
            metadata.Kind != FixtureInteractionKind.ElevatorPlatform ||
            PreviewActorKeys.ContainsKey(key))
        {
            MovingPlatforms.TryRemove(key, out _);
            return;
        }

        TrackMovingPlatform(
            recipient,
            key,
            metadata.Kind,
            metadata.ItemDefinitionId,
            basePosition,
            housingRotation,
            scale);
    }

    private static async Task MonitorMovingPlatformsAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(MovingPlatformUpdateInterval);
                var timelineSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

                foreach (var entry in MovingPlatforms.ToArray())
                {
                    var key = entry.Key;
                    var state = entry.Value;
                    var recipient = state.Recipient;

                    if (!MovingPlatformRuntimePlayers.ContainsKey(key.PlayerGuid) ||
                        PlayersInEditMode.ContainsKey(key.PlayerGuid) ||
                        !TryGetCurrentHouseId(recipient, out var currentHouseId) ||
                        currentHouseId != key.HouseId ||
                        !DatabaseFixtureIds.ContainsKey(key) ||
                        !ActorGuids.TryGetValue(key, out var npcGuid) ||
                        !recipient.Zone.TryGetNpc(npcGuid, out var npc))
                    {
                        continue;
                    }

                    var position = ResolveMovingPlatformPosition(
                        state.ItemDefinitionId,
                        state.BasePosition,
                        state.HousingRotation,
                        state.Scale,
                        timelineSeconds);
                    if (Vector4.DistanceSquared(position, state.LastPosition) <= 0.000001f)
                        continue;

                    state.LastPosition = position;
                    npc.UpdatePosition(position, ToActorRotation(state.HousingRotation));
                    SendMovingPlatformPosition(recipient, npc, position, state.HousingRotation);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Housing moving-platform runtime update failed.");
            }
        }
    }

    private static void ResetMovingPlatforms(Player recipient)
    {
        foreach (var entry in MovingPlatforms.Where(entry => entry.Key.PlayerGuid == recipient.Guid))
        {
            var state = entry.Value;
            if (!ActorGuids.TryGetValue(entry.Key, out var npcGuid) ||
                !recipient.Zone.TryGetNpc(npcGuid, out var npc))
            {
                continue;
            }

            state.LastPosition = state.BasePosition;
            npc.UpdatePosition(state.BasePosition, ToActorRotation(state.HousingRotation));
            SendMovingPlatformPosition(
                recipient,
                npc,
                state.BasePosition,
                state.HousingRotation);
        }
    }

    private static void SendMovingPlatformPosition(
        Player recipient,
        Npc npc,
        Vector4 position,
        Quaternion housingRotation)
    {
        var actorRotation = ToActorRotation(housingRotation);
        recipient.SendTunneled(new PlayerUpdatePacketUpdatePosition
        {
            Guid = npc.Guid,
            Position = position,
            Rotation = actorRotation,
            State = 0,
            Unknown = 0
        });
        recipient.SendTunneled(new HousingPacketUpdateFixturePosition
        {
            FixtureActorGuid = npc.Guid,
            Position = position,
            Rotation = ToHousingRotation(housingRotation)
        });
    }

    private static Vector4 ResolveMovingPlatformPosition(
        int itemDefinitionId,
        Vector4 basePosition,
        Quaternion housingRotation,
        float scale,
        double timelineSeconds)
    {
        var normalizedScale = NormalizeScale(scale);
        var distance = itemDefinitionId is 10342 or 10344 or 10397
            ? 20f * normalizedScale
            : itemDefinitionId == 10345
                ? 20f * normalizedScale
                : 10f * normalizedScale;
        var yaw = ToHousingRotation(housingRotation).X;
        var forward = new Vector4(MathF.Sin(yaw), 0f, MathF.Cos(yaw), 0f);

        if (itemDefinitionId == 10345)
            return basePosition + ResolveHighRiseMovingPlatformOffset(forward, distance, timelineSeconds);

        var travel = ResolvePingPongPlatformTravel(distance, timelineSeconds);
        return itemDefinitionId is 10341 or 10342 or 10396 or 10397 or 16845
            ? basePosition + new Vector4(0f, travel, 0f, 0f)
            : basePosition + (forward * travel);
    }

    private static float ResolvePingPongPlatformTravel(float distance, double timelineSeconds)
    {
        var travelSeconds = distance / MovingPlatformSpeed;
        var cycleSeconds = (travelSeconds + MovingPlatformEndpointHoldSeconds) * 2d;
        var phase = PositiveModulo(timelineSeconds, cycleSeconds);

        if (phase < MovingPlatformEndpointHoldSeconds)
            return 0f;

        phase -= MovingPlatformEndpointHoldSeconds;
        if (phase < travelSeconds)
            return distance * (float)(phase / travelSeconds);

        phase -= travelSeconds;
        if (phase < MovingPlatformEndpointHoldSeconds)
            return distance;

        phase -= MovingPlatformEndpointHoldSeconds;
        return distance * (1f - (float)(phase / travelSeconds));
    }

    private static Vector4 ResolveHighRiseMovingPlatformOffset(
        Vector4 forward,
        float distance,
        double timelineSeconds)
    {
        var points = new[]
        {
            Vector4.Zero,
            forward * distance,
            (forward * distance) + new Vector4(0f, distance, 0f, 0f)
        };
        var segmentLengths = new[] { distance, distance, distance * MathF.Sqrt(2f) };
        var cycleSeconds = segmentLengths.Sum(length => length / MovingPlatformSpeed) +
            (MovingPlatformEndpointHoldSeconds * points.Length);
        var phase = PositiveModulo(timelineSeconds, cycleSeconds);

        for (var index = 0; index < points.Length; index++)
        {
            if (phase < MovingPlatformEndpointHoldSeconds)
                return points[index];

            phase -= MovingPlatformEndpointHoldSeconds;
            var travelSeconds = segmentLengths[index] / MovingPlatformSpeed;
            var destination = points[(index + 1) % points.Length];
            if (phase < travelSeconds)
                return Vector4.Lerp(points[index], destination, (float)(phase / travelSeconds));

            phase -= travelSeconds;
        }

        return Vector4.Zero;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        var remainder = value % modulus;
        return remainder < 0d ? remainder + modulus : remainder;
    }

    private static void ReplayAnimatedFixtures(Player player, int houseId)
    {
        foreach (var runtimeKey in ActiveAnimatedFixtures.Keys.Where(key => key.HouseId == houseId))
            SendFixtureAnimation(player, houseId, runtimeKey.DatabaseFixtureId, FixtureLoopAnimationId);
    }

    private static void ReplayDefaultFixtureAnimations(Player player, int houseId)
    {
        foreach (var entry in ActorMetadata)
        {
            if (entry.Key.PlayerGuid != player.Guid ||
                entry.Key.HouseId != houseId ||
                !DatabaseFixtureIds.TryGetValue(entry.Key, out var databaseFixtureId))
            {
                continue;
            }

            var animationId = ResolveDefaultAnimationId(entry.Value.Kind, entry.Value.ItemDefinitionId);
            if (animationId != FixtureIdleAnimationId)
                RefreshFixtureActorAnimation(player, houseId, databaseFixtureId, animationId);
        }
    }

    private static void ReplayDefaultFixtureAnimation(Player player, int houseId, int databaseFixtureId)
    {
        var fixtureGuid = GetClientFixtureGuid(player.Guid, houseId, databaseFixtureId);
        var key = new FixtureActorKey(player.Guid, houseId, fixtureGuid);
        if (!ActorMetadata.TryGetValue(key, out var metadata))
            return;

        var animationId = ResolveDefaultAnimationId(metadata.Kind, metadata.ItemDefinitionId);
        if (animationId != FixtureIdleAnimationId)
            RefreshFixtureActorAnimation(player, houseId, databaseFixtureId, animationId);
    }

    private static void ActivateAnimatedFixture(
        Player sourcePlayer,
        FixtureActorKey sourceKey,
        FixtureActorMetadata metadata)
    {
        var databaseFixtureId = ResolveDatabaseFixtureId(
            sourcePlayer.Guid,
            sourceKey.HouseId,
            sourceKey.FixtureGuid);
        if (databaseFixtureId <= 0)
            return;

        switch (metadata.Kind)
        {
            case FixtureInteractionKind.TrainSet:
                ToggleLoopingFixture(sourcePlayer, sourceKey.HouseId, databaseFixtureId);
                break;
            case FixtureInteractionKind.GumballMachine:
                PlayOneShotFixtureAnimation(
                    sourcePlayer,
                    sourceKey.HouseId,
                    databaseFixtureId,
                    animationId: 2000,
                    resetDelay: TimeSpan.FromSeconds(2.25));
                break;
            case FixtureInteractionKind.Fireworks:
                PlayFixtureEffect(
                    sourcePlayer,
                    sourceKey.HouseId,
                    databaseFixtureId,
                    compositeEffectId: 5354);
                break;
        }
    }

    private static void ToggleLoopingFixture(Player sourcePlayer, int houseId, int databaseFixtureId)
    {
        var runtimeKey = new FixtureRuntimeKey(houseId, databaseFixtureId);
        var animationId = ActiveAnimatedFixtures.TryRemove(runtimeKey, out _)
            ? FixtureIdleAnimationId
            : FixtureLoopAnimationId;

        if (animationId == FixtureLoopAnimationId)
            ActiveAnimatedFixtures[runtimeKey] = 0;

        foreach (var recipient in GetHouseOccupants(sourcePlayer))
            SendFixtureAnimation(recipient, houseId, databaseFixtureId, animationId);
    }

    private static void PlayOneShotFixtureAnimation(
        Player sourcePlayer,
        int houseId,
        int databaseFixtureId,
        int animationId,
        TimeSpan resetDelay)
    {
        foreach (var recipient in GetHouseOccupants(sourcePlayer))
            SendFixtureAnimation(recipient, houseId, databaseFixtureId, animationId);

        _ = ResetFixtureAnimationAsync(sourcePlayer, houseId, databaseFixtureId, resetDelay);
    }

    private static async System.Threading.Tasks.Task ResetFixtureAnimationAsync(
        Player sourcePlayer,
        int houseId,
        int databaseFixtureId,
        TimeSpan resetDelay)
    {
        await System.Threading.Tasks.Task.Delay(resetDelay);
        foreach (var recipient in GetHouseOccupants(sourcePlayer))
        {
            if (TryGetCurrentHouseId(recipient, out var recipientHouseId) && recipientHouseId == houseId)
                SendFixtureAnimation(recipient, houseId, databaseFixtureId, animationId: 1);
        }
    }

    private static void PlayFixtureEffect(
        Player sourcePlayer,
        int houseId,
        int databaseFixtureId,
        int compositeEffectId)
    {
        foreach (var recipient in GetHouseOccupants(sourcePlayer))
        {
            var fixtureGuid = GetClientFixtureGuid(recipient.Guid, houseId, databaseFixtureId);
            var npcGuid = GetNpcGuid(recipient.Guid, houseId, fixtureGuid);
            if (npcGuid == 0 || !recipient.Zone.TryGetNpc(npcGuid, out var npc))
                continue;

            recipient.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = npcGuid,
                CompositeEffectId = compositeEffectId,
                Position = npc.Position,
                Clear = true
            });
        }
    }

    private static void SendFixtureAnimation(
        Player recipient,
        int houseId,
        int databaseFixtureId,
        int animationId)
    {
        var fixtureGuid = GetClientFixtureGuid(recipient.Guid, houseId, databaseFixtureId);
        var npcGuid = GetNpcGuid(recipient.Guid, houseId, fixtureGuid);
        if (npcGuid == 0)
            return;

        if (recipient.Zone.TryGetNpc(npcGuid, out var npc))
            npc.Animation = animationId;

        recipient.SendTunneled(new PlayerUpdatePacketSetAnimation
        {
            Guid = npcGuid,
            AnimationId = animationId
        });

        if (fixtureGuid != 0 && fixtureGuid != npcGuid)
        {
            recipient.SendTunneled(new PlayerUpdatePacketSetAnimation
            {
                Guid = fixtureGuid,
                AnimationId = animationId
            });
        }
    }

    private static void RefreshFixtureActorAnimation(
        Player recipient,
        int houseId,
        int databaseFixtureId,
        int animationId)
    {
        var fixtureGuid = GetClientFixtureGuid(recipient.Guid, houseId, databaseFixtureId);
        var npcGuid = GetNpcGuid(recipient.Guid, houseId, fixtureGuid);
        if (npcGuid == 0 || !recipient.Zone.TryGetNpc(npcGuid, out var npc))
            return;

        npc.Animation = animationId;
        npc.StandAnimId = ResolveDefaultStandAnimationId(animationId);

        recipient.SendTunneled(new PlayerUpdatePacketRemovePlayer
        {
            Guid = npcGuid
        });
        recipient.SendTunneled(CreateFixtureAddNpcPacket(npc));
        recipient.SendTunneled(new PlayerUpdatePacketUpdateIdleAnim
        {
            Guid = npcGuid,
            AnimationId = animationId
        });
        recipient.SendTunneled(new PlayerUpdatePacketSetAnimation
        {
            Guid = npcGuid,
            AnimationId = animationId
        });

        // Housing retains its own stable fixture identity in addition to the
        // linked scene actor. Latch the loop on both targets after the fixture
        // update has completed so the client cannot restore the native fixture
        // to env_loop_01 while leaving only the actor in the moving state.
        if (fixtureGuid != 0 && fixtureGuid != npcGuid)
        {
            recipient.SendTunneled(new PlayerUpdatePacketUpdateIdleAnim
            {
                Guid = fixtureGuid,
                AnimationId = animationId
            });
            recipient.SendTunneled(new PlayerUpdatePacketSetAnimation
            {
                Guid = fixtureGuid,
                AnimationId = animationId
            });
        }
    }

    private static List<(FixtureActorKey Key, FixtureActorMetadata Metadata, int DatabaseFixtureId, Npc Npc)> GetPersistedActors(
        Player player,
        int houseId)
    {
        var result = new List<(FixtureActorKey, FixtureActorMetadata, int, Npc)>();
        foreach (var metadataEntry in ActorMetadata)
        {
            var key = metadataEntry.Key;
            if (key.PlayerGuid != player.Guid || key.HouseId != houseId ||
                !DatabaseFixtureIds.TryGetValue(key, out var databaseFixtureId) ||
                !ActorGuids.TryGetValue(key, out var npcGuid) ||
                !player.Zone.TryGetNpc(npcGuid, out var npc))
            {
                continue;
            }

            result.Add((key, metadataEntry.Value, databaseFixtureId, npc));
        }

        return result;
    }

    private static FixtureInteractionKind ResolveInteractionKind(IResourceManager resourceManager, int itemDefinitionId)
    {
        if (!resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
            return FixtureInteractionKind.None;

        var modelName = itemDefinition.ModelName ?? string.Empty;
        if (itemDefinitionId is 10358 or 10362 or 10363 ||
            modelName.Contains("teleport", StringComparison.OrdinalIgnoreCase))
        {
            return FixtureInteractionKind.Teleporter;
        }

        if (modelName.Contains("train_set", StringComparison.OrdinalIgnoreCase))
            return FixtureInteractionKind.TrainSet;

        if (modelName.Contains("gumball_machine", StringComparison.OrdinalIgnoreCase))
            return FixtureInteractionKind.GumballMachine;

        if (modelName.Contains("firework", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("firecracker", StringComparison.OrdinalIgnoreCase))
        {
            return FixtureInteractionKind.Fireworks;
        }

        if (itemDefinitionId == 10451 ||
            modelName.Contains("vip_party_pool", StringComparison.OrdinalIgnoreCase))
        {
            return FixtureInteractionKind.PartyPool;
        }

        if (itemDefinitionId is 10341 or 10342 or 10343 or 10344 or 10345 or 10396 or 10397 or 16845 ||
            modelName.Contains("moving_block_01", StringComparison.OrdinalIgnoreCase))
        {
            return FixtureInteractionKind.ElevatorPlatform;
        }

        if (itemDefinitionId is 2931 or 16888 ||
            modelName.Contains("trampoline_large", StringComparison.OrdinalIgnoreCase))
        {
            return FixtureInteractionKind.LaunchPad;
        }

        return FixtureInteractionKind.None;
    }

    private static int ResolveDefaultAnimationId(FixtureInteractionKind kind, int itemDefinitionId)
    {
        if (kind == FixtureInteractionKind.ElevatorPlatform)
        {
            return itemDefinitionId switch
            {
                10345 => 2904, // Combined high-rise/escalator route.
                10342 or 10344 or 10397 => 2903, // 20-meter route.
                _ => 2902 // 10-meter route, including short and generic moving blocks.
            };
        }

        return kind is FixtureInteractionKind.Teleporter or
            FixtureInteractionKind.PartyPool
            ? FixtureLoopAnimationId
            : FixtureIdleAnimationId;
    }

    private static int ResolveDefaultStandAnimationId(int animationId)
    {
        return animationId == FixtureIdleAnimationId ? 0 : animationId;
    }

    private static bool SupportsClickInteraction(FixtureInteractionKind kind)
    {
        return kind is FixtureInteractionKind.Teleporter or
            FixtureInteractionKind.TrainSet or
            FixtureInteractionKind.GumballMachine or
            FixtureInteractionKind.Fireworks;
    }

    private static bool IsTeleporter(FixtureInteractionKind kind)
    {
        return kind == FixtureInteractionKind.Teleporter;
    }

    private static bool TryBeginInteraction(ulong playerGuid)
    {
        var now = DateTimeOffset.UtcNow;
        if (LastInteractionTimes.TryGetValue(playerGuid, out var last) && now - last < InteractionCooldown)
            return false;

        LastInteractionTimes[playerGuid] = now;
        return true;
    }

    private static bool TryGetCurrentHouseId(Player player, out int houseId)
    {
        houseId = 0;
        if (player.CurrentHouseGuid == 0)
            return false;

        try
        {
            var id = GuidHelper.GetHouseId(player.CurrentHouseGuid);
            if (id > int.MaxValue)
                return false;

            houseId = (int)id;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static float GetHorizontalDistance(Vector4 from, Vector4 to)
    {
        var deltaX = to.X - from.X;
        var deltaZ = to.Z - from.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
}
