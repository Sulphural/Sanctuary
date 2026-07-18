using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

// DEV PROBE ONLY - not a real packet.
//
// Sends [op 36][sub N] followed by an arbitrary body, so we can determine a packet's EXACT body length
// empirically. The client's ability deserializer wrappers return true only when the reader hit no error
// AND the buffer was consumed exactly: too few bytes trips the error flag, too many fails the trailing
// length check. So sweeping the body size and watching the wrapper's return value (hooked with Frida)
// pins the exact size without having to read branchy decompiler output correctly.
//
// This exists for sub 4 LaunchAndLand, whose nested struct (FUN_008e8910) contains branches that defeat
// static field extraction.
public class AbilityPacketRawProbe : BaseAbilityPacket, ISerializablePacket
{
    public byte[] Body = [];

    public AbilityPacketRawProbe(short subOpCode) : base(subOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        base.Write(writer);   // [op 36][sub N]

        foreach (var b in Body)
            writer.Write(b);

        return writer.Buffer;
    }
}
