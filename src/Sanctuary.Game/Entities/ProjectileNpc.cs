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
    private bool _done;
    private DateTime _expireAt;

    public ProjectileNpc(IZone zone) : base(zone)
    {
    }

    public void Launch(Vector4 start, Vector4 target, float speed, int impactEffectId)
    {
        _target = target;
        _speed = speed;
        _impactEffectId = impactEffectId;
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

    // Attach a PRJ_ composite effect to the carrier so it travels with the projectile (op35/16, Guid=self).
    public void AttachEffect(int effectId)
    {
        foreach (var player in VisiblePlayers.Values)
            player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
            {
                Guid = Guid,
                CompositeEffectId = effectId,
                Position = Position,
                Clear = false,
            });
    }

    public override void UpdateEveryTick()
    {
        if (_done)
            return;

        if (DateTime.UtcNow > _expireAt)
        {
            Arrive();
            return;
        }

        var dx = _target.X - Position.X;
        var dy = _target.Y - Position.Y;
        var dz = _target.Z - Position.Z;
        var dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist < 1.5f)
        {
            Arrive();
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

    private void Arrive()
    {
        if (_done)
            return;
        _done = true;

        // Impact burst at the target point (world-positioned, not attached).
        if (_impactEffectId > 0)
            foreach (var player in VisiblePlayers.Values)
                player.SendTunneled(new PlayerUpdatePacketPlayCompositeEffect
                {
                    Guid = 0,
                    CompositeEffectId = _impactEffectId,
                    Position = _target,
                    Clear = false,
                });

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
