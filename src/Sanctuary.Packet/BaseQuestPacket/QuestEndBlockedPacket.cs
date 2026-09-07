using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class QuestEndBlockedPacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 16;

    public ulong NpcGuid;
    public int TextId;
    public int QuestId;

    public QuestEndBlockedPacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(NpcGuid);
        writer.Write(TextId);
        writer.Write(QuestId);

        return writer.Buffer;
    }
}
