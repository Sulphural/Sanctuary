using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Core.Helpers;
using Sanctuary.Core.IO;
using Sanctuary.Database;
using Sanctuary.Database.Entities;
using Sanctuary.Game;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions;
using Sanctuary.Game.Zones;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class CommandPacketInteractRequestHandler
{
    private static ILogger _logger = null!;
    private static IDbContextFactory<DatabaseContext> _dbContextFactory = null!;
    private static IResourceManager _resourceManager = null!;

    // ★ HELD AS THE PROVIDER, NOT THE SERVICE. ConfigurePacketHandlers runs during startup, and resolving
    // a singleton there CONSTRUCTS it right then - dragging QuestManager (and its own dependencies) into
    // boot ordering that nothing previously required. Handlers only need this once a packet arrives, long
    // after everything is up, so the lookup is deferred to first use.
    private static IServiceProvider _services = null!;
    private static Sanctuary.Game.Quests.IQuestManager QuestManager =>
        _services.GetRequiredService<Sanctuary.Game.Quests.IQuestManager>();
    private static Sanctuary.Game.Collections.ICollectionManager CollectionManager =>
        _services.GetRequiredService<Sanctuary.Game.Collections.ICollectionManager>();

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CommandPacketInteractRequestHandler));
        _dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
        _resourceManager = serviceProvider.GetRequiredService<IResourceManager>();
        _services = serviceProvider;
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!CommandPacketInteractRequest.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(CommandPacketInteractRequest));
            return false;
        }

        var player = connection.Player;

        // Same guards as FreeInteractionNpc: the client can fire this on zone entry / from UI without a
        // deliberate click. Ignore interacts within the spawn grace window.
        if (player.SpawnedAt is { } spawnedAt && DateTime.UtcNow - spawnedAt < TimeSpan.FromSeconds(2))
            return true;

        // INSTANCE (Frostfang Fury): the victory EXIT DOOR (846 sg_exit_door_01, live-decoded) —
        // clicking it releases the encounter and sends the player home. This replaced the old
        // 6-second auto-kick; the player spins the loot wheel and leaves on their own time.
        if (player.Zone is FrostfangArenaZone arena && arena.IsExitDoor(packet.Guid))
        {
            arena.UseExitDoor(player);
            return true;
        }

        if (player.Zone is TormentedSpiritsArenaZone spiritArena && spiritArena.IsExitDoor(packet.Guid))
        {
            spiritArena.UseExitDoor(player);
            return true;
        }

        // Data-driven combat dungeons (DungeonCatalog) share one generic zone class.
        if (player.Zone is EncounterArenaZone encounterArena && encounterArena.IsExitDoor(packet.Guid))
        {
            encounterArena.UseExitDoor(player);
            return true;
        }

        // Housing fixtures are actors owned by the placement service rather than zone entities, so they
        // have to be resolved before the zone lookup below (which would miss them entirely).
        if (HousingFixtureActorService.TryHandleInteraction(player, packet.Guid))
            return true;

        if (!player.Zone.TryGetEntity(packet.Guid, out var entity))
            return true;

        // Enforce the NPC's interact range here too (this path resolves by guid and would otherwise
        // let a click land from any distance), so the "must be next to the NPC" rule holds regardless
        // of which interact packet the client sends.
        if (entity is Npc npc)
        {
            var playerPosition = new Vector3(player.Position.X, player.Position.Y, player.Position.Z);
            var npcPosition = new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z);

            if (Vector3.Distance(playerPosition, npcPosition) > npc.InteractRange)
                return true;
        }

        if (packet.Guid == player.LastInteractNpcGuid && DateTime.UtcNow - player.LastInteractAt < TimeSpan.FromSeconds(3))
            return true;

        player.LastInteractNpcGuid = packet.Guid;
        player.LastInteractAt = DateTime.UtcNow;

        // INSTANCE WIP (Frostfang Fury): clicking the Frostfang Growler wolf opens the adventure offer popup
        // (EncounterDetailsResponsePacket). The interaction provides the encounter context the cold "!offer"
        // test lacked.
        if (player.Zone is StartingZone startingZone
            && startingZone.GrowlerWolf is { } growler
            && growler.Guid == packet.Guid)
        {
            _logger.LogInformation("InteractRequest: Frostfang Growler ({guid}) clicked -> sending offer popup.",
                packet.Guid);

            // Encounter state machine (op41/sub106, mirrored from the live 2014-04-01 capture): the
            // real server steps 2 -> 3 -> 4 BEFORE the offer details, 5 with the ready ack, 6 in-zone.
            foreach (var state in new[] { 2, 3, 4 })
            {
                connection.SendTunneled(new EncounterStatePacket
                {
                    EncounterId = FrostfangArenaZone.EncounterId,
                    InstanceId = FrostfangArenaZone.EncounterInstanceId,
                    State = state,
                });
            }

            connection.SendTunneled(new EncounterDetailsResponsePacket
            {
                // Header ints = [EncounterId][InstanceId] on the live wire (details + state + PlayerEnter
                // all share them).
                Unknown = FrostfangArenaZone.EncounterId,
                Unknown2 = FrostfangArenaZone.EncounterInstanceId,
                // REAL ids from the team's minigame branch: Resources/ClientActivityDefinitions.json, activity
                // Id 174 "Frostfang Growler!" (Category 99 = wandering combat encounter, ServerType 1 = world/arena
                // launch).
                NameId = 93276,                       // "Frostfang Growler!"  (ClientActivityDefinitions Id 174)
                DescriptionId = 104171,               // Growler description
                Difficulty = 1,                       // 1 of 5 pips (matches the def)
                IconId = 1345,                        // wolf emblem ImageSetId (real launch used 28605; 1345 is live-proven here)
                MiniGameType = 4,                     // COMBAT (matches the real packet; the launch copy already sends it)
                // ★ PRIZES on the talk popup (2026-07-04): the popup's reward list renders from the
                // PREVIEW reward bundle ("BaseClient.MiniGame.RewardPreview.Entries", up to 4 non-hidden
                // rows). Set picked for the player's ACTIVE JOB server-side — live behavior; the packet
                // carries only the job CATEGORY (ProfileType 2 = combat jobs).
                PreviewRewards = FrostfangArenaZone.GetPrizePreviewFor(player),
                PreviewCoins = FrostfangArenaZone.PrizeCoins,
                PreviewXp = FrostfangArenaZone.PrizeXp,
                ProfileType = FrostfangArenaZone.CombatProfileType,
                ActivityId = FrostfangArenaZone.EncounterId,
            });

            // Auto-complete the ready handshake (sub107 -> "HandlerMiniGameStart:setReady") shortly after the
            // popup opens: the spinner flips to the green GO! without needing the "!ready" chat command.
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                connection.SendTunneled(new EncounterZoneIsReadyPacket());
                // Live order: state 5 lands right after the ready ack (04-01 seq 27148 -> 27150).
                connection.SendTunneled(new EncounterStatePacket
                {
                    EncounterId = FrostfangArenaZone.EncounterId,
                    InstanceId = FrostfangArenaZone.EncounterInstanceId,
                    State = 5,
                });
            });

            return true;
        }

        // INSTANCE (Tormented Spirits!): clicking THE single entrance spirit wandering the Blackspore
        // graveyard opens the encounter 146 offer popup — the same wandering-encounter pattern as the
        // Growler wolf. Only the one designated entrance spirit opens the offer; the other graveyard
        // spirits are hostile world enemies (fought, not clicked-to-enter).
        if (player.Zone is StartingZone spiritZone
            && spiritZone.SpiritEntranceGuid != 0
            && packet.Guid == spiritZone.SpiritEntranceGuid)
        {
            _logger.LogInformation("InteractRequest: Tormented Spirit ({guid}) clicked -> sending offer popup.",
                packet.Guid);

            foreach (var state in new[] { 2, 3, 4 })
            {
                connection.SendTunneled(new EncounterStatePacket
                {
                    EncounterId = TormentedSpiritsArenaZone.EncounterId,
                    InstanceId = TormentedSpiritsArenaZone.EncounterInstanceId,
                    State = state,
                });
            }

            connection.SendTunneled(new EncounterDetailsResponsePacket
            {
                Unknown = TormentedSpiritsArenaZone.EncounterId,
                Unknown2 = TormentedSpiritsArenaZone.EncounterInstanceId,
                NameId = TormentedSpiritsArenaZone.TitleNameId,           // "Tormented Spirits!"
                DescriptionId = TormentedSpiritsArenaZone.DescriptionId,  // "...put them to rest!"
                Difficulty = TormentedSpiritsArenaZone.Difficulty,
                IconId = TormentedSpiritsArenaZone.IconId,
                MiniGameType = 4, // COMBAT
                PreviewRewards = FrostfangArenaZone.GetPrizePreviewFor(player),
                PreviewCoins = FrostfangArenaZone.PrizeCoins,
                PreviewXp = FrostfangArenaZone.PrizeXp,
                ProfileType = FrostfangArenaZone.CombatProfileType,
                ActivityId = TormentedSpiritsArenaZone.EncounterId,
            });

            // Same auto-ready handshake as the Growler popup (spinner -> green GO!).
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                connection.SendTunneled(new EncounterZoneIsReadyPacket());
                connection.SendTunneled(new EncounterStatePacket
                {
                    EncounterId = TormentedSpiritsArenaZone.EncounterId,
                    InstanceId = TormentedSpiritsArenaZone.EncounterInstanceId,
                    State = 5,
                });
            });

            return true;
        }

        if (entity is CollectionNode collectionNode)
            return HandleCollectionNode(connection, collectionNode);

        entity.OnInteract(player);
        return true;
    }

    private static bool HandleCollectionNode(GatewayConnection connection, CollectionNode node)
    {
        var playerPosition = connection.Player.Position;
        var nodePosition = node.Position;
        var distanceSquared = Vector3.DistanceSquared(
            new Vector3(playerPosition.X, playerPosition.Y, playerPosition.Z),
            new Vector3(nodePosition.X, nodePosition.Y, nodePosition.Z));

        if (distanceSquared > node.InteractRange * node.InteractRange)
            return true;

        if (!node.TryReserve())
            return true;

        var itemPersisted = false;
        var nodeCompleted = false;

        try
        {
            var drop = node.TypeDefinition.Table.SelectRandom();
            var itemDefinitionId = drop.ItemDefinitionId;

            if (!_resourceManager.ClientItemDefinitions.TryGetValue(itemDefinitionId, out var itemDefinition))
            {
                _logger.LogError("Collection node type {type} references unknown item definition {itemDefinitionId}.",
                    node.TypeDefinition.Key, itemDefinitionId);
                node.Release();
                return true;
            }

            var ownedItemDefinitionIds = connection.Player.Items
                .Select(item => item.Definition)
                .ToHashSet();
            var collectionMatch = FindCollectionEntry(itemDefinitionId);
            var collectionWasStarted = collectionMatch is not null &&
                collectionMatch.Value.Definition.IsStarted(ownedItemDefinitionIds);
            var collectionEntryWasCollected = ownedItemDefinitionIds.Contains(itemDefinitionId);

            var characterId = GuidHelper.GetPlayerId(connection.Player.Guid);

            using var dbContext = _dbContextFactory.CreateDbContext();
            var dbCharacter = dbContext.Characters
                .Include(character => character.Items)
                .SingleOrDefault(character => character.Id == characterId);

            if (dbCharacter is null)
            {
                node.Release();
                return true;
            }

            var dbItem = dbCharacter.Items.SingleOrDefault(item => item.Definition == itemDefinitionId && item.Tint == 0);

            if (dbItem is null)
            {
                dbItem = new DbItem
                {
                    Id = dbCharacter.Items.Select(item => item.Id).DefaultIfEmpty(0).Max() + 1,
                    Definition = itemDefinitionId,
                    Count = 1,
                    Tint = 0
                };

                dbCharacter.Items.Add(dbItem);
            }
            else
            {
                dbItem.Count++;
            }

            if (dbContext.SaveChanges() <= 0)
            {
                node.Release();
                return true;
            }

            itemPersisted = true;

            var clientItem = connection.Player.Items.SingleOrDefault(item =>
                item.Definition == itemDefinitionId && item.Tint == 0);

            if (clientItem is null)
            {
                clientItem = new ClientItem
                {
                    Id = dbItem.Id,
                    Definition = dbItem.Definition,
                    Count = dbItem.Count,
                    Tint = dbItem.Tint
                };

                connection.Player.Items.Add(clientItem);

                using var writer = new PacketWriter();
                clientItem.Serialize(writer);
                itemDefinition.Serialize(writer);

                connection.SendTunneled(new ClientUpdatePacketItemAdd { Payload = writer.Buffer });
            }
            else
            {
                clientItem.Count = dbItem.Count;
                connection.SendTunneled(new ClientUpdatePacketItemUpdate
                {
                    ItemGuid = clientItem.Id,
                    Count = clientItem.Count
                });
            }

            ownedItemDefinitionIds.Add(itemDefinitionId);

            node.CompleteCollection();
            nodeCompleted = true;

            if (collectionMatch is not null && !collectionEntryWasCollected)
            {
                if (!collectionWasStarted)
                    SendCollectionStart(connection, collectionMatch.Value.Definition, ownedItemDefinitionIds);

                SendCollectionEntryUpdate(connection, collectionMatch.Value.Definition,
                    collectionMatch.Value.Entry, collectionMatch.Value.Index);
            }
            else
            {
                SendCollectionRewardToast(connection, clientItem, itemDefinition);
            }

            // Did that pickup finish a collection? Pays the collection's job its XP plus any coins/items.
            // Guarded like the quest credit below: the gather is already committed, so a reward fault must
            // not be reported as a failed gather.
            try
            {
                CollectionManager.OnItemCollected(connection.Player, itemDefinitionId);
            }
            catch (Exception collectionEx)
            {
                _logger.LogError(collectionEx, "Collection item {itemDefinitionId} was granted but the " +
                    "collection completion check failed.", itemDefinitionId);
            }

            // ★★ QUEST CREDIT GOES LAST, AND IN ITS OWN GUARD. A gathered node can also be a quest
            // objective - a Collect goal whose CollectNodeType names this node type - which is what lets a
            // quest use the pooled/respawning collection-node system instead of fixed pickups.
            //
            // It must NOT sit inside the gather's own try/catch: that block's failure path releases or
            // completes the node and returns false, so a fault on the QUEST side would be reported as a
            // failed gather and, worse, swallowed silently - the player gets the item and the node is
            // consumed while the goal never ticks. The gather is already committed by this point; a quest
            // problem is logged and nothing else is undone.
            try
            {
                QuestManager.OnCollectionNodeGathered(connection.Player, node.TypeDefinition.Key);
            }
            catch (Exception questEx)
            {
                _logger.LogError(questEx, "Collection node {type} was gathered but crediting the quest failed.",
                    node.TypeDefinition.Key);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect node {nodeId} ({type}).", node.SpawnDefinition.Id, node.TypeDefinition.Key);

            if (itemPersisted && !nodeCompleted)
                node.CompleteCollection();
            else if (!itemPersisted)
                node.Release();

            return false;
        }
    }

    private static CollectionEntryMatch? FindCollectionEntry(int itemDefinitionId)
    {
        foreach (var definition in _resourceManager.Collections.Values)
        {
            for (var index = 0; index < definition.Entries.Count; index++)
            {
                var entry = definition.Entries[index];

                if (entry.ItemDefinitionId == itemDefinitionId)
                    return new CollectionEntryMatch(definition, entry, index);
            }
        }

        return null;
    }

    private static void SendCollectionStart(GatewayConnection connection, CollectionDefinition definition,
        IReadOnlySet<int> ownedItemDefinitionIds)
    {
        var collection = definition.CreateClientCollection(connection.Player.Guid, ownedItemDefinitionIds);

        using var writer = new PacketWriter();
        collection.Serialize(writer);

        connection.SendTunneled(new ClientUpdatePacketCollectionStart { Payload = writer.Buffer });
    }

    private static void SendCollectionEntryUpdate(GatewayConnection connection, CollectionDefinition definition,
        CollectionEntryDefinition entryDefinition, int index)
    {
        var entry = definition.CreateClientCollectionEntry(entryDefinition, index, true);

        connection.SendTunneled(new ClientUpdatePacketCollectionAddEntry
        {
            DefinitionId = entry.DefinitionId,
            IconId = entry.IconId,
            IconTintId = entry.IconTintId,
            NameId = entry.NameId,
            CollectionId = entry.CollectionId,
            Index = entry.Index,
            Unknown = entry.Unknown,
            Collected = entry.Collected
        });
    }

    private readonly record struct CollectionEntryMatch(
        CollectionDefinition Definition,
        CollectionEntryDefinition Entry,
        int Index);

    // A real grant, not a preview: the banner icon/name ride in the bundle's IconId/NameId, and the
    // granted row goes in the entry list carrying the player's inventory item id, gated by the bundle's
    // lead byte. Upstream's own version XORed the node guid with Environment.TickCount into SourceGuid;
    // that produces neither a valid guid nor a stable id, so it is deliberately not carried over.
    private static void SendCollectionRewardToast(GatewayConnection connection, ClientItem clientItem,
        ClientItemDefinition itemDefinition)
    {
        connection.SendTunneled(new RewardBundlePacket
        {
            RewardBundle =
            {
                CarriesItemGuids = true,
                PlayerGuid = connection.Player.Guid,
                IconId = itemDefinition.Icon.Id,
                NameId = itemDefinition.NameId,
                Entries =
                {
                    new RewardBundleEntryItem
                    {
                        IconId = itemDefinition.Icon.Id,
                        TintId = clientItem.Tint,
                        NameId = itemDefinition.NameId,
                        Quantity = 1,
                        DefinitionId = clientItem.Definition,
                        ItemGuid = clientItem.Id
                    }
                }
            }
        });
    }
}
