using System;
using System.Linq;
using System.Numerics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Packet;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

[PacketHandler]
public static class CommandPacketFreeInteractionNpcHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CommandPacketFreeInteractionNpcHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!CommandPacketFreeInteractionNpc.TryDeserialize(data, out _))
        {
            _logger.LogError("Failed to deserialize {packet}.", nameof(CommandPacketFreeInteractionNpc));
            return false;
        }

        var player = connection.Player;

        // The client auto-fires this packet on zone entry if the player's saved position happens
        // to be within an NPC's interact range - ignore interacts within a short grace period after
        // spawning so only a real click (well after the player regains control) is honored.
        if (player.SpawnedAt is { } spawnedAt && DateTime.UtcNow - spawnedAt < TimeSpan.FromSeconds(2))
            return true;

        // Resolve the nearest interactable NPC that is within its interact range. InteractRange is
        // tuned to match the client's "Press X to interact" prompt distance so a click only lands
        // when the player is genuinely next to the NPC (not from across the plaza).
        var playerPosition = new Vector3(player.Position.X, player.Position.Y, player.Position.Z);

        // ★ RE-ARM THE LATCH THE MOMENT THE PLAYER IS OUT OF REACH of whoever it is holding. Done before
        // the target is resolved, so walking away from an NPC is all it takes to be able to trigger them
        // again - and so the latch can never outlive the approach that set it (an NPC that despawned or
        // dropped out of view releases it too).
        if (player.AutoInteractLatchGuid != 0)
        {
            var latched = player.VisibleNpcs.TryGetValue(player.AutoInteractLatchGuid, out var held) ? held : null;
            var stillInReach = latched is not null
                && Vector3.Distance(playerPosition, new Vector3(latched.Position.X, latched.Position.Y, latched.Position.Z))
                    <= latched.InteractRange;

            if (!stillInReach)
                player.AutoInteractLatchGuid = 0;
        }

        var target = player.VisibleNpcs.Values
            .Where(npc => npc.IsInteractable && npc.HasInteraction)
            .Select(npc => new
            {
                Npc = npc,
                Distance = Vector3.Distance(playerPosition, new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z))
            })
            .Where(x => x.Distance <= x.Npc.InteractRange)
            .OrderBy(x => x.Distance)
            .Select(x => x.Npc)
            .FirstOrDefault();

        if (target is null)
            return true;

        // ★★ ONE AUTO-INTERACTION PER APPROACH, and a time-based debounce is NOT enough for this.
        //
        // 26/20 is not a click. It carries no guid, and the client emits it on UI events as well as on
        // proximity - closing a panel or pressing a HUD button is enough - so an NPC whose interaction
        // opens a conversation re-opened it every single time the player touched the interface while
        // standing next to him. That is the "Calvin Coldcastle's dialog keeps coming back" report, and the
        // 3-second window below could never have caught it: closing the matchmaking panel happens long
        // after 3 seconds, and the offending pings arrive minutes apart.
        //
        // So the packet is treated as what it actually is - "the nearest interactable NPC is X" - and
        // acted on only when that is NEWS: once per approach. The latch is released as soon as the player
        // steps out of the NPC's reach (above), or when someone else becomes the nearest NPC, so walking
        // away and coming back still starts a fresh conversation.
        //
        // ★ Deliberate clicks are unaffected: those arrive as CommandPacketInteractRequest, which carries
        // a real guid and keeps its own 3-second debounce. So an NPC or a pile can still be clicked over
        // and over without moving - only the AUTOMATIC trigger is once-per-approach.
        //
        // KNOWN GAP: if the client sends no 26/20 at all between leaving an NPC and returning to him, the
        // latch is still held on arrival and the automatic greeting is skipped for that return - clicking
        // him works as always. In practice the client pings far too eagerly for this to come up; it is
        // recorded here so a "he didn't greet me the second time" report has an explanation to check.
        if (target.Guid == player.AutoInteractLatchGuid)
            return true;

        // Kept alongside the latch: this pair is shared with the click path, and a click arriving right
        // after an auto-interaction should still be debounced as the same interaction.
        if (target.Guid == player.LastInteractNpcGuid && DateTime.UtcNow - player.LastInteractAt < TimeSpan.FromSeconds(3))
            return true;

        player.AutoInteractLatchGuid = target.Guid;
        player.LastInteractNpcGuid = target.Guid;
        player.LastInteractAt = DateTime.UtcNow;

        target.OnInteract(player);

        return true;
    }
}
