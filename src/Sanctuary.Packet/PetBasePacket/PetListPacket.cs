using System;
using System.Collections.Generic;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

public class PetListPacket : PetBasePacket, ISerializablePacket
{
    public new const byte OpCode = 5;

    public List<PacketPetInfo> Pets = new List<PacketPetInfo>();

    public PetListPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Pets);

        return writer.Buffer;
    }
}
