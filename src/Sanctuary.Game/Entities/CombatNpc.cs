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
        [1907] = 1099, // Snowman Invader (snowman_present) -> com_swing; same family as the boss, and
                       // without an entry here the model's default contact event animates nothing at all
    };

    // ── Scripted march + idle roaming ────────────────────────────────────────────────────────────────
    // A one-time walk to somewhere (the Abominable Snowman's entrance, marching on the Gifting Tree).
    //
    // ★ This is deliberately NOT done by pointing SpawnPosition at the destination and letting the "walk
    // home" state carry it: SpawnPosition is also the LEASH ANCHOR. A mob whose home is 130 units away
    // aggros, takes one step, measures itself past LeashRange from home, and turns around - over and over,
    // which is exactly the "just going back and forth" bounce. While marching, the leash anchor is kept at
    // the mover's own position so aggro and pursuit behave normally the whole way in, and only when it
    // ARRIVES does the destination become its real home.
    public Vector4? MarchTarget { get; set; }

    // Movement state broadcast while this npc is moving; 2 = run (the default the client animates from).
    // Set to 0 to stop the client forcing a locomotion clip - see BroadcastPositionUpdate's note.
    public byte MovingAnimationState { get; set; } = 2;

    // Send the movement heading in the DIRECTION form the zones use when placing npcs, rather than as a
    // half-angle quaternion - required for CONTROLLER actors, which are oriented only by what we send.
    public bool DirectionStyleRotation { get; set; }

    // ★ A clip that must survive MOVEMENT. Re-sent immediately after every position broadcast, because the
    // broadcast is what makes the client resolve its own locomotion clip - so the only way to keep a custom
    // animation up is to restate it AFTER that, every time, not on an independent timer. A timer loses the
    // race whenever a position update happens to land after it, which is why 400ms and 150ms both failed.
    // 0 = normal behaviour (the client animates the npc's locomotion itself).
    public int StickyAnimationId { get; set; }

    // How often a sticky clip may be restated. Long enough that a looping animation actually gets to play,
    // short enough to beat the client's locomotion resolve.
    private const int StickyAnimationIntervalMs = 700;
    private DateTime _lastStickyAnimation = DateTime.MinValue;

    // Radians added to the computed movement heading before it is sent. 0 for everything that already faces
    // correctly; see the note where it is applied.
    public float HeadingOffset { get; set; }

    // ★ GROUND CLAMP FOR CONTROLLER ACTORS. A PHYSICS actor is stuck to the ground by the client; a
    // CONTROLLER actor is not - it sits at exactly the height the server sends, so it floats over any
    // terrain the mover's own Y easing doesn't track. Zones that spawn controller npcs supply their own
    // ground-height lookup here and the mover uses it verbatim.
    public Func<Vector4, float>? GroundHeight { get; set; }

    // ★ Suppress the streamed ExpectedSpeed. A PHYSICS actor animates its own run because the server tells
    // it how fast it is going; with the speed pinned at 0 the client has no reason to enter a locomotion
    // state, so a held clip (the Snowman Invaders' run-WITH-present ambient loop) can survive being moved.
    // Position updates still go out, so it still travels - it just glides rather than animating a run.
    public bool SuppressExpectedSpeed { get; set; }

    // RELENTLESS: the march is the point, not an entrance. A relentless marcher never abandons its
    // destination to chase - it keeps walking and swings at whatever steps into AttackRange, so the fight is
    // "stop it reaching the tree" rather than "kite it around a field". Non-relentless marchers drop the
    // march the moment they engage.
    public bool MarchRelentless { get; set; }

    // HARMLESS: pursues, faces and postures, but never deals damage. The Snow Days snowmen work this way -
    // the invaders chase and taunt you, the boss only cares about reaching the tree - so the whole event is a
    // snowball fight rather than something that can kill a player standing in a town square.
    public bool Harmless { get; set; }

    // A brief hitch in the march. Snowballs are meant to SLOW the Abominable Snowman, not stop or divert him:
    // each hit plants him for a moment and then he carries on. Nothing else interrupts a relentless march.
    private DateTime _staggerUntil = DateTime.MinValue;

    public void Stagger(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (until > _staggerUntil)
            _staggerUntil = until;
    }

    // Route every step through the zone's A* graph (the same source "Take Me There" uses) instead of only
    // when the coarse obstacle map reports the straight line blocked. Worth the cost for a long scripted
    // walk across real terrain; ordinary chases stay on the cheap straight-line test.
    public bool AlwaysRoute { get; set; }

    // Idle wander radius around SpawnPosition. 0 = stand still (the default for every existing world enemy,
    // so this changes nothing unless a spawner opts in).
    public float RoamRadius { get; set; }
    public float RoamSpeed { get; set; } = 2.5f;

    private Vector4? _roamTarget;
    private DateTime _nextRoamAt = DateTime.MinValue;

    // Cached A* route + stuck detection for obstacle-aware movement — see MoveTowards. Reset whenever
    // what we're walking to changes (new aggro target, giving up and heading home), so a stale route
    // planned toward the old destination isn't followed for up to a repath interval.
    private readonly Pathfinding.PathChaseState _pathState = new();

    public CombatNpc(IZone zone) : base(zone)
    {
        Disposition = 0; // Hostile
    }

    // Initialize combat stats from level + a difficulty TIER (classified from the enemy's name — see
    // EnemyTiers.FromName). Tiers give variety so not every enemy is the same fight: a Grunt/Initiate is a
    // quick kill, a Bruiser/Guardian is a slog, a boss is a real fight. Base HP is tankier than before (was
    // 200+level*150 = a 650-HP level-3 that a fresh player 2-shot) and per-kill XP is lower (was 50+level*25 =
    // ~125, only ~8 kills per early level).
    public void InitializeFromLevel(int level, EnemyTier tier = EnemyTier.Normal)
    {
        var m = tier.Multipliers();
        Level = level;
        MaxHitpoints = (int)((350 + level * 200) * m.Hp);
        CurrentHitpoints = MaxHitpoints;
        AttackDamage = (int)((20 + level * 15) * m.Damage);
        Defense = level * 5;
        XpReward = (int)((25 + level * 8) * m.Xp);
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
        // Look for nearby players to aggro. Checked FIRST so a marching npc still fights back when someone
        // walks up to it - the old "walks past you doing nothing on the way in" behaviour.
        var closestPlayer = FindClosestPlayer(AggroRange);

        // A relentless marcher doesn't chase and doesn't stop: it just keeps walking at its destination.
        // Players deal with it by killing it before it arrives, not by pulling it away.
        if (MarchRelentless && MarchTarget is { } relentlessMarch)
        {
            AggroTarget = null;

            // Staggered: stand still this tick, then keep going. Deliberately does NOT clear the march or
            // pick a target - being hit slows him down, it can't stop him or pull him off course.
            if (DateTime.UtcNow < _staggerUntil)
            {
                BroadcastStop();
                return;
            }

            UpdateMarch(relentlessMarch);
            return;
        }

        if (closestPlayer is not null && !closestPlayer.IsDead)
        {
            AggroTarget = closestPlayer;
            State = CombatState.Pursuing;
            _pathState.ResetPath(); // new destination - don't follow a route planned toward anything else

            // The march is an ENTRANCE, not a patrol: once something has engaged us, abandon it and anchor
            // here. Otherwise every lull in the fight sends us walking off again mid-combat, which reads as
            // the boss losing interest and pacing away. A RELENTLESS marcher is the exception - it keeps
            // going and fights on the move (see UpdateMarch).
            if (MarchTarget is not null && !MarchRelentless)
            {
                MarchTarget = null;
                SpawnPosition = Position;

                // Force-routing exists for the long scripted walk. Chasing a player a few metres away over a
                // 2000-node world graph would route via the nearest node and stagger; ordinary pursuit wants
                // the cheap straight-line steering.
                AlwaysRoute = false;
            }

            return;
        }

        if (MarchTarget is { } march)
        {
            UpdateMarch(march);
            return;
        }

        if (RoamRadius > 0f)
            UpdateRoam();
    }

    // Walk to the scripted destination, keeping the leash anchor with us so pursuit stays sane en route.
    private void UpdateMarch(Vector4 march)
    {
        if (DistanceTo(march) < 2f)
        {
            // Arrived: the destination becomes home, so from here the normal leash/return behaviour applies
            // around the place it was sent to.
            MarchTarget = null;
            SpawnPosition = march;
            _pathState.ResetPath();
            BroadcastStop();
            return;
        }

        // The leash anchor travels with us - see MarchTarget's note on why this can't just be SpawnPosition.
        SpawnPosition = Position;

        MoveTowards(march, ReturnSpeed);
    }

    // Idle wander: amble between random points near home, pausing between legs. Keeps a camp looking alive
    // instead of a row of statues, and stays well inside LeashRange so it never trips the leash.
    private void UpdateRoam()
    {
        var now = DateTime.UtcNow;

        if (_roamTarget is { } target)
        {
            if (DistanceTo(target) < 1f)
            {
                _roamTarget = null;
                _nextRoamAt = now.AddSeconds(Random.Shared.Next(2, 6)); // stand about before the next leg
                _pathState.ResetPath();
                BroadcastStop();
                return;
            }

            MoveTowards(target, RoamSpeed);
            return;
        }

        if (now < _nextRoamAt)
            return;

        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var distance = RoamRadius * (0.35f + Random.Shared.NextSingle() * 0.65f);

        _roamTarget = new Vector4(
            SpawnPosition.X + MathF.Cos(angle) * distance,
            SpawnPosition.Y,
            SpawnPosition.Z + MathF.Sin(angle) * distance,
            1f);

        _pathState.ResetPath();
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

        // NO leash check here (removed 2026-07-29, live feedback: "enemies sometimes will walk away from
        // you during a fight") - this used to disengage mid-swing the moment cumulative combat drift from
        // SpawnPosition passed LeashRange, even while the NPC was standing right next to the player actively
        // trading hits. That reads as a bug, not a design choice: if you're within AttackRange of it, it
        // isn't "escaping" anything, so there's nothing for a leash to guard against. The leash still applies
        // in UpdatePursuing (chasing but not yet engaged), which is its real job - stopping a mob from being
        // lured clear across the map before it ever lands a hit.

        // Face the target
        FaceTarget(AggroTarget.Position);

        // Auto-attack on timer. A Harmless npc keeps the menace - it stays on you, faces you, plays its
        // swing - but never actually lands anything.
        if ((DateTime.UtcNow - LastAttackTime).TotalSeconds >= AttackIntervalSeconds)
        {
            if (Harmless)
            {
                if (ExplicitAttackAnimByModel.TryGetValue(ModelId, out var harmlessSwing))
                    PlaySwingAnimation(harmlessSwing);
            }
            else
            {
                PerformAttack(AggroTarget);
            }

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
                _pathState.ResetPath(); // was walking home, now chasing - drop the return route
                return;
            }
        }

        MoveTowards(SpawnPosition, ReturnSpeed);
    }

    private void StartReturning()
    {
        AggroTarget = null;
        State = CombatState.Returning;
        _pathState.ResetPath(); // now walking home, not to the target - drop the chase route
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
                PlaySwingAnimation(missAnim);
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
            PlaySwingAnimation(swingAnimId);
    }

    // ★ NPC animation MUST be a base-animation write (PlayType 1). The op35/8 handler forks on bit0 of
    // PlayType: bit0 clear is the "play now" path, which bails unless [entity+0x1870] is non-null - and that
    // is never true for an npc. These swing packets were going out with PlayType 0 (the default), so they
    // did nothing at all: that is why the Abominable Snowman and the Snowman Invaders both hit while
    // standing frozen even though they were listed in ExplicitAttackAnimByModel.
    //
    // A base animation LOOPS until replaced, so the clip is reset to idle a beat later - the same "fake a
    // single play" trick QuestDialogue uses for its talking gesture. Safe here because an npc only swings
    // while standing in AttackRange, so nothing is being overridden mid-stride.
    private const int SwingHoldMs = 900;
    private const int IdleAnimationId = 1;

    private void PlaySwingAnimation(int animationId)
    {
        var swing = new PlayerUpdatePacketSetAnimation { Guid = Guid, AnimationId = animationId, PlayType = 1 };
        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(swing);

        var self = Guid;
        var watchers = new List<Player>(VisiblePlayers.Values);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(SwingHoldMs);
                var idle = new PlayerUpdatePacketSetAnimation { Guid = self, AnimationId = IdleAnimationId, PlayType = 1 };
                foreach (var watcher in watchers)
                    watcher.SendTunneled(idle);
            }
            catch { }
        });
    }

    // Force this enemy to engage a player without dealing damage — used when the player's own
    // ability handler applied the hit (via Npc.Health) so we skip our internal HP path but still react.
    // Also covers a player attacking from outside aggro range.
    public void AggroOnto(Player source)
    {
        if (IsDead || source.IsDead)
            return;

        // ★ A relentless marcher CANNOT be aggroed. Damage used to drag it out of the march and into a chase
        // (OnNpcDamaged -> AggroOnto -> Pursuing), which is precisely the "hit him and he stops and comes
        // after me" behaviour - the opposite of a boss that keeps walking no matter what. Hitting him
        // staggers him; it never redirects him.
        if (MarchRelentless && MarchTarget is not null)
            return;

        if (AggroTarget is null || !AggroTarget.Visible || AggroTarget.IsDead)
        {
            AggroTarget = source;
            if (State is CombatState.Idle or CombatState.Returning)
                State = CombatState.Pursuing;
        }
    }

    // Deal damage to this NPC from a player source. broadcastHitNumber=false lets a caller that already
    // sends its own per-hit feedback (the op32/7 basic-attack path) skip the 35/35 floating number here,
    // so the damage isn't drawn twice.
    public void TakeDamage(int amount, Player source, bool broadcastHitNumber = true)
    {
        if (IsDead)
            return;

        // Weapon/ability damage never touches a snowball-only enemy - see Npc.SnowballOnly. The snowball
        // route goes through ApplyDamage(fromSnowball: true) instead of here.
        if (SnowballOnly)
            return;

        CurrentHitpoints = Math.Max(0, CurrentHitpoints - amount);

        // Broadcast HP modification (floating combat number). Field mapping per the IDA-confirmed
        // wire format: Guid = ATTACKER, Guid2 = VICTIM, Unknown2 = max HP, Unknown3 = current HP
        // after the hit, Unknown4 = delta (-damage = the floating number).
        if (broadcastHitNumber)
        {
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
        }

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

        // Route around real geometry instead of walking through it. This used to be a pure straight-line
        // vector, so overworld enemies chased (and returned home) straight through cliffs and buildings —
        // the dungeon encounter AI had obstacle-aware chasing for a while before the overworld did, and
        // "Take Me There" routed players around geometry these same enemies ignored. All three now steer
        // through the one ChaseNavigator, over whichever routing source the zone has (Zone.TryFindPath
        // prefers the native ".map" graph — the overworld has one — and falls back to its WaypointGraph).
        //
        // A zone with no data at all returns the plain straight line, so this stays a no-op there —
        // identical behavior to before.
        var (dir, stepDist) = Pathfinding.ChaseNavigator.Step(
            new Vector3(Position.X, Position.Y, Position.Z),
            new Vector3(target.X, target.Y, target.Z),
            _pathState,
            Zone.NavObstacles,
            Zone.TryFindPath,
            Environment.TickCount64,
            AlwaysRoute);

        if (dir == Vector2.Zero)
            return;

        // Tell clients how fast we move so the client interpolates a smooth grounded run to each position
        // update (a PHYSICS actor with no ExpectedSpeed snaps between updates — the "flying" look). Only
        // re-send when the pace actually changes (chase speed vs return speed).
        SendExpectedSpeed(speed);

        // Calculate movement delta (tick rate is ~10 FPS = 0.1s per tick). Cap against the distance to the
        // steering target (the next waypoint when routing, else the final target) so we never overshoot it.
        var moveAmount = speed * 0.1f;
        if (moveAmount > stepDist)
            moveAmount = stepDist;

        var nx = dir.X;
        var nz = dir.Y;

        // Ease Y toward the target proportionally with horizontal progress instead of snapping straight to
        // target.Y — snapping popped the model to spawn height on the first return tick and then let client
        // gravity yank it back down (a vertical stutter every time it evaded). Reaching the target exactly
        // as we arrive keeps grounded physics NPCs smooth. Deliberately paced against the distance to the
        // FINAL target, not the current waypoint: when routing around an obstacle the waypoints are just
        // intermediate steering points, and easing Y to each of them in turn would bob the model.
        var frac = MathF.Min(1f, moveAmount / dist);
        var targetY = target.Y;
        var yFrac = frac;

        // ★ A long ROUTED walk follows real ground instead. Easing toward the destination's height is right
        // for a short chase over flat ground, but over a cross-map march it is the terrain that changes
        // underneath us: a boss walking 130 units from ground height 24 to a clearing at 27 rose steadily
        // into the air the whole way, and died floating - which then left his treasure hanging where his
        // body was. The routing graph's nodes ARE walkable ground samples, so when we're steering by them,
        // take the height from the waypoint we're actually walking to, paced against the distance to THAT
        // waypoint so we arrive at its height exactly as we reach it.
        //
        // Scoped to AlwaysRoute (the scripted march) on purpose: for an ordinary short detour around an
        // obstacle the waypoints are just steering points, and tracking each one's height in turn is the
        // model-bobbing the frac-against-final-target pacing above exists to avoid.
        if (AlwaysRoute && _pathState.CachedPath is { Count: > 0 } route)
        {
            targetY = route[Math.Min(_pathState.PathIndex, route.Count - 1)].Y;
            yFrac = MathF.Min(1f, moveAmount / MathF.Max(stepDist, 0.01f));
        }

        var newPos = new Vector4(
            Position.X + nx * moveAmount,
            Position.Y + (targetY - Position.Y) * yFrac,
            Position.Z + nz * moveAmount,
            1f
        );

        // Stick to the ground when the zone knows where it is - see GroundHeight.
        if (GroundHeight is { } ground)
            newPos.Y = ground(newPos);

        // Face the way we're actually walking, not straight at the target — matters when a blocked route
        // steps sideways or around a corner. Identical to the old behavior whenever the straight line is
        // clear, since dir is then exactly the vector to the target.
        //
        // ★ HeadingOffset exists because a CONTROLLER actor is oriented purely by what the server sends -
        // nothing derives facing from motion for it the way a physics actor does - and the convention the
        // client applies to that rotation is not the same one. Rather than guess it, the offset is tunable.
        var angle = MathF.Atan2(nx, nz) + HeadingOffset;

        // ★ TWO ROTATION CONVENTIONS EXIST IN THIS CODEBASE, and which one is right depends on who applies
        // it. A PHYSICS actor is faced by the CLIENT from its own motion, so whatever we send is ignored and
        // the half-angle quaternion below was never wrong in a way anyone could see. A CONTROLLER actor is
        // oriented purely by this value - and every place the zones set an npc's heading directly (Bruce,
        // the snowball referees, Calvin) uses the DIRECTION form `(sin h, 0, cos h, 0)`, not a quaternion.
        //
        // So controller-driven npcs get the direction form; everything else keeps the existing behaviour.
        var newRot = DirectionStyleRotation
            ? new Quaternion(MathF.Sin(angle), 0f, MathF.Cos(angle), 0f)
            : new Quaternion(0f, MathF.Sin(angle / 2f), 0f, MathF.Cos(angle / 2f));

        UpdatePosition(newPos, newRot);

        // Broadcast position update (throttled)
        var sentDx = newPos.X - LastSentPosition.X;
        var sentDz = newPos.Z - LastSentPosition.Z;
        var sentDist = MathF.Sqrt(sentDx * sentDx + sentDz * sentDz);

        // Tighter while the speed stream is suppressed: the client snaps to each update instead of
        // interpolating, so smaller, more frequent steps are the only thing that keeps it smooth.
        if (sentDist >= (SuppressExpectedSpeed ? 0.05f : 0.3f))
        {
            // Always the run state while actively moving — chase and evade are both sprints, so flipping
            // walk<->run at the old 7.0 speed cutoff just churned the animation. ExpectedSpeed above drives
            // the actual interpolation pace.
            //
            // ★ OVERRIDABLE. The movement state picks the client's locomotion clip, and it is reasserted on
            // every position broadcast - so it beats any SetAnimation, at any re-send rate. An npc that must
            // move while HOLDING a clip (the Snowman Invaders running off with a present, whose carry
            // animation is a separate slot from plain loc_run) sets this to 0 so the client stops forcing
            // its run cycle. ExpectedSpeed still drives interpolation, so it keeps moving normally.
            BroadcastPositionUpdate(MovingAnimationState);
            LastSentPosition = newPos;

            // Restate the held clip AFTER the movement update - see StickyAnimationId.
            //
            // ★ THROTTLED. Position broadcasts fire ~9x/sec at a walking pace, and re-sending a LOOPING
            // clip that often RESTARTS it every time, so the cycle never advances and the npc looks frozen
            // mid-stride. It only has to be restated often enough to win the race against the client's own
            // locomotion resolve, not on every single update.
            var sinceAnimation = DateTime.UtcNow - _lastStickyAnimation;

            if (StickyAnimationId > 0 && sinceAnimation.TotalMilliseconds >= StickyAnimationIntervalMs)
            {
                _lastStickyAnimation = DateTime.UtcNow;

                var frame = new PlayerUpdatePacketSetAnimation
                {
                    Guid = Guid,
                    AnimationId = StickyAnimationId,
                    PlayType = 1,
                };

                foreach (var player in VisiblePlayers.Values)
                    player.SendTunneled(frame);
            }
        }
    }

    // Stream ExpectedSpeed to clients only when it actually changes (chase/return/stop), so a
    // PHYSICS actor interpolates a smooth grounded run without re-sending the same pace every tick.
    private void SendExpectedSpeed(float speed)
    {
        if (SuppressExpectedSpeed)
            speed = 0f;

        if (LastSentExpectedSpeed == speed)
            return;
        LastSentExpectedSpeed = speed;
        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(new PlayerUpdatePacketExpectedSpeed { Guid = Guid, ExpectedSpeed = speed });
    }

    // Plant the NPC: tell clients to stop predicting movement (ExpectedSpeed 0) and send one
    // idle-state position at the current spot. Used when reaching attack range or arriving home, so the
    // model stops instead of coasting past on its last streamed speed.
    // Public so a scripted sequence can plant an npc on the spot (the Abominable Snowman's escape).
    public void BroadcastStop()
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
        // ★ Sending hitpoints is what MAKES the client draw a bar - so a barless enemy has to stay silent
        // here too, not just carry ShowHealthBar=false at spawn. This is why the Snowman Invaders grew a bar
        // the moment you hit one: the spawn was clean, and then the first point of damage announced it.
        if (!ShowHealthBar)
            return;

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
