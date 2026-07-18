using System.Numerics;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Move-and-cast: a position plus the entity the cast is aimed at.
//
// LAYOUT RECOVERED FROM THE CLIENT. op36 dispatcher FUN_00a35cc0, case 6 -> deserializer a33090, which
// reads in order:
//   FUN_00a2fc10   -> the [short op][short sub] header (BaseAbilityPacket.Write already emits it)
//   FUN_008e2410   -> a loop of 4 NaN-checked floats = Vector4 (written to +0x10)
//   FUN_008dadd0   -> two dwords = one 64-bit guid
//
// NOTE the name says "Client", but this case lives in the SERVER->CLIENT ability dispatcher, so the
// client does parse it inbound. Our client has never SENT sub 6 in any recorded session (11,830 ability
// packets logged, only subs 10 and 12 ever seen), so treat the direction as unsettled until observed.
public class AbilityPacketClientMoveAndCast : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 6;

    public Vector4 Position;
    public ulong Guid;

    public AbilityPacketClientMoveAndCast() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);   // [op 36][sub 6]

        writer.Write(Position);
        writer.Write(Guid);

        return writer.Buffer;
    }
}
