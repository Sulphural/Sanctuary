using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: stop an active aura effect on an entity.
//
// LAYOUT RECOVERED FROM THE CLIENT. op36 dispatcher FUN_00a35cc0, case 9 -> deserializer a33120, which
// reads in order:
//   FUN_00a2fc10   -> the [short op][short sub] header (BaseAbilityPacket.Write already emits it)
//   FUN_008dadd0   -> two dwords = one 64-bit guid
// That is the whole body - StopAura carries nothing but the target.
//
// Method validated against the known-good StartCasting (sub 3) and confirmed live for siblings
// CastInterrupt/DetonateProjectile, whose deserializers returned true for our bytes.
public class AbilityPacketStopAura : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 9;

    public ulong Guid;      // entity whose aura stops

    public AbilityPacketStopAura() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);   // [op 36][sub 9]

        writer.Write(Guid);

        return writer.Buffer;
    }
}
