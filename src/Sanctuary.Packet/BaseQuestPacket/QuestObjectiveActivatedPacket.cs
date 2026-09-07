using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class QuestObjectiveActivatedPacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 8;

    public int QuestId;
    public int ObjectiveId;
    public int RequiredCount;
    public bool Unknown2;

    public QuestObjectiveActivatedPacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(QuestId);
        writer.Write(ObjectiveId);
        writer.Write(RequiredCount);
        writer.Write(Unknown2);

        return writer.Buffer;
    }
}
