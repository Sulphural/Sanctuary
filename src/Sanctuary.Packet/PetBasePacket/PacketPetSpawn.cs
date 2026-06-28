using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PacketPetSpawn : PetBasePacket, IDeserializable<PacketPetSpawn>
{
    public new const byte OpCode = 33;

    public uint Id { get; set; }

    public PacketPetSpawn() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out PacketPetSpawn packet)
    {
        packet = null!;

        var reader = new PacketReader(data);

        if (!reader.TryRead(out short opCode) || opCode != PetBasePacket.OpCode)
            return false;

        if (!reader.TryRead(out byte subOpCode) || subOpCode != OpCode)
            return false;

        if (!reader.TryRead(out uint id))
            return false;

        packet = new PacketPetSpawn
        {
            Id = id
        };

        return true;
    }
}
