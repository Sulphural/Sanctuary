using System;
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
    private int _lingerMs;
    private bool _done;
    private bool _arrived;
    private DateTime _expireAt;
    private DateTime _removeAt;

    public ProjectileNpc(IZone zone) : base(zone)
    {
    }

    public void Launch(Vector4 start, Vector4 target, float speed, int impactEffectId, int lingerMs = 1500)
    {
        _target = target;
        _speed = speed;
        _impactEffectId = impactEffectId;
        _lingerMs = lingerMs;
        _expireAt = DateTime.UtcNow.AddSeconds(4); // safety despawn if it never reaches the target

        MovementType = 1;                    // CONTROLLER: interpolate to sent pos, no gravity
        Speed = speed;                       // baked ExpectedSpeed
        RiderGuid = 0xFFFFFFFFFFFFFFFF;      // "no rider" sentinel, else op125 is ignored
        Visible = true;

        // Position/Rotation have private setters - set them through UpdatePosition. updateZoneArea:false
        // keeps a fast mover out of the tile relevance churn (we register visibility ourselves via ShowTo).
        UpdatePosition(start, FacingRotation(target.X - start.X, target.Z - start.Z), false);
    }

    // Register the projectile with a viewer: send AddNpc + ExpectedSpeed so the client can interpolate it.
    public void ShowTo(Player player)
    {
        VisiblePlayers.TryAdd(player.Guid, player);
        player.SendTunneled(GetAddNpcPacket());
        player.SendTunneled(new PlayerUpdatePacketExpectedSpeed { Guid = Guid, ExpectedSpeed = _speed });
    }

    // The PRJ_ trail effect, ATTACHED to the flying carrier model (op35/41) so it rides the projectile,
    // then removed on landing (op35/42). This is the retail design: a looping "_trail" emitter attached to
    // the moving projectile and stopped when it lands. Two dead ends ruled this in: op35/16 world-anchored
    // puffs NEVER auto-clean (they piled up permanently, for every effect tried), and an invisible carrier
    // shows no attached effect - so the carrier is now a real projectile MODEL (fireball/arrow/etc.) that
    // the trail hangs on. TagId is fixed (keyed per-actor; each carrier is its own NPC).
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

        // Reached the target: stop moving and LINGER (keep the carrier alive) so the attached trail plays
        // out its lifetime and fades naturally. Removing the carrier kills the attached particles instantly
        // (the "instant cut"), so we wait _lingerMs before finalizing.
        if (_arrived)
        {
            if (DateTime.UtcNow >= _removeAt)
                Finalize();
            return;
        }

        if (DateTime.UtcNow > _expireAt)
        {
            ReachTarget();
            return;
        }

        var dx = _target.X - Position.X;
        var dy = _target.Y - Position.Y;
        var dz = _target.Z - Position.Z;
        var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 1.5f)
        {
            ReachTarget();
            return;
        }

        var step = MathF.Min(_speed * 0.1f, dist);  // ~10 ticks/sec
        var inv = step / dist;
        var newPos = new Vector4(
            Position.X + dx * inv,
            Position.Y + dy * inv,
            Position.Z + dz * inv,
            1f);

        UpdatePosition(newPos, FacingRotation(dx, dz), false);

        var packet = new PlayerUpdatePacketUpdatePosition
        {
            Guid = Guid,
            Position = Position,
            Rotation = Rotation,
            State = 1, // moving
            Unknown = 0,
        };
        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(packet);
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
                    Guid = 0,
                    CompositeEffectId = _impactEffectId,
                    Position = _target,
                    Clear = false,
                });
        }
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

    private static Quaternion FacingRotation(float dx, float dz)
    {
        var angle = MathF.Atan2(dx, dz);
        return new Quaternion(0f, MathF.Sin(angle / 2f), 0f, MathF.Cos(angle / 2f));
    }
}
