using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

/// <summary>
/// Handles player auto-attack requests against NPCs.
/// Calculates melee damage using player stats and deals damage to the target CombatNpc.
/// </summary>
[PacketHandler]
public static class CombatPacketAutoAttackTargetHandler
{
    private static ILogger _logger = null!;

    public static void ConfigureServices(IServiceProvider serviceProvider)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger(nameof(CombatPacketAutoAttackTargetHandler));
    }

    public static bool HandlePacket(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!CombatPacketAutoAttackTarget.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize CombatPacketAutoAttackTarget.");
            return false;
        }

        var player = connection.Player;

        if (player.IsDead)
            return true;

        // Find the target NPC
        if (!player.Zone.TryGetNpc(packet.TargetGuid, out var npc))
        {
            _logger.LogDebug("Auto-attack target {guid} not found.", packet.TargetGuid);
            return true;
        }

        // Only attack CombatNpcs
        if (npc is not CombatNpc combatNpc)
        {
            _logger.LogDebug("Auto-attack target {guid} is not a combat NPC.", packet.TargetGuid);
            return true;
        }

        if (combatNpc.IsDead)
            return true;

        // Check range
        var dx = player.Position.X - combatNpc.Position.X;
        var dz = player.Position.Z - combatNpc.Position.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);

        var weaponRange = player.Stats[CharacterStatId.WeaponRange].Float;
        var rangeMultiplier = player.Stats[CharacterStatId.RangeMultiplier].Float;
        var effectiveRange = weaponRange * rangeMultiplier;

        if (distance > effectiveRange * 2f) // Allow some slack for client-server position desync
        {
            _logger.LogDebug("Auto-attack target too far. Distance: {distance}, Range: {range}", distance, effectiveRange);
            return true;
        }

        // Enter combat
        player.InCombat = true;
        player.LastCombatTime = DateTime.UtcNow;
        player.CombatTargetGuid = combatNpc.Guid;

        // Calculate damage
        var damage = CalculateMeleeDamage(player);

        // Deal damage to the NPC
        combatNpc.TakeDamage(damage, player);

        // Send attack damage feedback to the player
        var attackDamage = new CombatPacketAttackTargetDamage
        {
            AttackerGuid = player.Guid,
            TargetGuid = combatNpc.Guid,
            Damage = damage
        };

        player.SendTunneled(attackDamage);

        // Send attack processed
        var attackProcessed = new CombatPacketAttackProcessed();
        player.SendTunneled(attackProcessed);

        return true;
    }

    private static int CalculateMeleeDamage(Player player)
    {
        var random = Random.Shared;

        // Base damage from stats
        var weaponDamage = player.Stats[CharacterStatId.EquippedMeleeWeaponDamage].Int;
        var handToHandDamage = player.Stats[CharacterStatId.MeleeHandToHandDamage].Int;
        var damageMultiplier = player.Stats[CharacterStatId.DamageMultiplier].Float;
        var weaponDamageMultiplier = player.Stats[CharacterStatId.MeleeWeaponDamageMultiplier].Float;
        var damageAddition = player.Stats[CharacterStatId.DamageAddition].Int;

        var baseDamage = Math.Max(weaponDamage, handToHandDamage);
        var totalDamage = (int)((baseDamage + damageAddition) * damageMultiplier * weaponDamageMultiplier);

        // Add variance (±20%)
        var variance = 0.8f + random.NextSingle() * 0.4f;
        totalDamage = (int)(totalDamage * variance);

        // Check for critical hit
        var critChance = player.Stats[CharacterStatId.MeleeCriticalHitChance].Int;
        var critMultiplier = player.Stats[CharacterStatId.MeleeCriticalHitMultiplier].Float;

        if (critChance > 0 && random.Next(100) < critChance)
        {
            var effectiveMultiplier = critMultiplier > 0 ? critMultiplier : 2.0f;
            totalDamage = (int)(totalDamage * effectiveMultiplier);
        }

        return Math.Max(1, totalDamage);
    }
}
