using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// S2C. Client applies this to the matching pet in its OWN persistent pet store (looked up by PetId,
// the same "hash key" convention PetListPacket entries use) and - ONLY if any of the 4 stat floats
// actually differ from what it already has stored - calls its equivalent of
// "PetListDataSource/PetActiveDataSource, please refresh your view" (RE'd via decompile of the
// client's op53 receive dispatcher FUN_00b5ce60, case 3, and the field-level deserializer
// FUN_00b555a0). This is the ONLY receive-path case that triggers that refresh for an
// ALREADY-CONSTRUCTED "My Pets" panel - PetListPacket (op53/5) itself does NOT, which is why
// re-sending PetListPacket alone (WallOfDataUIEventPacketHandler.HandleShowPets) never made an
// already-open panel repaint. Real hunger/hygiene/play/mood tracking is a separate, unimplemented
// feature (see project_pet_packet_opcode_audit memory) - this is sent purely as a refresh nudge, so
// the values must differ from whatever was last sent for the client's change-check to fire.
public class PetStatusPacket : PetBasePacket, ISerializablePacket
{
    public new const byte OpCode = 3;

    public int PetId;

    public float Hunger;
    public float Hygiene;
    public float Play;
    public float Mood;

    public PetStatusPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(PetId);

        writer.Write(Hunger);
        writer.Write(Hygiene);
        writer.Write(Play);
        writer.Write(Mood);

        return writer.Buffer;
    }
}
