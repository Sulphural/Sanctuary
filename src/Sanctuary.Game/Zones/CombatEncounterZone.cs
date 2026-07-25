using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Sanctuary.Core.IO;
using Sanctuary.Game.Entities;
using Sanctuary.Game.Resources.Definitions.Zones;
using Sanctuary.Packet;
using Sanctuary.Packet.Common;

namespace Sanctuary.Game.Zones;

// Shared per-mob combat state for the encounter AI (chase / attack / plant / idle / return-home).
// Subclasses extend it with their own extras (Frostfang's roamer + charge-delay + howl).
public class EncounterMobState
{
    public bool Charging;
    public float SlotAngle;
    public long NextAttackTicks;
    public Vector4 Home;    // spawn post — mobs walk back here and idle while the player is knocked down
    public bool Idling;     // true once parked at Home (broadcast the idle stop only once)
    public bool Planted;    // true once stopped in attack range — stop re-broadcasting position (attack jitter)
    public ulong TargetGuid; // who this mob is currently pursuing — see NearestLivePlayerSticky
}

// Shared base for the combat-encounter zones — the generic data-driven EncounterArenaZone
// plus the bespoke FrostfangArenaZone and TormentedSpiritsArenaZone. It owns the
// parts every combat encounter shares so a fix lands once instead of three times: the knockout-limit / fail /
// revive lifecycle. Subclasses supply the encounter id and the zone-specific ReturnHome (teardown
// + teleport), and keep their bespoke bits (Frostfang waves/Alpha, Spirits tombstones) as their own code.
// (First extraction step — the enemy AI, exit door, and win/reward flow still live in the subclasses and are
// candidates to migrate here next.)
public abstract class CombatEncounterZone : BaseZone
{
    protected CombatEncounterZone(BaseZoneDefinition zoneDefinition, IServiceProvider serviceProvider)
        : base(zoneDefinition, serviceProvider)
    {
    }

    // Knockouts before the encounter fails (retail = 5).
    protected const int KnockoutLimit = 5;

    private readonly object _knockoutLock = new();
    private readonly Dictionary<ulong, int> _knockouts = [];

    // Encounter/activity id + instance for the client encounter packets (respawn window etc.).
    protected abstract int FailEncounterId { get; }
    protected virtual int FailInstanceId => 1;

    // Short label for the knockout log line (e.g. the dungeon name).
    protected virtual string EncounterLogName => GetType().Name;

    // Combat instances give a long auto-revive FALLBACK — the client's own knockout window runs the real ~10s
    // countdown to the Revive button; this only backstops someone who never presses it.
    protected override int ReviveCooldownMs => 30000;

    // Tear the encounter down for this player and teleport them back to the overworld (zone-specific).
    protected abstract void ReturnHome(Player player);

    // Tear the encounter's client UI down for this player (ReturnHome calls this before the teleport,
    // and the "leave" chat/exit paths call it directly): mark won/lost, remove the minigame state, reset the
    // encounter data + fighting flags, clear the goals window. On a WIN, GameOver(Won=true) goes FIRST so the
    // end card the teardown triggers reads as a win; a mid-run bail keeps won=false ("TRY AGAIN!").
    public void EndEncounterForPlayer(Player player) => EndEncounterForPlayer(player, won: false);

    public void EndEncounterForPlayer(Player player, bool won)
    {
        if (won)
            player.SendTunneled(new MiniGameGameOverPacket(won: true));
        player.SendTunneled(new MiniGameStateRemovePacket());
        player.SendTunneled(PacketEncounterDataCommon.CreateDefault());
        player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = false });
        player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = false });
        player.SendTunneled(new UiObjectiveClearPacket()); // empty + hide the Goals window (op47/sub5)
        _logger.LogInformation("{label}: encounter released for {name}.", EncounterLogName, player.Name);
    }

    // ── Result cards ─────────────────────────────────────────────────────────────────────────────────
    // DO NOT gate the exit on the player closing the card. The client does NOT report the dismissal: we tried
    // routing BaseCommandPacket sub-op 42 (CommandPacketClosedMinigameEndScreen) and it never arrives for these
    // encounter cards (nor does Leave/RequestExit) — the player just sat in the instance clicking the exit door
    // with nothing happening. The card is raised as part of the teardown instead (EndEncounterForPlayer sends
    // GameOver, and the score card comes from the zone's win flow / SendFailEndScreen), and the teleport goes
    // out with it — which is the behavior that actually works in-game.

    // Forget a player's knockout tally (call on encounter start/complete so a fresh run starts at 0).
    protected void ResetKnockouts(ulong guid)
    {
        lock (_knockoutLock)
            _knockouts.Remove(guid);
    }

    // How many times this player has been knocked out this run (for the win-screen score).
    protected int KnockoutsUsed(ulong guid)
    {
        lock (_knockoutLock)
            return _knockouts.TryGetValue(guid, out var k) ? k : 0;
    }

    // Enter the encounter at full REAL max HP + mana (Stats[MaxHealth]) so the bar matches the
    // real-damage claw/bite — a fixed 2500 made it jump on the first hit. Call from OnClientIsReady.
    protected static void EnterAtFullVitals(Player player)
    {
        var startHp = player.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 2500;
        player.CurrentHitpoints = startHp;
        player.SendTunneled(new ClientUpdatePacketHitpoints { CurrentHitpoints = startHp, MaxHitpoints = startHp });
        player.SendTunneled(new ClientUpdatePacketMana { CurrentMana = 100, MaxMana = 100 });
    }

    // The victory exit door (each zone spawns it at its own spot via SpawnExitDoor, then registers it here).
    // Clicking it (routed from CommandPacketInteractRequestHandler) leaves the encounter.
    private readonly object _exitDoorLock = new();
    private Npc? _exitDoor;

    // The live victory door, or null. Subclasses read it for the visibility sweep + cleanup.
    protected Npc? ExitDoor
    {
        get { lock (_exitDoorLock) return _exitDoor; }
    }

    // Register the spawned victory door (or null to clear it on a re-run) so IsExitDoor/UseExitDoor
    // recognise clicks on it.
    protected void SetExitDoor(Npc? door)
    {
        lock (_exitDoorLock)
            _exitDoor = door;
    }

    public bool IsExitDoor(ulong guid)
    {
        lock (_exitDoorLock)
            return _exitDoor is { } door && door.Guid == guid;
    }

    public void UseExitDoor(Player player)
    {
        // WIN: the victory door tears down + teleports home. EndEncounterForPlayer(won: true) raises the
        // "You Win!" card on the way out; the player reads/closes it once they're back. (Waiting for them to
        // close it first does NOT work — the client never reports the dismissal, so they got stuck.)
        _logger.LogInformation("{label}: {name} used the exit door.", EncounterLogName, player.Name);
        ReturnHome(player);
    }

    // The minigame UI's LEAVE button (op39/sub6) and RequestExit (op41/sub109): bail out of the
    // instance back to the overworld. Same teardown + teleport as the exit door.
    public void LeaveEncounter(Player player)
    {
        if (player.Zone != this)
            return;

        _logger.LogInformation("{label}: {name} left the encounter.", EncounterLogName, player.Name);
        ReturnHome(player);
    }

    // ── Shared enemy AI ───────────────────────────────────────────────────────────────────────────────
    // Chase to an owned slot around the player, plant + attack in range, disengage to the spawn post and idle
    // while the player is knocked down. Subclasses run their own aggro/charge gating (and Frostfang its
    // roamer/waves), then call TickMobCombat for engaged mobs / TickMobReturnHome while the player is down.

    // Was 300ms (3.3Hz) - 3x coarser than the overworld CombatNpc's 10Hz tick (GatewayServer.cs), and unlike
    // that path this loop broadcasts a position update every single tick unconditionally (no distance
    // throttle), so mobs in dungeons moved in visibly larger ~1.5-1.8 unit jumps (MobChaseSpeed * old dt)
    // instead of the smaller, more frequent steps the overworld uses - the main source of dungeon-specific
    // rubber-banding/steppiness. Matching the overworld's cadence directly shrinks each step proportionally.
    protected const int TickMs = 100;
    protected const float MobYSpeed = 12f;
    protected const float MobAttackRange = 2.6f;
    protected const float MobEngageRadius = 1.9f;
    protected const int MobAttackCooldownMs = 4000;   // per-mob
    protected const int MobAttackGlobalGapMs = 1200;  // pack-wide minimum spacing
    protected const int MobAttackDamage = 150;
    protected const int MobAttackCritDamage = 300;
    protected const int MobAttackCritPercent = 10;
    protected const int MobAttackFxId = 5409;         // live melee-hit composite
    protected const int MobAttackCritFxId = 5622;

    // Chase/return speed. The pre-spawned zones drift at 5; Frostfang wolves charge at 6.
    protected virtual float MobChaseSpeed => 5f;

    // Attack spacing is per-TARGET, not pack-wide: the pack won't all bite the same player at once, but two
    // players being attacked by different mobs each get their own cadence (a single pack-wide gate made a group
    // share one bite budget so each player barely got hit). Solo (one target) is identical to the old behavior.
    // Touched only from the single per-zone AI loop, so a plain dictionary is safe.
    private readonly Dictionary<ulong, long> _lastAttackTicksByTarget = [];

    // Send a packet to every player currently in this encounter instance (per-zone one-liner).
    protected abstract void Broadcast(ISerializablePacket packet);

    protected static float MoveToward(float current, float goal, float maxDelta)
    {
        var delta = goal - current;
        if (MathF.Abs(delta) <= maxDelta)
            return goal;
        return current + MathF.Sign(delta) * maxDelta;
    }

    // The nearest player to pos that ISN'T knocked out, or null if every player
    // is down. Mobs pick their target with this each tick so the pack spreads across a group and re-targets
    // when a player falls — instead of the whole pack fixating on one player (and going home the moment that
    // one dies, ignoring everyone else still fighting).
    protected static Player? NearestLivePlayer(Vector3 pos, IReadOnlyList<Player> players)
    {
        Player? best = null;
        var best2 = float.MaxValue;
        foreach (var p in players)
        {
            if (p.IsDead)
                continue;
            var dx = p.Position.X - pos.X;
            var dz = p.Position.Z - pos.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 < best2)
            {
                best2 = d2;
                best = p;
            }
        }
        return best;
    }

    // Buffer (in units) a currently-pursued player must be beaten by before a mob switches target. Without
    // this, re-picking the literal nearest player every tick made a mob's target - and therefore its pursued
    // slot position - flip between two similarly-distant players in group combat, a visible jump each flip.
    protected const float StickyTargetSwitchBuffer = 3f;

    // Same as NearestLivePlayer, but sticks with the mob's current target (state.TargetGuid) unless it's
    // dead/gone or a genuinely closer player has shown up (more than StickyTargetSwitchBuffer units nearer).
    // Updates state.TargetGuid as a side effect. Use this for actual combat targeting; plain NearestLivePlayer
    // is still fine for non-targeting lookups (e.g. TickFleeingAlpha just needs any live player to run from).
    protected static Player? NearestLivePlayerSticky(Vector3 pos, IReadOnlyList<Player> players, EncounterMobState state)
    {
        Player? nearest = null;
        var nearestD2 = float.MaxValue;
        Player? current = null;
        var currentD2 = float.MaxValue;

        foreach (var p in players)
        {
            if (p.IsDead)
                continue;
            var dx = p.Position.X - pos.X;
            var dz = p.Position.Z - pos.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 < nearestD2)
            {
                nearestD2 = d2;
                nearest = p;
            }
            if (p.Guid == state.TargetGuid)
            {
                current = p;
                currentD2 = d2;
            }
        }

        if (current is not null)
        {
            var buffered = MathF.Sqrt(nearestD2) + StickyTargetSwitchBuffer;
            if (buffered * buffered >= currentD2)
                return current; // nothing meaningfully closer - keep pursuing the current target
        }

        state.TargetGuid = nearest?.Guid ?? 0;
        return nearest;
    }

    // Player is knocked down: disengage — amble back to the spawn post and idle there. Call this
    // (instead of TickMobCombat) for every mob while the player is down; resets Charging/Planted so the mob
    // re-engages cleanly on revive.
    protected void TickMobReturnHome(Npc mob, EncounterMobState state, float dt)
    {
        state.Charging = false;
        state.Planted = false;
        var here = new Vector3(mob.Position.X, mob.Position.Y, mob.Position.Z);
        var toHome = new Vector2(state.Home.X - here.X, state.Home.Z - here.Z);
        var distHome = toHome.Length();
        if (distHome > 0.6f)
        {
            state.Idling = false;
            var step = MathF.Min(MobChaseSpeed * dt, distHome);
            var dir = toHome / distHome;
            var ny = MoveToward(here.Y, state.Home.Y, MobYSpeed * dt);
            var np = new Vector4(here.X + dir.X * step, ny, here.Z + dir.Y * step, mob.Position.W);
            var frot = new Quaternion(dir.X, 0f, dir.Y, 0f);
            mob.UpdatePosition(np, frot);
            Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = np, Rotation = frot, State = 0, Unknown = 0 });
        }
        else if (!state.Idling)
        {
            state.Idling = true; // arrived — plant idle once (State 1 = standing)
            Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = mob.Position, Rotation = mob.Rotation, State = 1, Unknown = 0 });
        }
    }

    // Engaged-mob combat tick (player alive): converge on an owned slot around the player, plant
    // once in attack range (re-broadcasting every tick bobbed the model + fought the swing = jitter), and
    // attack on the per-mob cooldown gated by the pack-wide spacing.
    protected void TickMobCombat(Npc mob, EncounterMobState state, Player player, Vector3 playerPos, long now, float dt)
    {
        var here = new Vector3(mob.Position.X, mob.Position.Y, mob.Position.Z);
        var slot = playerPos + new Vector3(MathF.Sin(state.SlotAngle), 0f, MathF.Cos(state.SlotAngle)) * MobEngageRadius;
        var toPlayerH = new Vector2(playerPos.X - here.X, playerPos.Z - here.Z);
        var distToPlayerH = toPlayerH.Length();
        var face = distToPlayerH > 0.01f ? toPlayerH / distToPlayerH : new Vector2(0f, 1f);
        var rot = new Quaternion(face.X, 0f, face.Y, 0f);
        var newY = MoveToward(here.Y, playerPos.Y, MobYSpeed * dt);

        if (distToPlayerH > MobAttackRange)
        {
            state.Planted = false;
            var toSlot = new Vector2(slot.X - here.X, slot.Z - here.Z);
            var distToSlot = toSlot.Length();
            var step = MathF.Min(MobChaseSpeed * dt, distToSlot);
            var dir = distToSlot > 0.01f ? toSlot / distToSlot : Vector2.Zero;
            var np = new Vector4(here.X + dir.X * step, newY, here.Z + dir.Y * step, mob.Position.W);
            mob.UpdatePosition(np, rot);
            Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = np, Rotation = rot, State = 0, Unknown = 0 });
        }
        else
        {
            if (!state.Planted)
            {
                state.Planted = true;
                var np = new Vector4(here.X, newY, here.Z, mob.Position.W);
                mob.UpdatePosition(np, rot);
                Broadcast(new PlayerUpdatePacketUpdatePosition { Guid = mob.Guid, Position = np, Rotation = rot, State = 1, Unknown = 0 });
            }

            _lastAttackTicksByTarget.TryGetValue(player.Guid, out var lastAttackOnTarget);
            if (now >= state.NextAttackTicks && now - lastAttackOnTarget >= MobAttackGlobalGapMs && !player.IsDead)
            {
                state.NextAttackTicks = now + MobAttackCooldownMs;
                _lastAttackTicksByTarget[player.Guid] = now;
                PerformMobAttack(mob, player);
            }
        }
    }

    // One mob attack: real damage (knocks the player out at 0 -> the fail flow), the floating
    // number/bar + hit FX, and an explicit swing clip for boss models whose default contact event doesn't
    // animate (the Abominable Snowman).
    protected void PerformMobAttack(Npc attacker, Player player)
    {
        var maxHp = player.Stats.TryGetValue(CharacterStatId.MaxHealth, out var mh) ? mh.Int : 2500;

        // DODGE (base avoidance + Archer's Reflexes): the player evades — the AttackTargetDodged packet renders the
        // "Dodge" text + plays the attacker's swing, and the com_dodge sidestep layers on top; no damage is dealt.
        // Boss models whose default contact event doesn't animate still need their explicit swing clip.
        if (player.TryDodgeIncomingAttack(attacker.Guid))
        {
            if (CombatNpc.ExplicitAttackAnimByModel.TryGetValue(attacker.ModelId, out var missAnim))
                Broadcast(new PlayerUpdatePacketSetAnimation { Guid = attacker.Guid, AnimationId = missAnim });
            return;
        }

        var crit = Random.Shared.Next(100) < MobAttackCritPercent;
        var dmg = player.ReduceIncomingDamage(crit ? MobAttackCritDamage : MobAttackDamage); // Ninja Shrouded Armor
        player.TakeDamage(dmg);
        Broadcast(new CombatPacketAttackProcessed
        {
            AttackerGuid = attacker.Guid,
            TargetGuid = player.Guid,
            Damage = dmg,
            MaxHealth = maxHp,
            CompositeEffectId = crit ? MobAttackCritFxId : MobAttackFxId,
            CurrentHealth = player.CurrentHitpoints,
        });
        if (CombatNpc.ExplicitAttackAnimByModel.TryGetValue(attacker.ModelId, out var swingAnimId))
            Broadcast(new PlayerUpdatePacketSetAnimation { Guid = attacker.Guid, AnimationId = swingAnimId });
    }

    public override void OnPlayerKnockedOut(Player player)
    {
        if (player.Zone != this)
            return;

        int kos;
        lock (_knockoutLock)
        {
            _knockouts.TryGetValue(player.Guid, out kos);
            kos++;
            _knockouts[player.Guid] = kos;
        }

        _logger.LogInformation("{label}: {name} knocked out ({kos}/{limit}).", EncounterLogName, player.Name, kos, KnockoutLimit);

        // Drop the fighting flags either way (so sub125 shows the auto-recover version, not the overworld
        // pay/safe one).
        player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = false });
        player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = false });

        if (kos >= KnockoutLimit)
        {
            // Out of lives — FAIL. Persistent "TRY AGAIN!" end-screen (SendFailEndScreen: clears the knockdown
            // UI + Won=0 + score card), HOLD it, THEN tear down + teleport home and REVIVE so the player arrives
            // ALIVE (a fail used to strand them knocked out, which blocked firing even through a job-swap).
            // The hold is a timer because the client never reports the card being closed — see the note on
            // the result cards above.
            SendFailEndScreen(player);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(FailCardHoldMs);
                    ReturnHome(player);
                    player.Respawn();
                }
                catch (Exception ex) { _logger.LogError(ex, "Fail-return failed."); }
            });
            return;
        }

        // Non-fatal knockout — show the recover window + counter; auto-revive is the fallback.
        player.SendTunneled(new MiniGameKnockOutPacket(kos, KnockoutLimit));
        player.SendTunneled(new EncounterShowRespawnWindowPacket(FailEncounterId, FailInstanceId));
        ScheduleAutoRevive(player);
    }

    public override void OnPlayerRespawn(Player player)
    {
        // Revive with full HP + FX at the death spot (the window's Revive button revives you where you fell).
        var pos = player.DeathPosition;
        player.Respawn();

        if (player.Zone == this)
        {
            player.SendTunneled(new EncounterOverworldCombatPacket { Unknown3 = true });
            player.SendTunneled(new EncounterPacketIsFighting { InWorldCombat = true });
            player.UpdatePosition(pos, player.Rotation);
            player.SendTunneled(new ClientUpdatePacketUpdateLocation
            {
                Position = pos,
                Rotation = player.Rotation,
                Teleport = true,
            });
        }
    }
}
