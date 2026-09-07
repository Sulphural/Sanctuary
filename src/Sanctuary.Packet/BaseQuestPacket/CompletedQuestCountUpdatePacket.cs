using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class CompletedQuestCountUpdatePacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 12;

    public int Count;

    public CompletedQuestCountUpdatePacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Count);

        return writer.Buffer;
    }
}
