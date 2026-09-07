using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class QuestObjectiveUpdatePacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 9;

    public int QuestId;
    public int ObjectiveId;
    public int CurrentCount;
    public float CompletedPercentage;
    public bool Unknown5;
    public int Unknown6;

    public QuestObjectiveUpdatePacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(QuestId);
        writer.Write(ObjectiveId);
        writer.Write(CurrentCount);
        writer.Write(CompletedPercentage);
        writer.Write(Unknown5);
        writer.Write(Unknown6);

        return writer.Buffer;
    }
}
