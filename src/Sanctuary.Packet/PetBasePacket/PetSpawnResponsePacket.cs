using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PetSpawnResponsePacket : PetBasePacket, ISerializablePacket
{
    public new const byte OpCode = 35;

    public ulong OwnerGuid { get; set; }
    public ulong PetGuid { get; set; }
    public uint CompositeEffectId { get; set; }

    public PetSpawnResponsePacket() : base(OpCode)
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
