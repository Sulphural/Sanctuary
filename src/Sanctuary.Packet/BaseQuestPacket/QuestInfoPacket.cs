using System.Collections.Generic;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

public class QuestInfoPacket : BaseQuestPacket, ISerializablePacket
{
    public const int SubOpCode = 1;

    public int QuestId;
    public int TitleId;
    public int DescriptionId;
    public int HelperTextId;
    public int IconId;
    public int Unknown6;
    public bool Unknown7;

    public ulong NpcGuid;

    public int Unknown10;
    public bool Unknown11;
    public bool Unknown12;

    public RewardBundleBase RewardBundle { get; } = new();

    public QuestInfoPacket() : base(SubOpCode)
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
        writer.Write(IconId);
        writer.Write(Unknown6);
        writer.Write(Unknown7);

        RewardBundle.Serialize(writer);
        writer.Write(0);

        writer.Write(NpcGuid);
        writer.Write(Unknown10);
        writer.Write(Unknown11);
        writer.Write(Unknown12);

        return writer.Buffer;
    }
}
