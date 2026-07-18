using System;
using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Game.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Entities;

// A hostile NPC that can engage players in combat.
// Handles aggro, auto-attack, HP tracking, and death.
public class CombatNpc : Npc
{
    // Combat stats
    public int CurrentHitpoints { get; set; }
    public int MaxHitpoints { get; set; }
    public int AttackDamage { get; set; }
    public int Defense { get; set; }
    public int Level { get; set; }
    public int XpReward { get; set; }

    // Attack interval in seconds.
    public float AttackIntervalSeconds { get; set; } = 2.0f;

    // Aggro range — distance at which NPC starts pursuing a player.
    public float AggroRange { get; set; } = 15.0f;

    // Leash range — distance from spawn before NPC resets.
    public float LeashRange { get; set; } = 40.0f;

    // Melee attack range.
    public float AttackRange { get; set; } = 5.0f;

    // Movement speed when pursuing a target.
    public float CombatSpeed { get; set; } = 6.0f;

    // Movement speed when returning to spawn. Kept equal to CombatSpeed so the pace
    // (and the ExpectedSpeed we stream to clients) never jumps between chasing and evading — a mid-return
    // speed change made the client re-interpolate and stutter.
    public float ReturnSpeed { get; set; } = 6.0f;

    // State tracking
    public Vector4 SpawnPosition { get; set; }
    public Quaternion SpawnRotation { get; set; }
    public bool IsDead { get; set; }
    public DateTime LastAttackTime { get; set; } = DateTime.MinValue;
    public DateTime DeathTime { get; set; }
    public Player? AggroTarget { get; set; }
    public CombatState State { get; set; } = CombatState.Idle;

    // Respawn time in seconds after death.
    public float RespawnSeconds { get; set; } = 30.0f;

    // The last position we sent to clients, to avoid sending redundant updates.
    public Vector4 LastSentPosition { get; set; }

    // The last ExpectedSpeed we told clients — so we only re-broadcast when the pace changes
    // (chase vs return). A PHYSICS/CONTROLLER actor with no ExpectedSpeed snaps to each position update
    // (the "flying/sliding" look) instead of running smoothly along the ground.
    public float LastSentExpectedSpeed { get; set; } = -1f;

    // Models whose swing the client's default attack-contact event does NOT drive — their
    // animation network lacks the standard melee state, so they deal damage while standing frozen. For
    // these we explicitly stream a SetAnimation swing (op35/8) on each hit. Maps ModelId -> AnimationGroup
    // id: com_swing (1099, itself falling back to com_h2h_attack 1000) is the generic creature melee swing.
    // The Abominable Snowman boss (1944, snowmanboss.adr — a winter-event model) is the known offender.
    // Shared so both the overworld PerformAttack and the dungeon claw loop use one source.
    public static readonly IReadOnlyDictionary<int, int> ExplicitAttackAnimByModel = new Dictionary<int, int>
    {
        [1944] = 1099, // Abominable Snowman -> com_swing
    };

    public CombatNpc(IZone zone) : base(zone)
    {
        Disposition = 0; // Hostile
    }

    // Initialize combat stats based on level.
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
            // In attack range — stop cleanly (tell clients speed 0 + an idle-state position) so the model
            // plants instead of coasting past on its last ExpectedSpeed, then switch to attacking.
            BroadcastStop();
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

        // Arrived home. Threshold is tight (0.5) so the final settle is imperceptible instead of the old
        // hard 1.5-unit teleport-snap; MoveTowards caps its step to the remaining distance so we land clean.
        if (distToSpawn < 0.5f)
        {
            UpdatePosition(SpawnPosition, SpawnRotation);
            BroadcastStop(); // speed 0 + idle-state position
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

        // Re-aggro ONLY once we're back within leash of spawn. Re-aggroing while still far out just bounces
        // the NPC straight back into a leash-reset (Pursuing -> over-leash -> Returning ...), which is the
        // jittery back-and-forth that made evading enemies "glitch a lot" when a player chased them home.
        if (distToSpawn <= LeashRange * 0.8f)
        {
            var closestPlayer = FindClosestPlayer(AggroRange * 0.5f);
            if (closestPlayer is not null && !closestPlayer.IsDead)
            {
                AggroTarget = closestPlayer;
                State = CombatState.Pursuing;
                return;
            }
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
        // Never keep hitting a downed player — extra combat packets on a 0-HP target make the overworld
        // client bounce the HP bar back up (there's no death state out here to absorb them).
        if (target.IsDead)
            return;

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

        // DODGE (base avoidance + Archer's Reflexes): the player evades — the AttackTargetDodged packet renders the
        // "Dodge" text + plays our swing, and the com_dodge sidestep layers on top; no damage is dealt. Models
        // whose default contact event doesn't animate still need their explicit swing clip.
        if (target.TryDodgeIncomingAttack(Guid))
        {
            if (ExplicitAttackAnimByModel.TryGetValue(ModelId, out var missAnim))
                foreach (var p in VisiblePlayers.Values)
                    p.SendTunneled(new PlayerUpdatePacketSetAnimation { Guid = Guid, AnimationId = missAnim });
            return;
        }

        finalDamage = target.ReduceIncomingDamage(finalDamage); // Ninja Shrouded Armor

        // Apply damage to target (server-authoritative HP + the player's own HP-bar packet).
        target.TakeDamage(finalDamage, this);

        // VISUAL: tell every nearby client to play OUR attack-contact event — the model's swing/bite clip —
        // and pop the floating damage number + recoil on the target. Without this the enemy dealt damage
        // with no animation (the "Cray Snapper has no attack animation" report). CombatPacketAttackProcessed:
        // the attacker guid plays the swing; the target guid takes the number/bar/recoil/hit FX. CurrentHealth
        // is the post-hit value so the bar it drives matches the HP packet TakeDamage just sent.
        var attack = new CombatPacketAttackProcessed
        {
            AttackerGuid = Guid,
            TargetGuid = target.Guid,
            Damage = finalDamage,
            MaxHealth = target.Stats[CharacterStatId.MaxHealth].Int,
            CurrentHealth = target.CurrentHitpoints,
            CompositeEffectId = 0,
        };

        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(attack);

        // Warrior Counterattack (L20): reflect a share of the hit back at us (the attacker). Pops a floating
        // number on us via a second AttackProcessed with the roles swapped.
        if (Combat.WarriorWeaponAbilities.HasTrait(target, Combat.WarriorWeaponAbilities.CounterattackLevel))
        {
            var reflect = System.Math.Max(1, (int)(finalDamage * Combat.WarriorWeaponAbilities.CounterattackReflectPercent));
            ApplyDamage(reflect);
            var counter = new CombatPacketAttackProcessed
            {
                AttackerGuid = target.Guid,
                TargetGuid = Guid,
                Damage = reflect,
                MaxHealth = MaxHealth,
                CurrentHealth = Health,
                CompositeEffectId = 0,
            };
            foreach (var player in VisiblePlayers.Values)
                player.SendTunneled(counter);
        }

        // Models whose default combat-contact event doesn't animate get an explicit swing clip so they
        // don't hit while frozen (e.g. the Abominable Snowman boss).
        if (ExplicitAttackAnimByModel.TryGetValue(ModelId, out var swingAnimId))
        {
            var swing = new PlayerUpdatePacketSetAnimation { Guid = Guid, AnimationId = swingAnimId };
            foreach (var player in VisiblePlayers.Values)
                player.SendTunneled(swing);
        }
    }

    // Force this enemy to engage a player without dealing damage — used when the player's own
    // ability handler applied the hit (via Npc.Health) so we skip our internal HP path but still react.
    // Also covers a player attacking from outside aggro range.
    public void AggroOnto(Player source)
    {
        if (IsDead || source.IsDead)
            return;

        if (AggroTarget is null || !AggroTarget.Visible || AggroTarget.IsDead)
        {
            AggroTarget = source;
            if (State is CombatState.Idle or CombatState.Returning)
                State = CombatState.Pursuing;
        }
    }

    // Deal damage to this NPC from a player source.
    public void TakeDamage(int amount, Player source)
    {
        if (IsDead)
            return;

        CurrentHitpoints = Math.Max(0, CurrentHitpoints - amount);

        // Broadcast HP modification (floating combat number). Field mapping per the IDA-confirmed
        // wire format: Guid = ATTACKER, Guid2 = VICTIM, Unknown2 = max HP, Unknown3 = current HP
        // after the hit, Unknown4 = delta (-damage = the floating number).
        var hpMod = new PlayerUpdatePacketHitPointModification
        {
            Guid = source.Guid,
            Guid2 = Guid,
            Unknown = true,
            Unknown2 = MaxHitpoints,
            Unknown3 = CurrentHitpoints,
            Unknown4 = -amount
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

        // Tell clients how fast we move so the client interpolates a smooth grounded run to each position
        // update (a PHYSICS actor with no ExpectedSpeed snaps between updates — the "flying" look). Only
        // re-send when the pace actually changes (chase speed vs return speed).
        SendExpectedSpeed(speed);

        // Calculate movement delta (tick rate is ~10 FPS = 0.1s per tick)
        var moveAmount = speed * 0.1f;
        if (moveAmount > dist)
            moveAmount = dist;

        var nx = dx / dist;
        var nz = dz / dist;

        // Ease Y toward the target proportionally with horizontal progress instead of snapping straight to
        // target.Y — snapping popped the model to spawn height on the first return tick and then let client
        // gravity yank it back down (a vertical stutter every time it evaded). Reaching the target exactly
        // as we arrive keeps grounded physics NPCs smooth.
        var frac = moveAmount / dist;
        var newPos = new Vector4(
            Position.X + nx * moveAmount,
            Position.Y + (target.Y - Position.Y) * frac,
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
            // Always the run state while actively moving — chase and evade are both sprints, so flipping
            // walk<->run at the old 7.0 speed cutoff just churned the animation. ExpectedSpeed above drives
            // the actual interpolation pace.
            BroadcastPositionUpdate(2);
            LastSentPosition = newPos;
        }
    }

    // Stream ExpectedSpeed to clients only when it actually changes (chase/return/stop), so a
    // PHYSICS actor interpolates a smooth grounded run without re-sending the same pace every tick.
    private void SendExpectedSpeed(float speed)
    {
        if (LastSentExpectedSpeed == speed)
            return;
        LastSentExpectedSpeed = speed;
        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(new PlayerUpdatePacketExpectedSpeed { Guid = Guid, ExpectedSpeed = speed });
    }

    // Plant the NPC: tell clients to stop predicting movement (ExpectedSpeed 0) and send one
    // idle-state position at the current spot. Used when reaching attack range or arriving home, so the
    // model stops instead of coasting past on its last streamed speed.
    private void BroadcastStop()
    {
        SendExpectedSpeed(0f);
        BroadcastPositionUpdate(0);
        LastSentPosition = Position;
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
            Hitpoints = CurrentHitpoints,
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
