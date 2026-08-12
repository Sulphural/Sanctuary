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

        // The client re-sends FreeInteractionNpc periodically while the player lingers near an
        // interactable NPC (not just on a real click); debounce repeats with the same NPC within a
        // short window so a single deliberate click doesn't fire the interaction many times.
        if (target.Guid == player.LastInteractNpcGuid && DateTime.UtcNow - player.LastInteractAt < TimeSpan.FromSeconds(3))
            return true;

        player.LastInteractNpcGuid = target.Guid;
        player.LastInteractAt = DateTime.UtcNow;

        target.OnInteract(player);

        return true;
    }
}
