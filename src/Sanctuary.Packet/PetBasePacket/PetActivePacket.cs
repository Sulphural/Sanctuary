using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PetActivePacket : PetBasePacket, ISerializablePacket
{
    public new const byte OpCode = 9;

    public ulong OwnerGuid;
    public ulong PetGuid;

    public int CompositeEffectId;

    public PetActivePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(OwnerGuid);
        writer.Write(PetGuid);

        writer.Write(CompositeEffectId);

        return writer.Buffer;
    }
}
