using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

public class QuestRewardBundlePacket : RewardBasePacket, ISerializablePacket
{
    public const byte SubOpCode = 1;

    public RewardBundleBase RewardBundle { get; } = new();

    public int Unknown15;

    public QuestRewardBundlePacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        RewardBundle.Serialize(writer);

        writer.Write(Unknown15);

        return writer.Buffer;
    }
}
