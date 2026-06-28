using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PetDismountResponsePacket : PetBasePacket, ISerializablePacket
{
    public new const byte OpCode = 36;

    public ulong OwnerGuid { get; set; }
    public uint CompositeEffectId { get; set; }

    public PetDismountResponsePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(OwnerGuid);
        writer.Write(CompositeEffectId);

        return writer.Buffer;
    }
}
