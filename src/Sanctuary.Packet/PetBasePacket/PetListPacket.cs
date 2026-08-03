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

        // The client's list-wrapper deserializer (FUN_00B5BA90) reads 3 more int32 fields
        // immediately after the pet list itself (before returning) - confirmed via live IDA trace
        // 2026-08-02 (EAX/AL=0 at the deserializer's success check, tracked down to these fields
        // never being sent, causing an out-of-bytes read that fails the whole packet). Semantics
        // unknown, but their presence is required for the client to accept the packet at all.
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        return writer.Buffer;
    }
}
