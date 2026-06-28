using System.Collections.Generic;

using Sanctuary.Core.IO;
using Sanctuary.Packet.Common.GameCommerce;

namespace Sanctuary.Packet;

public class CoinStoreMerchantBundleListPacket : BaseCoinStorePacket, ISerializablePacket
{
    public new const short OpCode = 26;

    public Dictionary<int, AppStoreBundleDefinition> Bundles = [];

    public CoinStoreMerchantBundleListPacket() : base(OpCode)
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
