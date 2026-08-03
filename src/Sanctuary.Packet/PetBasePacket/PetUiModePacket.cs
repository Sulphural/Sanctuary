using System;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// Client -> server, fires repeatedly while the pet UI panel is open (observed live: mode cycles
// 0/1/2 as the player interacts with it - not a single bool as originally guessed, this was never
// verified against real traffic before). Payload is a single int32, not the bool this class
// previously assumed.
public class PetUiModePacket : PetBasePacket, ISerializablePacket, IDeserializable<PetUiModePacket>
{
    public new const byte OpCode = 24;

    public int Mode;

    public PetUiModePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Mode);

        return writer.Buffer;
    }

    // NOTE: PetBasePacketHandler already consumed the opcode + sub-opcode bytes before dispatching
    // here (same convention as PetSummonRecallPacket) - 'data' starts at the Mode field directly.
    public static bool TryDeserialize(ReadOnlySpan<byte> data, out PetUiModePacket value)
    {
        value = new PetUiModePacket();

        var reader = new PacketReader(data);

        if (!reader.TryRead(out value.Mode))
            return false;

        return true;
    }
}
