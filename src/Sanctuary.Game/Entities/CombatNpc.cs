using System;
using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Entities;

/// <summary>
/// A hostile NPC that can engage players in combat.
/// Handles aggro, auto-attack, HP tracking, and death.
/// </summary>
public class CombatNpc : Npc
{
    // Combat stats
    public int CurrentHitpoints { get; set; }
    public int MaxHitpoints { get; set; }
    public int AttackDamage { get; set; }
    public int Defense { get; set; }
    public int Level { get; set; }
    public int XpReward { get; set; }

    /// <summary>Attack interval in seconds.</summary>
    public float AttackIntervalSeconds { get; set; } = 2.0f;

    /// <summary>Aggro range — distance at which NPC starts pursuing a player.</summary>
    public float AggroRange { get; set; } = 15.0f;

    /// <summary>Leash range — distance from spawn before NPC resets.</summary>
    public float LeashRange { get; set; } = 40.0f;

    /// <summary>Melee attack range.</summary>
    public float AttackRange { get; set; } = 5.0f;

    /// <summary>Movement speed when pursuing a target.</summary>
    public float CombatSpeed { get; set; } = 6.0f;

    /// <summary>Movement speed when returning to spawn.</summary>
    public float ReturnSpeed { get; set; } = 10.0f;

    // State tracking
    public Vector4 SpawnPosition { get; set; }
    public Quaternion SpawnRotation { get; set; }
    public bool IsDead { get; set; }
    public DateTime LastAttackTime { get; set; } = DateTime.MinValue;
    public DateTime DeathTime { get; set; }
    public Player? AggroTarget { get; set; }
    public CombatState State { get; set; } = CombatState.Idle;

    /// <summary>Respawn time in seconds after death.</summary>
    public float RespawnSeconds { get; set; } = 30.0f;

    /// <summary>
    /// The last position we sent to clients, to avoid sending redundant updates.
    /// </summary>
    public Vector4 LastSentPosition { get; set; }

    public CombatNpc(IZone zone) : base(zone)
    {
        Disposition = 0; // Hostile
    }

    /// <summary>
    /// Initialize combat stats based on level.
    /// </summary>
    public void InitializeFromLevel(int level)
    {
        Level = level;
        MaxHitpoints = 200 + (level * 150);
        CurrentHitpoints = MaxHitpoints;
        AttackDamage = 20 + (level * 15);
        Defense = level * 5;
        XpReward = 50 + (level * 25);
        AttackIntervalSeconds = Math.Max(1.5f, 2.5f - (level * 0.05f));
    }

    public override void UpdateEveryTick()
    {
        if (IsDead || !Visible)
            return;

        switch (State)
        {
            case CombatState.Idle:
                UpdateIdle();
                break;
            case CombatState.Pursuing:
                UpdatePursuing();
                break;
            case CombatState.Attacking:
                UpdateAttacking();
                break;
            case CombatState.Returning:
                UpdateReturning();
                break;
        }
    }

    public override void UpdateEverySecond()
    {
        if (!IsDead)
            return;

        // Check for respawn
        if ((DateTime.UtcNow - DeathTime).TotalSeconds >= RespawnSeconds)
        {
            Respawn();
        }
    }

    private void UpdateIdle()
    {
        // Look for nearby players to aggro
        var closestPlayer = FindClosestPlayer(AggroRange);

        if (closestPlayer is not null && !closestPlayer.IsDead)
        {
            AggroTarget = closestPlayer;
            State = CombatState.Pursuing;
        }
    }

    private void UpdatePursuing()
    {
        if (AggroTarget is null || AggroTarget.IsDead || !AggroTarget.Visible)
        {
            StartReturning();
            return;
        }

        // Check leash range
        var distToSpawn = DistanceTo(SpawnPosition);
        if (distToSpawn > LeashRange)
        {
            StartReturning();
            return;
        }

        var distToTarget = DistanceTo(AggroTarget.Position);

        if (distToTarget <= AttackRange)
        {
            // In attack range — switch to attacking
            State = CombatState.Attacking;
            return;
        }

        // Move towards target
        MoveTowards(AggroTarget.Position, CombatSpeed);
    }

    private void UpdateAttacking()
    {
        if (AggroTarget is null || AggroTarget.IsDead || !AggroTarget.Visible)
        {
            StartReturning();
            return;
        }

        var distToTarget = DistanceTo(AggroTarget.Position);

        // If out of attack range, pursue again
        if (distToTarget > AttackRange * 1.5f)
        {
            State = CombatState.Pursuing;
            return;
        }

        // Check leash
        var distToSpawn = DistanceTo(SpawnPosition);
        if (distToSpawn > LeashRange)
        {
            StartReturning();
            return;
        }

        // Face the target
        FaceTarget(AggroTarget.Position);

        // Auto-attack on timer
        if ((DateTime.UtcNow - LastAttackTime).TotalSeconds >= AttackIntervalSeconds)
        {
            PerformAttack(AggroTarget);
            LastAttackTime = DateTime.UtcNow;
        }
    }

    private void UpdateReturning()
    {
        var distToSpawn = DistanceTo(SpawnPosition);

        if (distToSpawn < 1.5f)
        {
            // Arrived at spawn
            UpdatePosition(SpawnPosition, SpawnRotation);
            BroadcastPositionUpdate(0); // Idle
            State = CombatState.Idle;
            AggroTarget = null;

            // Heal to full on reset
            if (CurrentHitpoints < MaxHitpoints)
            {
                CurrentHitpoints = MaxHitpoints;
                BroadcastHpUpdate();
            }
            return;
        }

        // Check if a player is nearby and re-aggro
        var closestPlayer = FindClosestPlayer(AggroRange * 0.5f);
        if (closestPlayer is not null && !closestPlayer.IsDead)
        {
            AggroTarget = closestPlayer;
            State = CombatState.Pursuing;
            return;
        }

        MoveTowards(SpawnPosition, ReturnSpeed);
    }

    private void StartReturning()
    {
        AggroTarget = null;
        State = CombatState.Returning;
    }

    private void PerformAttack(Player target)
    {
        // Calculate damage with some variance
        var random = Random.Shared;
        var variance = random.NextSingle() * 0.4f + 0.8f; // 0.8x to 1.2x
        var baseDamage = (int)(AttackDamage * variance);

        // Apply target's defense
        var defense = target.Stats[CharacterStatId.Defense].Int;
        var damageReduction = target.Stats[CharacterStatId.DamageReductionAmount].Int;
        var damageReductionPct = target.Stats[CharacterStatId.DamageReductionPercent].Int;

        var finalDamage = baseDamage - defense - damageReduction;
        if (damageReductionPct > 0)
            finalDamage = (int)(finalDamage * (1f - damageReductionPct / 100f));

        finalDamage = Math.Max(1, finalDamage); // Always deal at least 1 damage

        // Apply damage to target
        target.TakeDamage(finalDamage, this);
    }

    /// <summary>
    /// Deal damage to this NPC from a player source.
    /// </summary>
    public void TakeDamage(int amount, Player source)
    {
        if (IsDead)
            return;

        CurrentHitpoints = Math.Max(0, CurrentHitpoints - amount);

        // Broadcast HP modification (floating combat number)
        var hpMod = new PlayerUpdatePacketHitPointModification
        {
            TargetGuid = Guid,
            Amount = -amount,
            SourceGuid = source.Guid
        };

        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(hpMod);

        // Broadcast updated HP bar
        BroadcastHpUpdate();

        // Aggro switch — if we have no target or this player is closer, target them
        if (AggroTarget is null || !AggroTarget.Visible || AggroTarget.IsDead)
        {
            AggroTarget = source;
            State = CombatState.Pursuing;
        }

        if (CurrentHitpoints <= 0)
        {
            Die(source);
        }
    }

    private void Die(Player killer)
    {
        IsDead = true;
        DeathTime = DateTime.UtcNow;
        State = CombatState.Idle;
        AggroTarget = null;

        // Broadcast death
        var destroyedPacket = new PlayerUpdatePacketDestroyed
        {
            Guid = Guid,
            KillerGuid = killer.Guid,
            Unknown = 0
        };

        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(destroyedPacket);

        // Award XP to the killer
        killer.AwardXp(XpReward);

        // Remove from visibility temporarily (will respawn later in UpdateEverySecond)
        foreach (var visiblePlayer in VisiblePlayers)
            visiblePlayer.Value.OnRemoveVisibleNpcs([this]);

        Visible = false;
    }

    private void Respawn()
    {
        IsDead = false;
        CurrentHitpoints = MaxHitpoints;
        State = CombatState.Idle;
        AggroTarget = null;

        UpdatePosition(SpawnPosition, SpawnRotation);
        LastSentPosition = SpawnPosition;

        Visible = true;
        UpdateZoneTile();

        // Re-add to visible players in the tile
        foreach (var player in VisiblePlayers.Values)
            player.OnAddVisibleNpcs([this]);
    }

    private void MoveTowards(Vector4 target, float speed)
    {
        var dx = target.X - Position.X;
        var dz = target.Z - Position.Z;
        var dist = MathF.Sqrt(dx * dx + dz * dz);

        if (dist < 0.1f)
            return;

        // Calculate movement delta (tick rate is ~10 FPS = 0.1s per tick)
        var moveAmount = speed * 0.1f;
        if (moveAmount > dist)
            moveAmount = dist;

        var nx = dx / dist;
        var nz = dz / dist;

        var newPos = new Vector4(
            Position.X + nx * moveAmount,
            target.Y, // Match target Y
            Position.Z + nz * moveAmount,
            1f
        );

        // Calculate facing rotation
        var angle = MathF.Atan2(dx, dz);
        var halfAngle = angle / 2f;
        var newRot = new Quaternion(0, MathF.Sin(halfAngle), 0, MathF.Cos(halfAngle));

        UpdatePosition(newPos, newRot);

        // Broadcast position update (throttled)
        var sentDx = newPos.X - LastSentPosition.X;
        var sentDz = newPos.Z - LastSentPosition.Z;
        var sentDist = MathF.Sqrt(sentDx * sentDx + sentDz * sentDz);

        if (sentDist >= 0.3f)
        {
            byte moveState = speed > 7f ? (byte)2 : (byte)1; // 1=walk, 2=run
            BroadcastPositionUpdate(moveState);
            LastSentPosition = newPos;
        }
    }

    private void FaceTarget(Vector4 target)
    {
        var dx = target.X - Position.X;
        var dz = target.Z - Position.Z;

        var angle = MathF.Atan2(dx, dz);
        var halfAngle = angle / 2f;
        var newRot = new Quaternion(0, MathF.Sin(halfAngle), 0, MathF.Cos(halfAngle));

        UpdatePosition(Position, newRot);
    }

    private void BroadcastPositionUpdate(byte state)
    {
        var posUpdate = new PlayerUpdatePacketUpdatePosition
        {
            Guid = Guid,
            Position = Position,
            Rotation = Rotation,
            State = state,
            Unknown = 0
        };

        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(posUpdate);
    }

    private void BroadcastHpUpdate()
    {
        var hpUpdate = new PlayerUpdatePacketUpdateHitpoints
        {
            Guid = Guid,
            CurrentHitpoints = CurrentHitpoints,
            MaxHitpoints = MaxHitpoints
        };

        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(hpUpdate);
    }

    private Player? FindClosestPlayer(float maxRange)
    {
        Player? closest = null;
        float closestDist = maxRange;

        foreach (var player in VisiblePlayers.Values)
        {
            if (player.IsDead || !player.Visible)
                continue;

            var dist = DistanceTo(player.Position);

            if (dist < closestDist)
            {
                closestDist = dist;
                closest = player;
            }
        }

        return closest;
    }

    private float DistanceTo(Vector4 target)
    {
        var dx = target.X - Position.X;
        var dz = target.Z - Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public override PlayerUpdatePacketAddNpc GetAddNpcPacket()
    {
        var packet = base.GetAddNpcPacket();

        // Show health bar on hostile NPCs
        // Unknown41 appears to control health bar display
        packet.Unknown41 = true;

        return packet;
    }
}

public enum CombatState
{
    Idle,
    Pursuing,
    Attacking,
    Returning
}
