using System;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Game.Zones;
using Sanctuary.Gateway.Admin;
using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class CommandPacketInteractRequestHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CommandPacketInteractRequestHandler));
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

        entity.OnInteract(player);

        return true;
    }
}
