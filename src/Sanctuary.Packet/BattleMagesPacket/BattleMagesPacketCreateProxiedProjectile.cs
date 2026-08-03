using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// op133 (BattleMages) sub 0xb (11) = CreateProxiedProjectile. RE'd 2026-07-28 via static Ghidra RTTI
// chain (BattleMagesProcessor's own vtable slot 4 = dispatcher FUN_00b728a0; case 0xb calls create
// handler FUN_00b72540 -> deserializer FUN_00b718e0). This is a SEPARATE, self-contained proxied-
// projectile system from the native-ability launcher (b84190/903180) proven unreachable from any
// server packet this session (see ProjectileNpc's notes) - BattleMagesProcessor constructs its own
// BattleMagesProxiedProjectile object directly via a factory call, with no ProxiedCharacter+0x508 or
// transient-effect-registry gate in the create handler. Nothing in that handler checks for an active
// Battle Mages session either - only a clean parse and a non-duplicate Id.
//
// Wire body (after [short 133][short 11]), field-by-field from the deserializer's read order:
//   int Id, int GuidLow, int GuidHigh, Vector4 x4, float x3, int x2  (96 bytes total).
// Field COUNT/TYPES are RE-confirmed from the deserializer's bounds checks; field SEMANTICS (which
// vector is start/end/velocity, what the trailing ints mean) are inferred from shape only, not yet
// live-verified - this is a first probe, not a finished implementation.
public class BattleMagesPacketCreateProxiedProjectile : ISerializablePacket
{
    public const short OpCode = 133;
    public const short SubOpCode = 11;

    public int Id;
    public int GuidLow;
    public int GuidHigh;
    public Vector4 Vector1;
    public Vector4 Vector2;
    public Vector4 Vector3;
    public Vector4 Vector4Field;
    public float Float1;
    public float Float2;
    public float Float3;
    public int Int1;
    public int Int2;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(SubOpCode);

        writer.Write(Id);
        writer.Write(GuidLow);
        writer.Write(GuidHigh);
        writer.Write(Vector1);
        writer.Write(Vector2);
        writer.Write(Vector3);
        writer.Write(Vector4Field);
        writer.Write(Float1);
        writer.Write(Float2);
        writer.Write(Float3);
        writer.Write(Int1);
        writer.Write(Int2);

        return writer.Buffer;
    }
}
