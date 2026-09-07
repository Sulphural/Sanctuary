using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class QuestEndPacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 13;

    public ulong NpcGuid;
    public int QuestId;
    public int TitleId;
    public int DescriptionId;
    public float Percent = 1f;

    public int RewardCoins;
    public int RewardExperience;
    public List<RewardBundleItem> RewardItems = new();

    public QuestEndPacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(NpcGuid);
        writer.Write(QuestId);
        writer.Write(TitleId);
        writer.Write(DescriptionId);

        RewardBundleSerializer.Write(writer, RewardCoins, RewardExperience, RewardItems);

        writer.Write(Percent);

        return writer.Buffer;
    }
}
