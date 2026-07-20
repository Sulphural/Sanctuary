using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// BaseObjectivePacket (op45) sub-opcode 11 = "ObjectiveFirstMovement" — arms the client to complete an
// objective on the player's next movement (the tutorial "move past the barrier" / "press WASD" steps).
// The client reports ObjectiveClientComplete (45/7) once the player moves.
//
// WIRE FORMAT reverse-engineered (Ghidra, FUN_009b28f0): after the 3-byte header (short 45 + byte 11):
//   int  ObjectiveId (+0x0c)
//   byte Flag        (+0x10)
public class ObjectiveFirstMovementPacket : ISerializablePacket
{
    public const short OpCode = 45;
    public const byte SubOpCode = 11;

    public int ObjectiveId;
    public bool Flag;

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(SubOpCode);

        writer.Write(ObjectiveId);
        writer.Write(Flag);

        return writer.Buffer;
    }
}
