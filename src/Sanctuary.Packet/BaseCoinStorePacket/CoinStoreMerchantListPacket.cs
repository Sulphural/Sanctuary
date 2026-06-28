using Sanctuary.Core.IO;
using Sanctuary.Packet.Common;

namespace Sanctuary.Packet;

public class CoinStoreMerchantListPacket : BaseCoinStorePacket, ISerializablePacket
{
    public new const short OpCode = 10;

    public MerchantList MerchantList = new();

    public CoinStoreMerchantListPacket() : base(OpCode)
    {
    }

    public byte[] Serialize()
    {
        using var writer = new PacketWriter();

        Write(writer);

        MerchantList.Serialize(writer);

        return writer.Buffer;
    }
}
