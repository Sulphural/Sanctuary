using System.Collections.Generic;

using Sanctuary.Core.IO;

namespace Sanctuary.Packet;

public class CoinStoreOpenPacket : BaseCoinStorePacket, ISerializablePacket
{
    public new const short OpCode = 7;

    public int CategoryId;
    public int ItemGroupId;
    public List<int> Items = [];

    public CoinStoreOpenPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();
        Write(writer);
        writer.Write(CategoryId);
        writer.Write(ItemGroupId);
        writer.Write(Items);
        return writer.Buffer;
    }
}
