using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class PromotionalBundleDataPacket : BasePromotionalPacket, ISerializablePacket
{
    public const short SubOpCode = 2;

    public List<BundleInfo> Bundles = [];

    public class BundleInfo : ISerializableType
    {
        public int ItemDefinitionId;
        public int ItemCount;

        public void Serialize(PacketWriter writer)
        {
            writer.Write(ItemDefinitionId);
            writer.Write(ItemCount);
        }
    }

    public PromotionalBundleDataPacket() : base(SubOpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        writer.Write(Bundles);

        return writer.Buffer;
    }
}
