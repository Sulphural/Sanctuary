using System;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sanctuary.Game.Combat;
using Sanctuary.Game.Entities;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;
using Sanctuary.Packet.Common.Attributes;

namespace Sanctuary.Gateway.Handlers;

// Handles player auto-attack requests against NPCs.
// Calculates melee damage using player stats and deals damage to the target CombatNpc.
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

        return ExecuteBasicAttack(connection, packet.TargetGuid);
    }

    // op32/3 — same request shape as auto-attack but a single non-repeating swing. Server-side the
    // resolution is identical (the client owns the repeat cadence for auto-attack), so both routes share
    // ExecuteBasicAttack.
    public static bool HandleSingleAttack(GatewayConnection connection, ReadOnlySpan<byte> data)
    {
        if (!CombatPacketSingleAttackTarget.TryDeserialize(data, out var packet))
        {
            _logger.LogError("Failed to deserialize CombatPacketSingleAttackTarget.");
            return false;
        }

        return ExecuteBasicAttack(connection, packet.TargetGuid);
    }

    private static bool ExecuteBasicAttack(GatewayConnection connection, ulong targetGuid)
    {
        var player = connection.Player;

        if (player.IsDead)
            return true;

        // Find the target NPC
        if (!player.Zone.TryGetNpc(targetGuid, out var npc))
        {
            _logger.LogDebug("Attack target {guid} not found.", targetGuid);
            return true;
        }

        // Only attack CombatNpcs
        if (npc is not CombatNpc combatNpc)
        {
            _logger.LogDebug("Attack target {guid} is not a combat NPC.", targetGuid);
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

        // Pace + sync this swing exactly like a basic attack fired from the toolbar (op36 slot 0) - same
        // per-weapon swing length gate, same "damage lands as the swing connects" delay. Before this, a
        // direct click-to-attack (this handler) landed damage INSTANTLY with no pace gate at all, while the
        // toolbar path had a real wind-up - the same weapon felt like two different weapons depending on
        // which input triggered the swing. TryGateBasicSwing shares its pace-tracking with the toolbar path,
        // so alternating between click-attack and toolbar-attack can't be used to out-pace either one.
        var basicAbility = JobWeaponAbilities.ResolveAbility(player, 0);
        if (!AbilityPacketClientRequestStartAbilityHandler.TryGateBasicSwing(player, basicAbility.Animation, out var damageDelay))
            return true; // still mid-swing — drop this extra click, no damage

        // Enter combat
        player.InCombat = true;
        player.LastCombatTime = DateTime.UtcNow;
        player.CombatTargetGuid = combatNpc.Guid;
        player.EnterWorldCombat(); // opens the client's floating-damage-number gate (op41 sub132/133)

        // Calculate damage now (deterministic per swing), apply it once the delay elapses (as the swing
        // connects), matching AbilityPacketClientRequestStartAbilityHandler.ResolveDamageAfterCast's pattern.
        var damage = CalculateMeleeDamage(player);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)(damageDelay * 1000));

                if (!combatNpc.IsAlive || player.IsDead)
                    return; // target died to an earlier hit, or the attacker went down mid-swing

                // Deal damage. The hit feedback rides op32/7 below, so suppress TakeDamage's own 35/35
                // HitPointModification broadcast — sending both draws the floating number twice.
                combatNpc.TakeDamage(damage, player, broadcastHitNumber: false);

                // Damage-dealt confirmation to the attacking client (op32/4).
                player.SendTunneled(new CombatPacketAttackTargetDamage
                {
                    AttackerGuid = player.Guid,
                    TargetGuid = combatNpc.Guid,
                    Damage = damage
                });

                // Per-hit feedback (op32/7): attacker plays the contact event (and, for the local player, the
                // action-bar melee cooldown reset — correct here, this IS the melee swing); target shows the
                // floating -Damage number, health bar, recoil. Broadcast so bystanders see the exchange too.
                player.SendTunneledToVisible(new CombatPacketAttackProcessed
                {
                    AttackerGuid = player.Guid,
                    TargetGuid = combatNpc.Guid,
                    Damage = damage,
                    MaxHealth = combatNpc.MaxHitpoints,
                    CurrentHealth = combatNpc.CurrentHitpoints,
                }, sendToSelf: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delayed basic-attack resolution failed.");
            }
        });

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
