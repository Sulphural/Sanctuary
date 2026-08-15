using System;
using System.Collections.Generic;
using System.Numerics;

using Sanctuary.Game.Zones;
using Sanctuary.Packet;

namespace Sanctuary.Game.Entities;

// A server-authoritative TRAVELLING PROJECTILE. The client's own projectile-fly path (op36/4 -> b84190 ->
// 903180) is gated on client-side combat state (caster ProxiedCharacter+0x508) that no packet can set, so
// instead we fly a real actor: spawn an invisible carrier NPC at the caster, attach a PRJ_ composite effect
// to it (op35/16 - attached effects follow the actor), and move it straight to the target with op125
// position updates. MovementType=1 (CONTROLLER) makes the client interpolate to each sent position at
// ExpectedSpeed with NO gravity (type 2 would gravity-drop it, type 0 drops updates). On arrival it plays
// an impact effect and despawns. No client combat state required - the projectile IS a moving entity.
public sealed class ProjectileNpc : Npc
{
    private Vector4 _target;
    private float _speed;
    private int _impactEffectId;
    private ulong _impactTargetGuid;
    private int _lingerMs;
    private bool _done;
    private bool _arrived;
    private bool _seekMode;   // when true, the CLIENT drives motion (op35/59 SeekTarget); we only despawn on expiry
    private DateTime _arriveAt;
    private DateTime _expireAt;
    private DateTime _removeAt;

    // Deferred launch: the carrier exists and is configured, but stays unshown and motionless until
    // _launchAt. That's how a thrown projectile leaves the hand at the RELEASE point of the thrower's
    // wind-up instead of at the instant the animation starts. Held on the ZONE TICK rather than a timer -
    // creating/registering an entity off-thread would race the zone's own guid counter.
    private bool _pendingLaunch;
    private DateTime _launchAt;
    private float _flightSeconds;
    private readonly List<Player> _pendingViewers = [];

    // Carrier identity fields, tunable live via the `/projectile` dev command while the in-combat health-bar
    // bug is being pinned down (see Launch). Defaults are the values that are correct out of combat.
    //
    // ActiveProfile 8 is the live value captured off the Frostfang heart; it must stay NON-ZERO regardless
    // of what fixes the bar, because 0 short-circuits the client's nameplate resolver entirely and brings
    // back the ctor's default ally plate - a bar in every state rather than just in combat.
    public static int CarrierActiveProfile { get; set; } = 8;
    public static int CarrierNameId { get; set; }            // 0 = nameless; the heart uses a REAL id here

    // Sent as a real op35/28 packet in ShowTo - NOT the AddNpc field of the same name, which the client
    // discards. 1 = neutral, which is what keeps the carrier off the client's combat roster and out of the
    // ally health-bar pass. 2 (ally) is what the client assumes on its own and is what the bug looked like.
    public static int CarrierDisposition { get; set; } = 1;  // 0 hostile, 1 neutral, 2 ally

    // Sent as op35/20 in ShowTo.
    //
    // ★ TRACED IN THE CLIENT (Ghidra export, 2026-08-13) after four failed guesses. The nameplate visibility
    // gate is FUN_009d08f0: it reads ProxiedCharacter+0x6F8 (CharacterStatus) and if ANY bit of mask
    // 0x1542CE is set it runs the HIDE path (FUN_009d01f0) over every plate element - the health bar being
    // one of them - and otherwise shows them. That mask is:
    //     IsAfraid | IsAsleep | IsSilenced | IsStunned | IsKnockedOut | IsKnockedBack
    //   | IsGoingHome | IsFrozen | IsScriptedAnimation | IsPoppedUp
    // IsNonAttackable (0x1) is NOT in it, which is exactly why setting it changed nothing: it is the right
    // flag for "not a combat target" and the wrong flag for "no plate".
    //
    // IsSilenced is the bit picked out of that mask because it is inert on a carrier that never casts
    // anything. Deliberately NOT one of Asleep/Rooted/Stunned/KnockedOut/KnockedBack/Frozen/Scripted - the
    // enum's own header notes those halt the client's movement controller, which would freeze the projectile
    // mid-flight. IsNonAttackable rides along because real NPCs ship it in the 04-01 capture and it is
    // independently correct.
    //
    // If the bar somehow survives, the other inert bits in the mask are IsGoingHome (0x4000) and IsPoppedUp
    // (0x100000) - `/projectile status <n>` tries them without a rebuild.
    public static CharacterStatus CarrierStatus { get; set; } =
        CharacterStatus.IsNonAttackable | CharacterStatus.IsSilenced;

    // Let the client's native SeekMovementController fly this carrier instead of server op125 updates.
    public void EnableSeekMode() => _seekMode = true;

    public ProjectileNpc(IZone zone) : base(zone)
    {
    }

    // Play the impact burst ON this actor (auto-fades) instead of world-anchored (world effects never clean).
    public void SetImpactTarget(ulong guid) => _impactTargetGuid = guid;

    // Fired once, on the zone tick, at the moment the projectile lands - so anything the hit should DO
    // (damage, a status effect) happens when the player SEES it land rather than when it was thrown.
    // Ticked, not timer-based, so it can't outlive the zone or fire on a torn-down projectile.
    public Action? OnImpact { get; set; }

    // One-call spawn+configure+launch of a projectile from caster to target, visible to the caster and
    // everyone who can see them. trailEffId rides the invisible carrier; impactEffId plays on the target.
    // launchDelayMs holds the projectile at the muzzle for that long before it appears and flies - use it to
    // land the launch on a thrown animation's release frame. 0 = fire immediately, the original behavior.
    public static ProjectileNpc? Fire(IZone zone, Player caster, Vector4 start, Vector4 target, ulong targetGuid,
        int trailEffId, int impactEffId, float speed = 55f, int modelId = 1056, float scale = 1f, int lingerMs = 1500,
        Action? onImpact = null, int launchDelayMs = 0)
    {
        if (!zone.TryCreateProjectileNpc(out var proj))
            return null;

        proj.OnImpact = onImpact;
        proj.ModelId = modelId;
        proj.Scale = scale;
        proj.SetTrail(trailEffId);
        proj.SetImpactTarget(targetGuid);
        proj.Launch(start, target, speed, impactEffId, lingerMs);

        if (launchDelayMs > 0)
        {
            // Remember who to show it to, but show nobody yet - the tick does all of it at release time so
            // the carrier, its trail and its flight clock all start together.
            proj._pendingViewers.Add(caster);
            foreach (var viewer in caster.VisiblePlayers.Values)
                proj._pendingViewers.Add(viewer);

            proj._pendingLaunch = true;
            proj._launchAt = DateTime.UtcNow.AddMilliseconds(launchDelayMs);
            return proj;
        }

        proj.ShowTo(caster);
        foreach (var viewer in caster.VisiblePlayers.Values)
            proj.ShowTo(viewer);

        proj.AttachTrail();
        proj.GlideToTarget();
        return proj;
    }

    public void Launch(Vector4 start, Vector4 target, float speed, int impactEffectId, int lingerMs = 1500)
    {
        _target = target;
        _speed = speed;
        _impactEffectId = impactEffectId;
        _lingerMs = lingerMs;

        MovementType = 1;                    // CONTROLLER: interpolate to sent pos, no gravity
        Speed = speed;                       // baked ExpectedSpeed
        RiderGuid = 0xFFFFFFFFFFFFFFFF;      // "no rider" sentinel, else op125 is ignored
        Visible = true;

        // A projectile is not a creature: never show a nameplate or health bar on the carrier (the client
        // renders both by default for an NPC - that was the "projectile has a health bar" bug). MaxHealth=0
        // marks it non-damageable, and ActiveProfile must be NON-default so the client's nameplate resolver
        // actually runs (ActiveProfile 0 short-circuits it and keeps the ctor ally plate + bar) - matches the
        // bar-less Frostfang "heart" prop (ActiveProfile 8, ShowHealthBar false, HideNamePlate true).
        // ★ THE HEALTH-BAR-IN-COMBAT BUG (reported live 2026-08-13). These flags are enough OUT of combat,
        // but once the player is in overworld combat - op41 sub132 SetInWorldCombat + sub133 SetIsFighting,
        // sent by Player.EnterWorldCombat and held for a decay window - the client draws a bar on the carrier
        // anyway. That is exactly why the bug is ORDER-DEPENDENT: a snowball thrown on a fresh login has no
        // bar, and every projectile after the first bow shot does, arrows included.
        //
        // RESOLVED: the client had the carrier down as an ALLY and was giving it an ally's combat health bar.
        // The Disposition assignment below does NOT fix that on its own - AddNpc's Disposition is discarded
        // client-side - the real fix is the op35/28 packet ShowTo sends. This field is set anyway so the
        // server-side view of the entity matches what the client is actually told.
        HideNamePlate = true;
        ShowHealthBar = false;
        MaxHealth = 0;
        ActiveProfile = CarrierActiveProfile;
        Disposition = CarrierDisposition;
        NameId = CarrierNameId;
        IsInteractable = false;   // a targetable NPC draws the health bar - a projectile is not a target
        InteractRange = 0;

        // Position/Rotation have private setters - set them through UpdatePosition. updateZoneArea:false
        // keeps a fast mover out of the tile relevance churn (we register visibility ourselves via ShowTo).
        // Full 3D facing (yaw + pitch) so directional trail effects point AT the target including elevation,
        // instead of always lying flat (yaw-only made elevated/dropping shots look mis-oriented).
        UpdatePosition(start, FacingRotation(target.X - start.X, target.Y - start.Y, target.Z - start.Z), false);

        // Flight time = distance / speed. The client interpolates the WHOLE path in one glide (see
        // GlideToTarget), so we don't step per-tick; we just time the arrival to fire the impact.
        var dx = target.X - start.X;
        var dy = target.Y - start.Y;
        var dz = target.Z - start.Z;
        var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        var flightSec = speed > 0.1f ? dist / speed : 0.3f;
        _flightSeconds = flightSec;
        _arriveAt = DateTime.UtcNow.AddSeconds(flightSec);
        _expireAt = DateTime.UtcNow.AddSeconds(flightSec + 3); // safety despawn
    }

    // Retail-smooth motion: send ONE op125 to the destination and let the MovementType=1 CONTROLLER
    // client-side interpolate the render (and the attached trail, which rides the interpolated position)
    // there at ExpectedSpeed - a single continuous glide instead of ~10 visible steps/sec. Call AFTER
    // ShowTo (client must have the entity) and AttachTrail (so the trail is on before it moves).
    public void GlideToTarget()
    {
        if (_seekMode)
            return; // seek probe drives motion via op35/59 instead

        UpdatePosition(_target, Rotation, false);
        var packet = new PlayerUpdatePacketUpdatePosition
        {
            Guid = Guid,
            Position = _target,
            Rotation = Rotation,
            State = 1, // moving
            Unknown = 0,
        };
        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(packet);
    }

    // Register the projectile with a viewer: send AddNpc + ExpectedSpeed so the client can interpolate it.
    public void ShowTo(Player player)
    {
        VisiblePlayers.TryAdd(player.Guid, player);
        player.SendTunneled(GetAddNpcPacket());
        player.SendTunneled(new PlayerUpdatePacketExpectedSpeed { Guid = Guid, ExpectedSpeed = _speed });

        // ★ THE HEALTH-BAR-IN-COMBAT FIX. AddNpc's own Disposition int is IGNORED by the client (see
        // PlayerUpdatePacketUpdateDisposition's header): the apply takes m_nDisposition from a CLIENT-GLOBAL
        // arena flag - arena => 0 hostile, everywhere else => 2 ALLY - and the ProxiedCharacter ctor defaults
        // to 2 as well. So in the overworld this carrier was an ALLIED COMBATANT as far as the client was
        // concerned, and once the player enters world combat (op41 sub132/133) the client draws health bars
        // for allies, exactly as it does for a pet or a party member. Hence the order dependence: no combat
        // state, no ally bars, no bar on the projectile.
        //
        // op35/28 is the real per-NPC lever and has to be sent AFTER AddNpc. Neutral takes the carrier out of
        // the combat roster - the same thing the arenas already do to their DOORS, which are props with the
        // same "must not be a combat participant" problem.
        player.SendTunneled(new PlayerUpdatePacketUpdateDisposition { Guid = Guid, Disposition = CarrierDisposition });

        // ★ AND the part that actually says "this is not a combat target": CharacterStatus.IsNonAttackable.
        // The enum's own note records that REAL NPCs in the 2014-04-01 capture ship status=1, and this
        // carrier was shipping 0 - i.e. attackable - so the client had every reason to put a combat health
        // bar on it the moment combat state came up. Nothing in AddNpc carries this; it is only ever set by
        // op35/20 SetCharacterState, after the actor exists.
        player.SendTunneled(new PlayerUpdatePacketUpdateCharacterState { Guid = Guid, Status = CarrierStatus });
    }

    // The PRJ_ trail effect, ATTACHED to the flying carrier (op35/41) so it rides the projectile, then
    // removed on landing (op35/42). This is the retail design: a looping "_trail" emitter attached to the
    // moving projectile and stopped when it lands. op35/16 world-anchored puffs were a dead end (they NEVER
    // auto-clean - piled up permanently for every effect tried). The carrier is model 1056
    // invisible_cube_with_skeleton: invisible but HAS A BONE, so the attached effect renders (a boneless
    // null model shows nothing). Real prop models (fireball 1982 / arrow 793) were tried and read as
    // untextured junk on screen - the trail alone is the retail look. TagId is fixed (keyed per-actor;
    // each carrier is its own NPC).
    private int _effectId;
    private const int TrailTagId = 90001;

    public void SetTrail(int effectId) => _effectId = effectId;

    // Attach the trail to the carrier so it follows the flying model. Call after ShowTo (needs visibility).
    public void AttachTrail()
    {
        if (_effectId <= 0)
            return;
        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(new PlayerUpdatePacketAddEffectTagCompositeEffect
            {
                Guid = Guid,
                TagId = TrailTagId,
                CompositeEffectId = _effectId,
                SourceGuid = Guid,
                Unknown = 0,
                Unknown2 = 0,
            });
    }

    public override void UpdateEveryTick()
    {
        if (_done)
            return;

        // Still in the thrower's hand: nobody has been shown the carrier yet, so there is nothing to move.
        // At release, everything starts at once - show, attach the trail, restart the flight clock from NOW
        // (Launch's own clock started when the throw was requested, which is exactly the delay we held for)
        // and send the single glide.
        if (_pendingLaunch)
        {
            if (DateTime.UtcNow < _launchAt)
                return;

            _pendingLaunch = false;
            _arriveAt = DateTime.UtcNow.AddSeconds(_flightSeconds);
            _expireAt = DateTime.UtcNow.AddSeconds(_flightSeconds + 3);

            foreach (var viewer in _pendingViewers)
                ShowTo(viewer);
            _pendingViewers.Clear();

            AttachTrail();
            GlideToTarget();
            return;
        }

        // Reached the target: stop moving and LINGER (keep the carrier alive) so the attached trail plays
        // out its lifetime and fades naturally. Removing the carrier kills the attached particles instantly
        // (the "instant cut"), so we wait _lingerMs before finalizing.
        if (_arrived)
        {
            if (DateTime.UtcNow >= _removeAt)
                Finalize();
            return;
        }

        // The client is gliding the carrier along the single op125 waypoint (GlideToTarget). We just wait
        // for the computed flight time to elapse, then fire the impact. _expireAt is the safety net.
        var now = DateTime.UtcNow;
        if (now >= _arriveAt || now > _expireAt)
            ReachTarget();
    }

    // Projectile hit the target: STOP the trail emitter (op35/42) so no new particles spawn at the enemy
    // (otherwise the looping emitter keeps pooling there and the projectile "sits" on the target), play the
    // impact burst, and START the linger. We keep the carrier alive through _lingerMs so op35/42 STOPS
    // emission without the carrier-removal hard-killing the already-laid trail particles - those fade out
    // over their own lifetime. The carrier is removed only after the fade.
    private void ReachTarget()
    {
        if (_arrived)
            return;
        _arrived = true;
        _removeAt = DateTime.UtcNow.AddMilliseconds(_lingerMs);

        foreach (var player in VisiblePlayers.Values)
        {
            if (_effectId > 0)
                player.SendTunneled(new PlayerUpdatePacketRemoveEffectTagCompositeEffect
                {
                    Guid = Guid,
                    TagId = TrailTagId,
                });

            if (_impactEffectId > 0)
                player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
                {
                    // Play ON the target actor when we have one (auto-fades); else world-anchored fallback.
                    Guid = _impactTargetGuid,
                    CompositeEffectId = _impactEffectId,
                    Position = _target,
                    Clear = false,
                });
        }

        var onImpact = OnImpact;
        OnImpact = null; // one-shot: the linger keeps ticking this entity after arrival
        onImpact?.Invoke();
    }

    // Linger elapsed: remove the carrier. By now the trail has faded on its own, so nothing gets cut.
    private void Finalize()
    {
        if (_done)
            return;
        _done = true;

        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(new PlayerUpdatePacketRemovePlayerGracefully
            {
                Guid = Guid,
                Animate = false,
                Delay = 0,
                EffectDelay = 0,
                CompositeEffectId = 0,
                Duration = 0,
            });

        Dispose();
    }

    // The client's character "rotation" is NOT a quaternion - it's the facing DIRECTION packed as
    // (dirX, 0, dirZ, 0) (live-verified from player rotation: x/z track the movement direction, y=w=0). Point
    // the carrier's model along its flight path by packing the normalized horizontal direction the same way.
    private static Quaternion FacingRotation(float dx, float dy, float dz)
    {
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.0001f)
            return new Quaternion(1f, 0f, 0f, 0f);
        return new Quaternion(dx / len, 0f, dz / len, 0f);
    }
}
