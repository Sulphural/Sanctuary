using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class QuestCompletePacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 4;

    public int QuestId;

    public QuestCompletePacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(QuestId);

        return writer.Buffer;
    }
}
