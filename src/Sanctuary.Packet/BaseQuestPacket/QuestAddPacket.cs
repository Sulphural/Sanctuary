using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class QuestAddPacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 3;

    public int QuestId;
    public int TitleId;
    public int DescriptionId;
    public int HelperTextId;
    public bool MembersOnly;
    public long TimeStarted;
    public int ProfileId;
    public float CompletedPercentage;
    public int IconId;
    public bool SystemQuest;

    public bool IncludeObjective;
    public int ObjectiveId;
    public int ObjectiveNameId;
    public int ObjectiveDescriptionId;
    public int ObjectiveField2;

    public QuestAddPacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(QuestId);
        writer.Write(TitleId);
        writer.Write(DescriptionId);
        writer.Write(HelperTextId);
        writer.Write(MembersOnly);
        writer.Write(TimeStarted);
        writer.Write(ProfileId);
        writer.Write(false);
        writer.Write(CompletedPercentage);

        RewardBundleSerializer.Write(writer, 0, 0);

        if (IncludeObjective)
        {
            writer.Write(1);

            writer.Write(ObjectiveId);

            writer.Write(ObjectiveNameId);
            writer.Write(ObjectiveDescriptionId);
            writer.Write(ObjectiveField2);
            writer.Write(false);

            RewardBundleSerializer.Write(writer, 0, 0);

            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(false); writer.Write(0);
        }
        else
        {
            writer.Write(0);
        }

        writer.Write(IconId);
        writer.Write(SystemQuest);
        writer.Write(false);
        writer.Write(false);

        return writer.Buffer;
    }
}
