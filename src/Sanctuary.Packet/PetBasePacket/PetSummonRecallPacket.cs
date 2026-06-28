using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PetSummonRecallPacket : PetBasePacket
{
    public new const byte OpCode = 4;

    public int Id;

    public PetSummonRecallPacket() : base(OpCode)
    {
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out PetSummonRecallPacket packet)
    {
        packet = new();

        var reader = new PacketReader(data);

        if (!packet.TryRead(ref reader))
            return false;

        if (!reader.TryRead(out packet.Id))
            return false;

        return true;
    }

    public override string ToString()
    {
        return $"{nameof(PetSummonRecallPacket)} ( {nameof(Id)}: {Id} )";
    }
}
