using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// BasePlayerUpdatePacket (op35) sub-opcode 64 = "HudMessage" — the on-screen tutorial "floating
// dialogue box" (Darkthorne's instructions). Server -> client.
//
// WIRE FORMAT reverse-engineered (Ghidra, FUN_008d7f80): after the 4-byte header (short 35 + short 64):
//   ulong Guid1   (+0x10)  - source/speaker guid
//   ulong Guid2   (+0x18)  - target guid (the player)
//   int   StringId(+0x20)  - the message text (localization id) — best-guess field, verified in-game
//   int   Unknown1(+0x24)
//   int   Unknown2(+0x28)
//   int   Unknown3(+0x2c)
//   int   Unknown4(+0x30)
//   int   Unknown5(+0x34)
public class HudMessagePacket : BasePlayerUpdatePacket, ISerializablePacket
{
    public new const short OpCode = 64;

    public ulong Guid1;      // speaker/source
    public ulong Guid2;      // target (player)
    public int StringId;     // message text (localization id)
    public int Unknown1;
    public int Unknown2;
    public int Unknown3;
    public int Unknown4;
    public int Unknown5;

    public HudMessagePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer); // short 35 + short 64

        writer.Write(Guid1);
        writer.Write(Guid2);
        writer.Write(StringId);
        writer.Write(Unknown1);
        writer.Write(Unknown2);
        writer.Write(Unknown3);
        writer.Write(Unknown4);
        writer.Write(Unknown5);

        return writer.Buffer;
    }
}
