using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Server -> client: interrupt an in-progress cast (cancels the cast bar / casting state).
//
// LAYOUT RECOVERED FROM THE CLIENT, not guessed. The op36 dispatcher is FUN_00a35cc0; case 0x12 calls
// deserializer a31b10, whose inner reader a2fdb0 reads, in order:
//   FUN_00a2fc10   -> the [short op][short sub] header (already written by BaseAbilityPacket.Write)
//   8 bytes        -> +0x10/+0x14   (64-bit guid)
//   4 bytes        -> +0x18         (int)
//
// The same recovery method was validated against AbilityPacketStartCasting (sub 3), where it reproduced
// our known-working layout exactly. Field NAMES here are still provisional - the reader gives types and
// order, not meaning - so Unknown stays Unknown until we confirm it live.
public class AbilityPacketCastInterrupt : BaseAbilityPacket, ISerializablePacket
{
    public new const short OpCode = 18;

    public ulong Guid;      // caster whose cast is being interrupted
    public int Unknown;     // reason/ability id - meaning not yet confirmed

    public AbilityPacketCastInterrupt() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);   // [op 36][sub 18]

        writer.Write(Guid);
        writer.Write(Unknown);

        return writer.Buffer;
    }
}
