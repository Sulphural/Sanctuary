using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class QuestObjectiveAddedPacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 7;

    public int QuestId;

    public int ObjectiveNameId;
    public int ObjectiveDescriptionId;
    public int ObjectiveField2;

    public QuestObjectiveAddedPacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(QuestId);

        writer.Write(ObjectiveNameId);
        writer.Write(ObjectiveDescriptionId);
        writer.Write(ObjectiveField2);
        writer.Write(false);

        RewardBundleSerializer.Write(writer, 0, 0);

        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(false);
        writer.Write(0);

        return writer.Buffer;
    }
}
