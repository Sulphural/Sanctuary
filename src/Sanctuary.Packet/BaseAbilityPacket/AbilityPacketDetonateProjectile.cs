using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: detonate an in-flight projectile (the impact half of a launched shot).
//
// LAYOUT RECOVERED FROM THE CLIENT, not guessed. The op36 dispatcher is FUN_00a35cc0; case 0xe calls
// deserializer a31b80, whose inner reader a2fe20 reads, in order:
//   FUN_00a2fc10   -> the [short op][short sub] header (already written by BaseAbilityPacket.Write)
//   8 bytes        -> +0x10/+0x14   (64-bit guid)
//   4 bytes        -> +0x18         (int)
//   4 bytes        -> +0x1c         (int)
//   4 bytes        -> +0x20         (float - the reader NaN-checks it, which is how floats are
//                                    distinguished from ints in these readers)
//
// The method was validated against AbilityPacketStartCasting (sub 3), where it reproduced our
// known-working layout exactly. Field NAMES are provisional: the reader gives types and order, not
// meaning. Confirm live before relying on the semantics.
public class AbilityPacketDetonateProjectile : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 14;

    public ulong Guid;        // projectile owner / target - to be confirmed
    public int Unknown;
    public int Unknown2;
    public float Unknown3;

    public AbilityPacketDetonateProjectile() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);   // [op 36][sub 14]

        writer.Write(Guid);
        writer.Write(Unknown);
        writer.Write(Unknown2);
        writer.Write(Unknown3);

        return writer.Buffer;
    }
}
