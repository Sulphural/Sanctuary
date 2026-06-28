using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PetUiModePacket : PetBasePacket, ISerializablePacket
{
    public new const byte OpCode = 24;

    public bool Enabled;

    public PetUiModePacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Enabled);

        return writer.Buffer;
    }
}
