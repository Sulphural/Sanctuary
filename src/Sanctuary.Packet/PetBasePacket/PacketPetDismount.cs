using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PacketPetDismount : PetBasePacket
{
    public new const byte OpCode = 34;

    public PacketPetDismount() : base(OpCode)
    {
    }
}
