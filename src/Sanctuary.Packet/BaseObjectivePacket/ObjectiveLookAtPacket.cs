using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// BaseObjectivePacket (op45) sub-opcode 6 = "ObjectiveLookAt" — arms the client to complete an
// objective when the player looks at a target object (the tutorial "look at the Magic Sphere" steps).
// The client watches, then reports via ObjectiveClientComplete (45/7, client->server).
//
// WIRE FORMAT reverse-engineered (Ghidra, FUN_009b2760): after the 3-byte header (short 45 + byte 6):
//   int32  ObjectiveId (+0x0c)
//   8 bytes TargetGuid (+0x10/+0x14) - the object to look at. A sentinel (-1,-1) instead switches the
//           client to "look at a world LOCATION" and reads a Vector4 next; we use the object-guid mode.
//   int32  (+0x18)  - unknown (bone/part id on the target?); 0 works
//   int32  (+0x30)  - unknown; 0
//   int32  (+0x34)  - unknown; 0
public class ObjectiveLookAtPacket : ISerializablePacket
{
    public const short OpCode = 45;
    public const byte SubOpCode = 6;

    public int ObjectiveId;

    // The object the player must look at (guid mode). The client completes the objective on look.
    public ulong TargetGuid;

    public int Unknown;   // +0x18
    public int Unknown2;  // +0x30
    public int Unknown3;  // +0x34

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        writer.Write(OpCode);
        writer.Write(SubOpCode);

        writer.Write(ObjectiveId);
        writer.Write(TargetGuid);
        writer.Write(Unknown);
        writer.Write(Unknown2);
        writer.Write(Unknown3);

        return writer.Buffer;
    }
}
